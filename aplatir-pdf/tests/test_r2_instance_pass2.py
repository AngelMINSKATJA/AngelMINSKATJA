"""2e passe d'audit, angle « instance / démarrage / arrêt » : régressions introduites par les correctifs
(mémorisation des noms de sortie ``sorties.json``, verrou d'instance dans le profil itinérant, arrêt de
l'application). Hermétique (``tmp_path``) ; les tests d'interface exigent ``APLATIR_GUI_TESTS=1``.

Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant R3-INST-n du rapport) : retirer
le marqueur quand c'est corrigé.
"""
import json
import logging
import os
import time

import pymupdf
import pytest

tray = pytest.importorskip("aplatir_tray")
core = pytest.importorskip("aplatir_core")

GUI = pytest.mark.skipif(not os.environ.get("APLATIR_GUI_TESTS"), reason="GUI")


@pytest.fixture
def profil(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    return tmp_path


def _pdf(chemin, texte):
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    d.new_page().insert_text((72, 72), texte)
    d.save(str(chemin))
    d.close()


def _texte(chemin):
    d = pymupdf.open(str(chemin))
    try:
        return d[0].get_text().strip()
    finally:
        d.close()


# --------------------------------------------------------------------------- #
# R3-INST-1 : un partage de sortie injoignable AU DÉMARRAGE fait oublier les noms réservés
# --------------------------------------------------------------------------- #
def test_noms_reserves_conserves_si_le_dossier_de_sortie_est_injoignable_au_lancement(profil):
    of1, of2 = profil / "OF-1" / "Rapport.pdf", profil / "OF-2" / "Rapport.pdf"
    _pdf(of1, "RAPPORT OF-1")
    _pdf(of2, "RAPPORT OF-2")
    partage = profil / "partage"
    sortie = partage / "Rapports"
    sortie.mkdir(parents=True)

    # session 1 : le partage est là, l'OF-1 est traité et son nom mémorisé
    reserves = tray.charger_reserves()
    dst1 = core.chemin_sortie_unique(of1, sortie, reserves)
    assert core.aplatir_fichier(of1, sortie, dst=dst1).statut in ("ok", "copie")
    tray.sauver_reserves(reserves)

    # ouverture de session suivante : l'outil démarre (Run) AVANT que le lecteur réseau soit reconnecté
    partage.rename(profil / "partage_hors_ligne")
    reserves = tray.charger_reserves()
    (profil / "partage_hors_ligne").rename(partage)       # le partage revient ; l'outil tourne toujours

    # l'utilisateur dépose maintenant l'OF-2 (autre fichier, MÊME nom)
    dst2 = core.chemin_sortie_unique(of2, sortie, reserves)
    core.aplatir_fichier(of2, sortie, dst=dst2)
    assert dst2 != dst1, "même nom de sortie pour deux sources différentes"
    assert _texte(dst1) == "RAPPORT OF-1", "la sortie de l'OF-1 a été remplacée par celle de l'OF-2"


# --------------------------------------------------------------------------- #
# R3-INST-2 : N appels système synchrones au démarrage, avant toute interface
# --------------------------------------------------------------------------- #
def test_charger_reserves_ne_fait_pas_un_appel_systeme_par_entree(profil, monkeypatch):
    d = tray.dossier_config()
    d.mkdir(parents=True)
    entrees = {os.path.normcase(str(profil / "sortie" / f"[a]- doc{i}.pdf")): f"src{i}" for i in range(2000)}
    (d / tray.FICHIER_SORTIES).write_text(json.dumps(entrees), encoding="utf-8")
    appels = {"n": 0}
    reel = os.path.exists

    def compte(p):
        appels["n"] += 1
        return reel(p)

    monkeypatch.setattr(tray.os.path, "exists", compte)
    tray.charger_reserves()
    assert appels["n"] <= 50, f"{appels['n']} appels os.path.exists synchrones au démarrage"


# --------------------------------------------------------------------------- #
# R3-INST-3 : verrou + instance.json (port de 127.0.0.1) dans le profil ITINÉRANT
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="R3-INST-3: instance.lock et instance.json (port local + jeton, donc propres "
                   "à UNE machine) sont dans %APPDATA% (itinérant / redirigé vers un partage) : sur un 2e poste du "
                   "même profil le verrou paraît pris, le port n'existe pas, et le lancement attend ~16 s puis "
                   "démarre un doublon qui écrase l'instance.json du 1er poste")
def test_verrou_et_instance_json_sont_dans_un_dossier_propre_a_la_machine(profil, monkeypatch):
    itinerant, local = profil / "roaming", profil / "local"
    monkeypatch.setenv("APPDATA", str(itinerant))
    monkeypatch.setenv("LOCALAPPDATA", str(local))
    verrou = tray.prendre_verrou()
    serveur, _jeton = tray.ouvrir_serveur()
    try:
        trouves = sorted(p.name for p in itinerant.rglob("*") if p.is_file())
        assert not {tray.FICHIER_VERROU, tray.FICHIER_INSTANCE} & set(trouves), \
            f"état propre à la machine dans le profil itinérant : {trouves}"
    finally:
        serveur.close()
        if verrou:
            verrou.close()


# --------------------------------------------------------------------------- #
# R3-INST-4 : la boucle d'événements continue après quitter() / quitter_force sans revérifier l'état
# --------------------------------------------------------------------------- #
@pytest.fixture
def fabrique(profil, monkeypatch):
    monkeypatch.setattr(tray, "pystray", None)
    monkeypatch.setattr(tray, "regler_demarrage", lambda *a, **k: True)
    monkeypatch.setattr(tray, "demarrage_actif", lambda *a, **k: False)
    monkeypatch.setattr(tray, "ouvrir_dossier", lambda *a, **k: None)
    apps = []

    def faire(sortie=None):
        d = tray.dossier_config()
        d.mkdir(parents=True, exist_ok=True)
        (d / "config.json").write_text(json.dumps({"sortie": str(sortie or ""), "demarrage_auto": False,
                                                   "premier_plan": True, "securite": True, "astuce_vue": True}),
                                       encoding="utf-8")
        app = tray.App(cache=False, fichiers=[], serveur=None)
        apps.append(app)
        return app

    yield faire
    for a in apps:
        try:
            a.jobs.put(None)
            a.root.destroy()
        except Exception:
            pass


@GUI
def test_pas_d_evenement_traite_apres_la_fermeture(fabrique, caplog):
    app = fabrique()
    app._poster("quitter_force")
    app._poster("afficher")                 # ex. 2e lancement de la MÊME version, acquitté « OK » juste avant
    with caplog.at_level(logging.ERROR, logger=tray.ID_APP):
        app._pomper()
    erreurs = [r.getMessage() for r in caplog.records if r.levelno >= logging.ERROR]
    assert not erreurs, erreurs


@GUI
@pytest.mark.xfail(strict=True, reason="R3-INST-4: quitter_force ne revérifie pas en_cours : une demande « quit » "
                   "acquittée alors que des fichiers venaient d'être mis en file (événement « fichiers » pas encore "
                   "traité par le fil Tk) ferme l'application en abandonnant ces fichiers")
def test_quitter_force_ne_ferme_pas_pendant_un_traitement(fabrique, profil, monkeypatch):
    sortie = profil / "sortie"
    sortie.mkdir()
    src = profil / "essai.pdf"
    _pdf(src, "x")
    app = fabrique(sortie)
    monkeypatch.setattr(core, "aplatir_fichier", lambda *a, **k: time.sleep(30))     # traitement long
    app._poster("fichiers", [str(src)])     # acquitté « OK » au lancement n°1 (même version)
    app._poster("quitter_force")            # acquittée au lancement n°2 (autre version) : en_cours valait encore 0
    app._pomper()
    assert app.en_cours == 1
    assert not app._ferme, "l'application s'est fermée alors qu'un fichier venait d'être mis en file"


# --------------------------------------------------------------------------- #
# R3-INST-5 : le rafraîchissement du chemin de démarrage au lancement accepte un exe lancé depuis un .zip
# --------------------------------------------------------------------------- #
class _FauxWinreg:
    """Juste ce qu'il faut de ``winreg`` pour ``regler_demarrage`` / ``demarrage_actif`` (jamais le vrai registre)."""
    HKEY_CURRENT_USER = "HKCU"
    KEY_SET_VALUE = 2
    KEY_READ = 0x20019
    REG_SZ = 1

    class _Cle:
        def __init__(self, valeurs):
            self.valeurs = valeurs

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def __init__(self):
        self.cles = {}

    def CreateKeyEx(self, ruche, chemin, reserve=0, acces=0):
        return self._Cle(self.cles.setdefault(chemin.lower(), {}))

    def OpenKey(self, ruche, chemin, reserve=0, acces=0):
        if chemin.lower() not in self.cles:
            raise FileNotFoundError(2, "clé absente", chemin)
        return self._Cle(self.cles[chemin.lower()])

    def QueryValueEx(self, cle, nom):
        if nom not in cle.valeurs:
            raise FileNotFoundError(2, "valeur absente", nom)
        return cle.valeurs[nom]

    def SetValueEx(self, cle, nom, reserve, type_, valeur):
        cle.valeurs[nom] = (valeur, type_)

    def DeleteValue(self, cle, nom):
        if nom not in cle.valeurs:
            raise FileNotFoundError(2, "valeur absente", nom)
        del cle.valeurs[nom]


def test_le_rafraichissement_du_demarrage_n_ecrit_pas_un_chemin_temporaire(monkeypatch):
    import sys
    faux = _FauxWinreg()
    monkeypatch.setitem(sys.modules, "winreg", faux)
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    bon = r"C:\Outils\AplatirPDF\AplatirPDF.exe"
    monkeypatch.setattr(sys, "executable", bon)
    assert tray.regler_demarrage(True)                      # installation normale
    zip_temp = r"C:\Users\zoe\AppData\Local\Temp\Temp1_AplatirPDF-windows.zip\AplatirPDF.exe"
    monkeypatch.setattr(sys, "executable", zip_temp)
    tray.regler_demarrage(True, lever_veto=False)           # ce que fait App.__init__ à chaque lancement
    valeur = faux.cles[tray.CLE_RUN.lower()][tray.ID_APP][0]
    assert "Temp1_" not in valeur and "\\Temp\\" not in valeur, valeur
