"""2e passe d'audit (angle « instance unique / mise à jour ») : les correctifs de l'instance unique
(verrou + serveur à jeton + poignée de main de version) corrigent-ils vraiment, et n'en
cassent-ils pas d'autres ?

Tout est hermétique (dossier de profil dans ``tmp_path``, aucun affichage requis, ``App`` remplacée
par un faux) : ces tests tournent aussi sur un runner Windows. Les tests ``xfail(strict=True)``
démontrent un vrai défaut (identifiant R2-INST-n du rapport) : retirer le marqueur quand c'est corrigé.
"""
import json
import os
import socket
import threading
import time

import pytest

tray = pytest.importorskip("aplatir_tray")
_ANCIENNE_VERSION_ACTIVE = tray.ancienne_version_active      # la vraie (la fixture « profil » la neutralise)


# --------------------------------------------------------------------------- #
# Outils
# --------------------------------------------------------------------------- #
@pytest.fixture
def profil(tmp_path, monkeypatch):
    """Profil Windows isolé + aucune interaction avec le vrai port de l'ancienne version."""
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "ancienne_version_active", lambda: False)
    monkeypatch.setattr(tray, "activer_dpi_windows", lambda: None)
    infos: list = []
    monkeypatch.setattr(tray, "message_info", infos.append)
    avant = list(tray.log.handlers)
    yield tmp_path / "appdata" / tray.ID_APP, infos
    for h in tray.log.handlers[:]:
        if h not in avant:
            tray.log.removeHandler(h)
            h.close()


def _faux_app(monkeypatch, au_demarrage=None):
    """Remplace ``App`` : mémorise sa création (donc « une instance a démarré »)."""
    creees: list = []

    class FauxApp:
        def __init__(self, cache, fichiers, serveur, jeton=None):
            creees.append({"fichiers": list(fichiers), "serveur": serveur})
            if au_demarrage:
                au_demarrage()

        def lancer(self):
            pass

    monkeypatch.setattr(tray, "App", FauxApp)
    return creees


def _attente_courte(monkeypatch, maximum=0.4):
    """Les attentes de ``contacter_instance`` (4 s / 2 s) sont ramenées à ``maximum`` : tests rapides."""
    reel = tray.contacter_instance
    monkeypatch.setattr(tray, "contacter_instance",
                        lambda msg, dossier=None, attente=0.0: reel(msg, dossier, min(attente, maximum)))


def _servir(serveur, jeton, recus, version=None, occupe=lambda: False):
    th = threading.Thread(target=tray.servir_instance, args=(serveur, jeton, recus.append, occupe, version),
                          daemon=True)
    th.start()
    return th


# --------------------------------------------------------------------------- #
# Ce qui doit marcher (garde-fous)
# --------------------------------------------------------------------------- #
def test_deux_lancements_simultanes_un_seul_verrou_gagnant(profil):
    dossier, _ = profil
    gagnants: list = []
    barriere = threading.Barrier(6)

    def lancer():
        barriere.wait()
        v = tray.prendre_verrou()
        if v is not None:
            gagnants.append(v)

    fils = [threading.Thread(target=lancer) for _ in range(6)]
    [f.start() for f in fils]
    [f.join() for f in fils]
    try:
        assert len(gagnants) == 1
    finally:
        [v.close() for v in gagnants]


def test_verrou_relache_a_la_fermeture_du_fichier(profil):
    v = tray.prendre_verrou()
    assert v is not None and tray.prendre_verrou() is None
    v.close()                       # = mort du processus : le verrou doit partir avec le descripteur
    v2 = tray.prendre_verrou()
    assert v2 is not None
    v2.close()


def test_instance_json_est_remplace_en_une_seule_ecriture(profil):
    dossier, _ = profil
    s1, j1 = tray.ouvrir_serveur()
    s2, j2 = tray.ouvrir_serveur()          # ex. instance « de secours » : l'ancien contenu est remplacé entier
    try:
        info = json.loads((dossier / tray.FICHIER_INSTANCE).read_text(encoding="utf-8"))
        assert info["jeton"] == j2 and info["port"] == s2.getsockname()[1]
        assert not (dossier / (tray.FICHIER_INSTANCE + ".tmp")).exists()
    finally:
        s1.close()
        s2.close()


# --------------------------------------------------------------------------- #
# R2-INST-1 : le « continuer sans verrou » est définitif (aucun nouvel essai)
# --------------------------------------------------------------------------- #
def test_main_reprend_le_verrou_qui_se_libere_pendant_la_negociation(profil, monkeypatch):
    """Cas réel : l'instance précédente est en train de se fermer (instance.json déjà retiré, verrou encore
    tenu quelques instants) ; le verrou d'un processus mort peut aussi tarder à être libéré par Windows
    (doc. LockFile). La nouvelle instance doit finir par TENIR le verrou, pas tourner sans."""
    _attente_courte(monkeypatch)
    detenteur = tray.prendre_verrou()
    libere = threading.Event()

    def liberer():
        time.sleep(0.8)
        detenteur.close()
        libere.set()

    threading.Thread(target=liberer, daemon=True).start()
    verdict: list = []

    def au_demarrage():
        libere.wait(5)
        autre = tray.prendre_verrou()           # None = main() tient le verrou ; sinon : il tourne sans verrou
        verdict.append(autre is None)
        if autre is not None:
            autre.close()

    creees = _faux_app(monkeypatch, au_demarrage)
    assert tray.main([]) == 0
    for c in creees:
        c["serveur"] and c["serveur"].close()
    assert verdict == [True], "la nouvelle instance tourne sans le verrou : la prochaine en démarrera une 3e"


def test_main_perdant_de_la_course_au_verrou_recontacte_la_gagnante(profil, monkeypatch):
    """Ancienne version A en cours + 2 lancements de la nouvelle version : le 1er prend la place (verrou +
    serveur de la nouvelle version), le 2e a déjà reçu « AUTRE » de A mais A a disparu quand il envoie
    « quit » (réponse None). Il doit alors retomber sur la gagnante (« OK » puis sortie), pas démarrer."""
    monkeypatch.setattr(tray, "VERSION", "9-nouvelle")
    gagnante = tray.prendre_verrou()
    serveur, jeton = tray.ouvrir_serveur()
    recus: list = []
    _servir(serveur, jeton, recus, version="9-nouvelle")
    reel = tray.contacter_instance
    scenario = iter(["AUTRE", None])            # réponses de l'ancienne instance : « autre version », puis plus rien

    def contacter(msg, dossier=None, attente=0.0):
        try:
            return next(scenario)
        except StopIteration:
            return reel(msg, dossier, min(attente, 1.0))

    monkeypatch.setattr(tray, "contacter_instance", contacter)
    creees = _faux_app(monkeypatch)
    try:
        code = tray.main([])
    finally:
        serveur.close()
        gagnante.close()
    assert code == 0
    assert creees == [], "un doublon a démarré à côté de l'instance gagnante (2 icônes, 2 fenêtres)"
    assert recus == [{"cmd": "show"}]


# --------------------------------------------------------------------------- #
# R2-INST-2 : message livré deux fois si le serveur démarre lentement
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="R2-INST-2: le client réessaie sur une NOUVELLE connexion après un délai "
                   "dépassé, et le serveur traite aussi la 1re : les fichiers sont reçus deux fois")
def test_message_relance_apres_delai_n_est_livre_qu_une_fois(profil, monkeypatch):
    """Le serveur est publié (instance.json) avant la fin de la construction de la fenêtre : si celle-ci est
    lente (antivirus, 1er lancement), le client abandonne la 1re connexion mais ses octets restent dans la
    file d'attente d'écoute ; le serveur, une fois démarré, les traite EN PLUS de la nouvelle tentative."""
    reel = socket.create_connection
    monkeypatch.setattr(tray.socket, "create_connection",
                        lambda adresse, timeout=None: reel(adresse, timeout=0.4))     # « 3 s » ramenées à 0.4 s
    serveur, jeton = tray.ouvrir_serveur()
    recus: list = []
    threading.Timer(0.8, lambda: _servir(serveur, jeton, recus)).start()               # fenêtre lente à construire
    try:
        rep = tray.contacter_instance({"cmd": "files", "paths": ["C:\\x\\a.pdf"]}, attente=1.6)
        time.sleep(0.8)
    finally:
        serveur.close()
    assert rep == "OK"
    assert recus == [{"cmd": "files", "paths": ["C:\\x\\a.pdf"]}], recus      # reçu 2 fois = PDF traité 2 fois


# --------------------------------------------------------------------------- #
# R2-INST-3 : publication d'instance.json sans nouvelle tentative
# --------------------------------------------------------------------------- #
def test_ouvrir_serveur_survit_a_un_refus_passager_de_os_replace(profil, monkeypatch):
    dossier, _ = profil
    reel = os.replace
    appels = {"n": 0}

    def replace(src, dst, *a, **k):
        if str(dst).endswith(tray.FICHIER_INSTANCE):
            appels["n"] += 1
            if appels["n"] == 1:
                raise PermissionError(13, "Accès refusé (analyse antivirus du fichier tout juste écrit)")
        return reel(src, dst, *a, **k)

    monkeypatch.setattr(tray.os, "replace", replace)
    serveur, jeton = tray.ouvrir_serveur()          # lève PermissionError : main() lance alors l'App SANS serveur
    serveur.close()
    assert (dossier / tray.FICHIER_INSTANCE).exists()


# --------------------------------------------------------------------------- #
# R2-INST-4 : une ancienne exe ÉCRASE une exe plus récente
# --------------------------------------------------------------------------- #
def test_une_exe_plus_ancienne_ne_remplace_pas_l_instance_plus_recente(profil, monkeypatch):
    monkeypatch.setattr(tray, "VERSION", "9-recente")           # l'instance qui tourne est la récente
    verrou = tray.prendre_verrou()
    serveur, jeton = tray.ouvrir_serveur()                      # publie sa version dans instance.json
    recus: list = []

    def poster(msg):
        recus.append(msg)
        if msg.get("cmd") == "quit":
            verrou.close()

    threading.Thread(target=tray.servir_instance, args=(serveur, jeton, poster, lambda: False, "9-recente"),
                     daemon=True).start()
    monkeypatch.setattr(tray, "VERSION", "3-ancienne")          # l'exe lancée ensuite est plus ancienne
    _attente_courte(monkeypatch, 1.0)
    creees = _faux_app(monkeypatch)
    infos: list = []
    monkeypatch.setattr(tray, "message_info", infos.append)
    try:
        tray.main([])
    finally:
        serveur.close()
        if not verrou.closed:
            verrou.close()
    assert len(infos) == 1 and "plus récente" in infos[0]
    assert {"cmd": "quit"} not in recus, "l'instance récente a reçu l'ordre de se fermer par une exe plus ancienne"
    assert creees == []


# --------------------------------------------------------------------------- #
# R2-INST-5 : détection de l'ancienne version = « commence par OK »
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="R2-INST-5: un service étranger qui répond « OK... » sur le port 47653 "
                   "empêche à jamais l'outil de démarrer")
def test_service_etranger_repondant_ok_n_est_pas_l_ancienne_version(monkeypatch):
    """La 1re version répondait exactement « OK » puis fermait. ``startswith(b"OK")`` prend pour elle
    n'importe quel service local qui répond « OK ... » : main() affiche alors « fermez l'ancienne version »
    à chaque lancement, sans rien à fermer."""
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    s.listen(5)

    def servir():
        c, _ = s.accept()
        with c:
            c.recv(64)
            c.sendall(b"OK ready (agent d'inventaire)\r\n")

    threading.Thread(target=servir, daemon=True).start()
    monkeypatch.setattr(tray, "PORT_ANCIEN", s.getsockname()[1])
    try:
        assert _ANCIENNE_VERSION_ACTIVE() is False
    finally:
        s.close()
