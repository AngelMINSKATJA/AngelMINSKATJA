"""Interface (aplatir_tray.App) pilotée sans utilisateur : démarrage, fermeture, dépôt de fichiers,
compteurs, quitter, protocole d'instance unique.

Les tests d'interface exigent un affichage (Windows, ou ``xvfb-run -a`` sous Linux) :
    APLATIR_GUI_TESTS=1 xvfb-run -a python -m pytest tests/test_gui_app.py
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant GUI-n du rapport).
"""
import json
import os
import pathlib
import socket
import threading
import time
from types import SimpleNamespace

import pymupdf
import pytest

tray = pytest.importorskip("aplatir_tray")
import aplatir_core as core  # noqa: E402

GUI = pytest.mark.skipif(not os.environ.get("APLATIR_GUI_TESTS"), reason="GUI")

# Un objet Tk détruit puis ramassé par le GC depuis un AUTRE fil (le fil de travail d'une App précédente)
# bloque ~1 s par variable (« main thread is not in main loop ») ou fait abandonner Python : on neutralise.
pytestmark = pytest.mark.filterwarnings("ignore::pytest.PytestUnraisableExceptionWarning")


@pytest.fixture(autouse=True)
def _gc_sur_le_fil_principal_seulement():
    import gc
    gc.disable()
    yield
    gc.enable()
    gc.collect()


# --------------------------------------------------------------------------- #
# Outils
# --------------------------------------------------------------------------- #
def _pdf(chemin, texte="x", tampon=True):
    chemin = pathlib.Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), texte)
    if tampon:
        p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


class FauxIcone:
    """Remplace pystray.Icon : mémorise les appels, ne crée aucune fenêtre."""
    def __init__(self, *a, **k):
        self.menu = a[3] if len(a) > 3 else k.get("menu")
        self.appels = []

    def run_detached(self, *a):
        self.appels.append("run_detached")

    def stop(self):
        self.appels.append("stop")

    def update_menu(self):
        self.appels.append("update_menu")

    def notify(self, message, titre=None):
        self.appels.append(("notify", message))


class FauxMenu:
    SEPARATOR = object()

    def __init__(self, *items):
        self.items = items


class FauxItem:
    def __init__(self, texte, action, **kw):
        self.texte, self.action, self.kw = texte, action, kw


def _faux_pystray():
    return SimpleNamespace(Icon=FauxIcone, Menu=FauxMenu, MenuItem=FauxItem)


@pytest.fixture
def fabrique(tmp_path, monkeypatch):
    """fabrique(cfg=None, cache=False, fichiers=(), icone=False) -> App prête à piloter."""
    apps = []
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "pystray", None)
    # ne jamais toucher au registre / ouvrir de fenêtres de l'explorateur
    monkeypatch.setattr(tray, "regler_demarrage", lambda *a, **k: True)
    monkeypatch.setattr(tray, "demarrage_actif", lambda *a, **k: False)
    monkeypatch.setattr(tray, "ouvrir_dossier", lambda *a, **k: None)

    def faire(cfg=None, cache=False, fichiers=(), icone=False, serveur=None):
        if cfg is not None:
            d = tmp_path / "appdata" / tray.ID_APP
            d.mkdir(parents=True, exist_ok=True)
            (d / "config.json").write_text(json.dumps(cfg), encoding="utf-8")
        if icone:
            monkeypatch.setattr(tray, "pystray", _faux_pystray())
        app = tray.App(cache=cache, fichiers=list(fichiers), serveur=serveur)
        apps.append(app)
        return app

    yield faire
    for a in apps:
        try:
            a.jobs.put(None)
            a.root.destroy()
        except Exception:
            pass


def _pomper(app, condition, delai=20.0):
    fin = time.time() + delai
    while time.time() < fin:
        app.root.update()
        if condition():
            return True
        time.sleep(0.005)
    return False


def _detruite(app):
    import tkinter
    try:
        app.root.winfo_exists()
    except tkinter.TclError:
        return True
    return False


def _lignes(app):
    return [app.arbre.item(i, "values") for i in app.arbre.get_children()]


# --------------------------------------------------------------------------- #
# Configuration (sans interface)
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("contenu", [
    "{pas du json", "", "[1, 2]", "null", '"texte"',
    json.dumps({"sortie": 5, "demarrage_auto": "oui", "premier_plan": 0, "securite": None}),
    json.dumps({"sortie": {"a": 1}}),
])
def test_config_illisible_ou_mal_typee_retombe_sur_les_defauts(tmp_path, contenu):
    chemin = tmp_path / "config.json"
    chemin.write_text(contenu, encoding="utf-8")
    c = tray.Config(chemin)
    assert c.charger() is True                      # le fichier existe : pas un premier lancement
    assert c.valeurs == tray.DEFAUTS


def test_config_absente_est_un_premier_lancement(tmp_path):
    c = tray.Config(tmp_path / "rien" / "config.json")
    assert c.charger() is False and c.valeurs == tray.DEFAUTS


def test_config_aller_retour_et_cles_inconnues(tmp_path):
    chemin = tmp_path / "sous" / "config.json"
    c = tray.Config(chemin)
    c["sortie"] = str(tmp_path / "é é")
    c2 = tray.Config(chemin)
    assert c2.charger() and c2["sortie"] == str(tmp_path / "é é")
    chemin.write_text(json.dumps({"sortie": "C:/x", "inconnue": 1}), encoding="utf-8")
    c3 = tray.Config(chemin)
    c3.charger()
    assert c3.valeurs == {**tray.DEFAUTS, "sortie": "C:/x"}


def test_config_non_enregistrable_ne_leve_pas(tmp_path):
    fichier = tmp_path / "unfichier"
    fichier.write_text("x")
    c = tray.Config(fichier / "config.json")      # le « dossier » parent est un fichier
    c["sortie"] = "x"                              # ne doit pas lever
    assert c["sortie"] == "x"


# --------------------------------------------------------------------------- #
# Découpage de la liste de fichiers fournie par tkdnd (liste Tcl)
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("donnee, attendu", [
    ("{C:/a b/x.pdf} C:/y.pdf {C:/é/z.pdf}", ["C:/a b/x.pdf", "C:/y.pdf", "C:/é/z.pdf"]),
    ("{//srv/part/dossier OF/x.pdf} //srv/part/y.pdf", ["//srv/part/dossier OF/x.pdf", "//srv/part/y.pdf"]),
    (r"C:/a\{b/x.pdf C:/c\}d.pdf", ["C:/a{b/x.pdf", "C:/c}d.pdf"]),
    ("{C:/a{b}c/x.pdf}", ["C:/a{b}c/x.pdf"]),
    ("C:/\U0001F4C4rapport.pdf", ["C:/\U0001F4C4rapport.pdf"]),
    ("{C:/a;b/x.pdf} {C:/a[1]/x.pdf} {C:/a$b/x.pdf}", ["C:/a;b/x.pdf", "C:/a[1]/x.pdf", "C:/a$b/x.pdf"]),
])
def test_decoupage_de_la_liste_deposee(donnee, attendu):
    import tkinter
    assert list(tkinter.Tcl().splitlist(donnee)) == attendu


# --------------------------------------------------------------------------- #
# Démarrage / fenêtre / zone de notification
# --------------------------------------------------------------------------- #
@GUI
def test_premier_lancement_affiche_la_fenetre_meme_avec_tray(fabrique, tmp_path):
    app = fabrique(cfg=None, cache=True, icone=True)
    app.root.update()
    assert app.visible and app.root.state() == "normal"
    assert (tmp_path / "appdata" / tray.ID_APP / "config.json").exists()   # créée => plus « premier »


@GUI
def test_demarrage_tray_cache_la_fenetre_puis_le_clic_la_remontre(fabrique):
    app = fabrique(cfg={"sortie": ""}, cache=True, icone=True)
    app.root.update()
    assert not app.visible and app.root.state() == "withdrawn"
    app._poster("afficher")                       # ce que fait le clic sur l'icône
    assert _pomper(app, lambda: app.visible)
    assert app.root.state() == "normal"


@GUI
def test_tray_sans_icone_disponible_montre_la_fenetre(fabrique):
    app = fabrique(cfg={"sortie": ""}, cache=True, icone=False)      # pystray indisponible
    app.root.update()
    assert app.visible and app.root.state() == "normal"


@GUI
def test_la_croix_cache_quand_l_icone_existe(fabrique):
    app = fabrique(cfg={"sortie": ""}, icone=True)
    app.root.update()
    app.fermer_fenetre()
    assert not app.visible and app.root.state() == "withdrawn"
    assert "stop" not in app.icone.appels
    app._poster("afficher")
    assert _pomper(app, lambda: app.visible)


@GUI
def test_la_croix_quitte_sans_icone(fabrique):
    app = fabrique(cfg={"sortie": ""}, icone=False)
    app.root.update()
    app.fermer_fenetre()
    assert _detruite(app)


@GUI
def test_evenement_afficher_apres_masquer_repete(fabrique):
    app = fabrique(cfg={"sortie": ""}, icone=True)
    for _ in range(3):
        app.masquer()
        app._poster("afficher")
        assert _pomper(app, lambda: app.visible)
        assert app.root.state() == "normal"


# --------------------------------------------------------------------------- #
# ajouter() : dossier de sortie
# --------------------------------------------------------------------------- #
@GUI
def test_ajouter_sans_dossier_de_sortie_demande_puis_traite(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": ""})
    sortie = tmp_path / "choisie"
    demandes = []
    monkeypatch.setattr(tray.filedialog, "askdirectory", lambda **k: demandes.append(k) or str(sortie))
    src = _pdf(tmp_path / "in" / "a.pdf")
    app.ajouter([str(src)])
    assert len(demandes) == 1 and app.cfg["sortie"] == str(sortie)
    assert _pomper(app, lambda: app.en_cours == 0)
    assert (sortie / "[a]- a.pdf").exists()


@GUI
@pytest.mark.parametrize("retour", ["", None, ()])
def test_ajouter_sans_dossier_de_sortie_annule_ne_traite_rien(fabrique, tmp_path, monkeypatch, retour):
    app = fabrique(cfg={"sortie": ""})
    monkeypatch.setattr(tray.filedialog, "askdirectory", lambda **k: retour)
    src = _pdf(tmp_path / "in" / "a.pdf")
    app.ajouter([str(src)])
    assert app.en_cours == 0 and app.jobs.empty()
    assert "rien n'a été traité" in app.var_statut.get()
    assert _lignes(app) == []


@GUI
def test_ajouter_dossier_de_sortie_blanc_redemande(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": "   "})
    monkeypatch.setattr(tray.filedialog, "askdirectory", lambda **k: "")
    app.ajouter([str(_pdf(tmp_path / "a.pdf"))])
    assert app.en_cours == 0


@GUI
def test_sortie_qui_est_un_fichier_donne_une_erreur_par_ligne(fabrique, tmp_path):
    cible = tmp_path / "pas_un_dossier"
    cible.write_text("x")
    app = fabrique(cfg={"sortie": str(cible)})
    app.ajouter([str(_pdf(tmp_path / "in" / "a.pdf")), str(_pdf(tmp_path / "in" / "b.pdf"))])
    assert _pomper(app, lambda: app.en_cours == 0)
    assert [v[1] for v in _lignes(app)] == ["✖ Erreur", "✖ Erreur"]
    assert app.stats["erreur"] == 2 and "2 erreur(s)" in app.var_statut.get()
    assert cible.read_text() == "x"                # le fichier n'a pas été touché


@GUI
def test_sortie_absente_est_creee(fabrique, tmp_path):
    sortie = tmp_path / "n" / "existe" / "pas"
    app = fabrique(cfg={"sortie": str(sortie)})
    app.ajouter([str(_pdf(tmp_path / "in" / "a.pdf"))])
    assert _pomper(app, lambda: app.en_cours == 0)
    assert (sortie / "[a]- a.pdf").exists()


# --------------------------------------------------------------------------- #
# Lots, compteurs, _fin
# --------------------------------------------------------------------------- #
@GUI
def test_lot_de_60_fichiers_compteurs_et_reactivite(fabrique, tmp_path):
    sortie = tmp_path / "out"
    app = fabrique(cfg={"sortie": str(sortie)})
    fichiers = [str(_pdf(tmp_path / "in" / f"f{i:03}.pdf", f"doc{i}", tampon=(i % 2 == 0))) for i in range(60)]
    (tmp_path / "in" / "casse.pdf").write_bytes(b"pas un pdf")
    fichiers.append(str(tmp_path / "in" / "casse.pdf"))
    t0 = time.time()
    app.ajouter(fichiers)
    assert time.time() - t0 < 2.0                  # ajouter() ne traite rien lui-même
    assert app.en_cours == 61
    assert _pomper(app, lambda: app.en_cours == 0, 60)
    assert app.stats == {"ok": 30, "copie": 30, "image": 0, "alerte": 0, "erreur": 1, "ignore": 0}   # 30 sans tampon : copiés
    assert app.var_statut.get() == "Terminé : 30 aplati(s), 30 copié(s) (rien à aplatir), 1 erreur(s)."
    assert len(list(sortie.glob("[[]a[]]- *.pdf"))) == 60
    assert len(_lignes(app)) == 61 and not any("attente" in str(app.arbre.item(i, "tags"))
                                                 for i in app.arbre.get_children())


@GUI
def test_depot_pendant_un_lot_cumule_les_compteurs_puis_reinitialise(fabrique, tmp_path, monkeypatch):
    sortie = tmp_path / "out"
    app = fabrique(cfg={"sortie": str(sortie)})
    portes = threading.Event()
    reel = core.aplatir_fichier

    def lent(src, dossier, **k):
        portes.wait(10)
        return reel(src, dossier, **k)
    monkeypatch.setattr(core, "aplatir_fichier", lent)
    app.ajouter([str(_pdf(tmp_path / "in" / "a.pdf"))])
    app.ajouter([str(_pdf(tmp_path / "in" / "b.pdf"))])      # arrive pendant le premier lot
    assert app.en_cours == 2
    portes.set()
    assert _pomper(app, lambda: app.en_cours == 0)
    assert app.stats["ok"] == 2 and "2 aplati(s)" in app.var_statut.get()
    app.ajouter([str(_pdf(tmp_path / "in" / "c.pdf"))])      # nouveau lot : on repart de zéro
    assert _pomper(app, lambda: app.en_cours == 0)
    assert app.stats["ok"] == 1 and "1 aplati(s)" in app.var_statut.get()


@GUI
def test_exception_dans_le_worker_est_une_ligne_erreur_et_le_worker_continue(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": str(tmp_path / "out")})
    reel = core.aplatir_fichier
    n = {"i": 0}

    def capricieux(src, dossier, **k):
        n["i"] += 1
        if n["i"] == 1:
            raise RuntimeError("boum")
        return reel(src, dossier, **k)
    monkeypatch.setattr(core, "aplatir_fichier", capricieux)
    app.ajouter([str(_pdf(tmp_path / "in" / "a.pdf")), str(_pdf(tmp_path / "in" / "b.pdf"))])
    assert _pomper(app, lambda: app.en_cours == 0)
    lignes = _lignes(app)
    assert lignes[0][1] == "✖ Erreur" and "RuntimeError : boum" in lignes[0][2]
    assert lignes[1][1] == "✔ Aplati"
    assert app.stats["erreur"] == 1 and app.stats["ok"] == 1


@GUI
@pytest.mark.parametrize("statut, dst, texte, tag, cle", [
    ("ok", True, "✔ Aplati", "ok", "ok"),
    ("copie", True, "✔ Copié", "ok", "copie"),
    ("securite", True, "⚠ Aplati (image)", "alerte", "image"),
    ("alerte", True, "⚠ À vérifier", "alerte", "alerte"),
    ("ignore", False, "— Ignoré", "ignore", "ignore"),
    ("erreur", False, "✖ Erreur", "erreur", "erreur"),
    ("inconnu", False, "✖ Erreur", "erreur", "erreur"),
])
def test_fin_pour_chaque_statut(fabrique, tmp_path, statut, dst, texte, tag, cle):
    app = fabrique(cfg={"sortie": str(tmp_path)})
    iid = app.arbre.insert("", "end", values=("x.pdf", "⏳ En attente", ""), tags=("attente",))
    app.en_cours = 1
    res = core.Resultat(src=tmp_path / "x.pdf", dst=(tmp_path / "[a]- x.pdf") if dst else None,
                        statut=statut, message="msg")
    app._fin(iid, res)
    v = app.arbre.item(iid, "values")
    assert v[1] == texte and tag in app.arbre.item(iid, "tags")
    assert "→" not in v[2]                  # nom de sortie habituel (préfixe + nom) : pas de flèche
    assert app.stats[cle] == 1 and app.en_cours == 0
    assert app.var_statut.get().startswith("Terminé")


@GUI
def test_fin_sur_ligne_supprimee_ne_leve_pas(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path)})
    app.en_cours = 1
    app._fin("I999", core.Resultat(src=tmp_path / "x.pdf", statut="ok", message="m"))
    assert app.en_cours == 0 and app.stats["ok"] == 1


# --------------------------------------------------------------------------- #
# Dépôt (tkdnd) : analyse de event.data
# --------------------------------------------------------------------------- #
@GUI
def test_on_drop_decoupe_la_liste_tcl_et_rend_l_action(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path / "out")})
    a = _pdf(tmp_path / "in" / "un dossier é" / "a b.pdf")
    b = _pdf(tmp_path / "in" / "b.pdf")
    data = "{%s} %s" % (a.as_posix(), b.as_posix())
    rendu = app._on_drop(SimpleNamespace(data=data, action="copy"))
    assert rendu == "copy"
    assert _pomper(app, lambda: len(app.arbre.get_children()) == 2)     # traité APRÈS le retour du rappel
    assert _pomper(app, lambda: app.en_cours == 0)
    assert sorted(p.name for p in (tmp_path / "out").iterdir()) == ["[a]- a b.pdf", "[a]- b.pdf"]


@GUI
def test_on_drop_donnee_invalide_ne_leve_pas(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path / "out")})
    assert app._on_drop(SimpleNamespace(data="{non ferme", action="copy")) == "copy"
    assert "Erreur interne" in app.var_statut.get()


@GUI
def test_depot_de_non_pdf_et_introuvable_affiche_des_lignes_ignorees(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path / "out")})
    (tmp_path / "n.txt").write_text("x")
    app.ajouter([str(tmp_path / "n.txt"), str(tmp_path / "absent.pdf")])
    assert [v[1] for v in _lignes(app)] == ["— Ignoré", "— Ignoré"]
    assert app.en_cours == 0


# --------------------------------------------------------------------------- #
# Actions diverses
# --------------------------------------------------------------------------- #
@GUI
def test_vider_la_liste_refuse_pendant_un_traitement(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": str(tmp_path)})
    infos = []
    monkeypatch.setattr(tray.messagebox, "showinfo", lambda *a, **k: infos.append(a))
    app.arbre.insert("", "end", values=("x", "y", "z"))
    app.en_cours = 1
    app.vider_liste()
    assert len(_lignes(app)) == 1 and len(infos) == 1
    app.en_cours = 0
    app.vider_liste()
    assert _lignes(app) == [] and app.var_statut.get() == "Prêt."


@GUI
def test_choisir_sortie_enregistre_et_met_a_jour_l_affichage(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": ""})
    assert "Choisissez le dossier de sortie" in app.zone.cget("text")
    monkeypatch.setattr(tray.filedialog, "askdirectory", lambda **k: str(tmp_path / "sortie é"))
    assert app.choisir_sortie() is True
    assert app.var_sortie.get() == str(tmp_path / "sortie é")
    assert "Glissez-déposez vos PDF ici\n" in app.zone.cget("text")
    cfg = json.loads((tmp_path / "appdata" / tray.ID_APP / "config.json").read_text(encoding="utf-8"))
    assert cfg["sortie"] == str(tmp_path / "sortie é")
    monkeypatch.setattr(tray.filedialog, "askdirectory", lambda **k: "")
    assert app.choisir_sortie() is False and app.cfg["sortie"] == str(tmp_path / "sortie é")


@GUI
def test_ouvrir_sortie_dossier_impossible_previent(fabrique, tmp_path, monkeypatch):
    fichier = tmp_path / "unfichier"
    fichier.write_text("x")
    app = fabrique(cfg={"sortie": str(fichier / "sous")})
    erreurs = []
    monkeypatch.setattr(tray.messagebox, "showerror", lambda *a, **k: erreurs.append(a))
    app.ouvrir_sortie()
    assert len(erreurs) == 1


@GUI
def test_notification_de_fin_seulement_si_la_fenetre_est_cachee(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path / "out")}, icone=True)
    app.root.update()
    app.afficher()
    app.ajouter([str(_pdf(tmp_path / "a.pdf"))])
    assert _pomper(app, lambda: app.en_cours == 0)
    assert not [a for a in app.icone.appels if isinstance(a, tuple)]       # fenêtre visible : pas de bulle
    app.masquer()
    app.ajouter([str(_pdf(tmp_path / "b.pdf"))])
    assert _pomper(app, lambda: app.en_cours == 0)
    assert [a for a in app.icone.appels if isinstance(a, tuple)] == [("notify", "Terminé : 1 aplati(s).")]


@GUI
def test_definir_demarrage_met_a_jour_config_et_menu(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path)}, icone=True)
    app.definir_demarrage(False)
    assert app.cfg["demarrage_auto"] is False and app.var_demarrage.get() is False
    assert "update_menu" in app.icone.appels


# --------------------------------------------------------------------------- #
# GUI-3 : le résumé final « Terminé : ... » est inexact
# --------------------------------------------------------------------------- #
@GUI
def test_resume_ne_compte_pas_les_copies_comme_aplatis(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path / "out")})
    app.ajouter([str(_pdf(tmp_path / "in" / "signe.pdf", tampon=True)),
                 str(_pdf(tmp_path / "in" / "rien.pdf", tampon=False))])
    assert _pomper(app, lambda: app.en_cours == 0)
    assert [v[1] for v in _lignes(app)] == ["✔ Aplati", "✔ Copié"]
    assert "2 aplati(s)" not in app.var_statut.get(), app.var_statut.get()


@GUI
def test_resume_compte_les_fichiers_ignores_a_la_collecte(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path / "out")})
    (tmp_path / "in").mkdir()
    (tmp_path / "in" / "notes.docx").write_text("x")
    app.ajouter([str(_pdf(tmp_path / "in" / "a.pdf")), str(tmp_path / "in" / "notes.docx"),
                 str(tmp_path / "in" / "absent.pdf")])
    assert _pomper(app, lambda: app.en_cours == 0)
    assert [v[1] for v in _lignes(app)].count("— Ignoré") == 2
    assert "2 ignoré(s)" in app.var_statut.get(), app.var_statut.get()


# --------------------------------------------------------------------------- #
# Quitter
# --------------------------------------------------------------------------- #
@GUI
def test_quitter_occupe_refus_ne_ferme_pas(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": str(tmp_path)}, icone=True)
    app.en_cours = 1
    questions = []
    monkeypatch.setattr(tray.messagebox, "askyesno", lambda *a, **k: questions.append(a) or False)
    app.quitter()
    assert len(questions) == 1 and "stop" not in app.icone.appels
    assert not _detruite(app)


@GUI
def test_quitter_occupe_accepte_arrete_icone_et_detruit(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": str(tmp_path)}, icone=True)
    app.en_cours = 1
    monkeypatch.setattr(tray.messagebox, "askyesno", lambda *a, **k: True)
    app.quitter()
    assert app.icone.appels.count("stop") == 1
    assert _detruite(app)


@GUI
def test_quitter_inactif_ne_pose_aucune_question(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": str(tmp_path)}, icone=True)
    monkeypatch.setattr(tray.messagebox, "askyesno", lambda *a, **k: pytest.fail("question inutile"))
    app.quitter()
    assert app.icone.appels.count("stop") == 1


@GUI
def test_quitter_via_evenement_de_l_icone(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path)}, icone=True)
    app._poster("quitter")
    fin = time.time() + 5
    while not _detruite(app) and time.time() < fin:
        try:
            app.root.update()
        except Exception:
            break
        time.sleep(0.01)
    assert _detruite(app)
    assert app.icone.appels.count("stop") == 1


# --------------------------------------------------------------------------- #
# Fil d'exécution : le menu de l'icône ne touche pas à Tk
# --------------------------------------------------------------------------- #
@GUI
def test_actions_du_menu_de_l_icone_ne_font_que_poster(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path)}, icone=True)
    app.root.update()
    items = [i for i in app.icone.menu.items if hasattr(i, "action")]
    assert [i.texte for i in items][:1] == ["Ouvrir Aplatir PDF"]
    appels = []
    reel = app.evenements.put
    app.evenements.put = lambda ev: appels.append(ev) or reel(ev)
    for item in items:
        item.action(app.icone, item)
    assert [a[0] for a in appels] == ["afficher", "choisir_sortie", "ouvrir_sortie",
                                      "basculer_demarrage", "quitter"]
    case = [i for i in items if i.texte.startswith("Lancer au")][0]
    assert case.kw["checked"](None) is True        # lecture seule de cfg depuis le fil de l'icône


@GUI
def test_pomper_se_reprogramme_apres_une_exception(fabrique, tmp_path, monkeypatch):
    app = fabrique(cfg={"sortie": str(tmp_path)})
    vus = []
    reel = app._evenement

    def evenement(ev):
        vus.append(ev[0])
        if ev[0] == "boum":
            raise RuntimeError("x")
        reel(ev)
    monkeypatch.setattr(app, "_evenement", evenement)
    app._poster("boum")
    app._poster("afficher")
    assert _pomper(app, lambda: "afficher" in vus)
    app._poster("afficher")
    n = len(vus)
    assert _pomper(app, lambda: len(vus) > n)       # la boucle tourne toujours


# --------------------------------------------------------------------------- #
# Instance unique : verrou par profil + serveur local à jeton (sans interface)
# --------------------------------------------------------------------------- #
@pytest.fixture
def instance(tmp_path, monkeypatch):
    """Une « instance déjà lancée » : verrou pris, serveur ouvert, messages reçus dans ``recus``."""
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    verrou = tray.prendre_verrou()
    assert verrou is not None
    serveur, jeton = tray.ouvrir_serveur()
    recus: list = []
    occupe = {"v": False}

    def poster(msg):
        recus.append(msg)
        if msg.get("cmd") == "quit":        # l'instance obéit : elle se ferme et relâche le verrou
            verrou.close()

    threading.Thread(target=tray.servir_instance, args=(serveur, jeton, poster, lambda: occupe["v"]),
                     daemon=True).start()
    port = json.loads((tmp_path / "appdata" / tray.ID_APP / tray.FICHIER_INSTANCE).read_text())["port"]
    yield SimpleNamespace(port=port, jeton=jeton, recus=recus, occupe=occupe, verrou=verrou)
    serveur.close()
    verrou.close()


def _brut(port, octets, shutdown=True):
    """Envoie des octets bruts ; retourne la réponse (b"" si le serveur ferme sans répondre)."""
    try:
        c = socket.create_connection(("127.0.0.1", port), timeout=5)
    except OSError:
        return b""
    try:
        c.sendall(octets)
        if shutdown:
            c.shutdown(socket.SHUT_WR)
        return c.recv(16)
    except OSError:                              # reset / abort : le serveur a refusé le message
        return b""
    finally:
        c.close()


def _msg(inst, **champs):
    return json.dumps(dict(champs, jeton=inst.jeton, version=tray.VERSION)).encode()


def test_instance_un_seul_verrou_par_profil(instance):
    assert tray.prendre_verrou() is None                          # même profil : refusé


def test_instance_show_et_files(instance):
    assert tray.contacter_instance({"cmd": "show"}) == "OK"
    fichiers = {"cmd": "files", "paths": ["C:\\a b\\é.pdf", "//srv/x.pdf"]}
    assert tray.contacter_instance(fichiers) == "OK"
    assert instance.recus == [{"cmd": "show"}, fichiers]          # jeton et version ne sont pas transmis


@pytest.mark.parametrize("octets", [b"{oops\n", b"[1, 2]\n", b"", b"\xff\xfe\n", b"null\n"])
def test_instance_message_invalide_est_refuse_sans_tuer_le_serveur(instance, octets):
    assert _brut(instance.port, octets) == b""
    assert tray.contacter_instance({"cmd": "show"}) == "OK"      # toujours vivant
    assert instance.recus == [{"cmd": "show"}]


def test_instance_message_sans_retour_a_la_ligne_accepte(instance):
    assert _brut(instance.port, _msg(instance, cmd="show")) == b"OK"


def test_instance_message_trop_gros_est_refuse_sans_tuer_le_serveur(instance):
    enorme = json.dumps({"cmd": "files", "paths": ["C:\\" + "x" * 200 + ".pdf"] * 6000,
                         "jeton": instance.jeton}).encode() + b"\n"
    assert len(enorme) > (1 << 20)
    assert _brut(instance.port, enorme) == b""
    assert tray.contacter_instance({"cmd": "show"}) == "OK"


def test_instance_mauvais_jeton_refuse(instance):
    mauvais = json.dumps({"cmd": "files", "paths": ["x.pdf"], "jeton": "pas-le-bon",
                          "version": tray.VERSION}).encode()
    assert _brut(instance.port, mauvais) == b"NON"
    assert instance.recus == []


def test_instance_autre_version_repond_autre_et_ne_traite_rien(instance, monkeypatch):
    monkeypatch.setattr(tray, "VERSION", "99-autre")
    assert tray.contacter_instance({"cmd": "files", "paths": ["x.pdf"]}) == "AUTRE"
    assert instance.recus == []


def test_instance_demande_de_fermeture(instance, monkeypatch):
    monkeypatch.setattr(tray, "VERSION", "99-autre")             # la fermeture est acceptée quelle que soit la version
    instance.occupe["v"] = True
    assert tray.contacter_instance({"cmd": "quit"}) == "OCCUPE"
    assert instance.recus == []
    instance.occupe["v"] = False
    assert tray.contacter_instance({"cmd": "quit"}) == "OK"
    assert instance.recus == [{"cmd": "quit"}]


def test_deux_profils_utilisateur_ont_chacun_leur_instance(tmp_path):
    """Le verrou et le serveur sont par profil : l'instance de A ne répond jamais pour B."""
    a, b = tmp_path / "A", tmp_path / "B"
    va, vb = tray.prendre_verrou(a), tray.prendre_verrou(b)
    assert va is not None and vb is not None
    sa, ja = tray.ouvrir_serveur(a)
    recus: list = []
    threading.Thread(target=tray.servir_instance, args=(sa, ja, recus.append), daemon=True).start()
    try:
        assert tray.contacter_instance({"cmd": "show"}, dossier=a) == "OK"
        assert tray.contacter_instance({"cmd": "show"}, dossier=b) is None
    finally:
        sa.close()
        va.close()
        vb.close()
    assert recus == [{"cmd": "show"}]


def test_instance_json_perime_ou_etranger_ne_bloque_pas(tmp_path, monkeypatch):
    """Si instance.json pointe vers un programme étranger muet : pas de blocage, pas de réponse."""
    muet = socket.socket()
    muet.bind(("127.0.0.1", 0))
    muet.listen(5)
    (tmp_path / tray.FICHIER_INSTANCE).write_text(
        json.dumps({"port": muet.getsockname()[1], "jeton": "x", "version": "1"}))
    reel = socket.create_connection
    monkeypatch.setattr(tray.socket, "create_connection",
                        lambda adresse, timeout=None: reel(adresse, timeout=0.4))
    try:
        assert tray.contacter_instance({"cmd": "show"}, dossier=tmp_path) is None
    finally:
        muet.close()


def test_servir_instance_survit_a_une_erreur_transitoire_d_accept(tmp_path):
    serveur, jeton = tray.ouvrir_serveur(tmp_path)

    class Capricieux:
        def __init__(self):
            self.n = 0

        def settimeout(self, t):
            serveur.settimeout(t)

        def accept(self):
            self.n += 1
            if self.n == 1:
                raise ConnectionAbortedError(10053, "connexion annulée avant accept")
            return serveur.accept()

        def fileno(self):
            return serveur.fileno()

    recus: list = []
    th = threading.Thread(target=tray.servir_instance, args=(Capricieux(), jeton, recus.append), daemon=True)
    th.start()
    try:
        time.sleep(0.3)
        assert th.is_alive(), "le fil serveur est mort sur une erreur transitoire"
        assert tray.contacter_instance({"cmd": "show"}, dossier=tmp_path) == "OK"
    finally:
        serveur.close()


def test_oublier_serveur_ne_retire_que_sa_propre_publication(tmp_path):
    serveur, jeton = tray.ouvrir_serveur(tmp_path)
    try:
        tray.oublier_serveur("autre-jeton", tmp_path)
        assert (tmp_path / tray.FICHIER_INSTANCE).exists()
        tray.oublier_serveur(jeton, tmp_path)
        assert not (tmp_path / tray.FICHIER_INSTANCE).exists()
    finally:
        serveur.close()


def _fermer_journal(avant):
    for h in tray.log.handlers[:]:
        if h not in avant:
            tray.log.removeHandler(h)
            h.close()


def test_main_avec_fichier_quand_une_instance_tourne_transmet_et_sort(tmp_path, instance):
    src = _pdf(tmp_path / "x.pdf")
    avant = list(tray.log.handlers)
    try:
        t0 = time.time()
        assert tray.main([str(src)]) == 0
        assert time.time() - t0 < 3
        assert instance.recus == [{"cmd": "files", "paths": [os.path.abspath(str(src))]}]
        assert tray.main(["--tray"]) == 0
        assert instance.recus[-1] == {"cmd": "show"}
    finally:
        _fermer_journal(avant)


def _faux_lancer(vues, monkeypatch):
    def faux_lancer(self):
        vues.append((self.visible, self.root.state()))
        self.jobs.put(None)
        self.root.destroy()
    monkeypatch.setattr(tray.App, "lancer", faux_lancer)
    monkeypatch.setattr(tray, "activer_dpi_windows", lambda: None)


@GUI
def test_main_quand_le_verrou_est_tenu_mais_personne_ne_repond_continue(tmp_path, monkeypatch, fabrique):
    """Un verrou orphelin ne doit pas empêcher l'outil de s'ouvrir."""
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    verrou = tray.prendre_verrou()
    monkeypatch.setattr(tray, "contacter_instance", lambda *a, **k: None)
    monkeypatch.setattr(tray.time, "monotonic", iter(range(0, 10_000, 5)).__next__)   # le délai de 15 s passe vite
    monkeypatch.setattr(tray.time, "sleep", lambda s: None)
    vues: list = []
    _faux_lancer(vues, monkeypatch)
    avant = list(tray.log.handlers)
    try:
        assert tray.main([]) == 0
    finally:
        verrou.close()
        _fermer_journal(avant)
    assert vues == [(True, "normal")]


@GUI
def test_main_remplace_une_instance_d_une_autre_version(tmp_path, instance, monkeypatch, fabrique):
    """Mise à jour de l'exe : l'ancienne instance reçoit « quit », relâche le verrou, la nouvelle démarre."""
    monkeypatch.setattr(tray, "VERSION", "99-nouvelle")
    vues: list = []
    _faux_lancer(vues, monkeypatch)
    avant = list(tray.log.handlers)
    try:
        assert tray.main([]) == 0
    finally:
        _fermer_journal(avant)
    assert instance.recus == [{"cmd": "quit"}]
    assert vues == [(True, "normal")]


def test_main_autre_version_occupee_n_ecrase_rien(tmp_path, instance, monkeypatch):
    monkeypatch.setattr(tray, "VERSION", "99-nouvelle")
    instance.occupe["v"] = True
    infos = []
    monkeypatch.setattr(tray, "message_info", infos.append)
    avant = list(tray.log.handlers)
    try:
        assert tray.main([]) == 0
    finally:
        _fermer_journal(avant)
    assert instance.recus == [] and len(infos) == 1 and "traiter" in infos[0]


def test_main_ancienne_version_a_port_fixe_est_signalee(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "ancienne_version_active", lambda: True)
    infos = []
    monkeypatch.setattr(tray, "message_info", infos.append)
    avant = list(tray.log.handlers)
    try:
        assert tray.main([]) == 0
    finally:
        _fermer_journal(avant)
    assert len(infos) == 1 and "ancienne version" in infos[0].lower()


@GUI
def test_nom_different_de_la_sortie_est_signale_dans_la_ligne(fabrique, tmp_path):
    """Une sortie renommée « (2) » est annoncée en tête du détail, avant le message."""
    app = fabrique(cfg={"sortie": str(tmp_path)})
    iid = app.arbre.insert("", "end", values=("x.pdf", "⏳ En attente", ""), tags=("attente",))
    app.en_cours = 1
    app._fin(iid, core.Resultat(src=tmp_path / "x.pdf", dst=tmp_path / "[a]- x (2).pdf", statut="ok", message="msg"))
    assert app.arbre.item(iid, "values")[2] == "→ [a]- x (2).pdf ; msg"


@GUI
def test_ligne_selectionnee_montre_le_texte_complet_et_la_source(fabrique, tmp_path):
    app = fabrique(cfg={"sortie": str(tmp_path)})
    iid = app.arbre.insert("", "end", values=("x.pdf", "✖ Erreur", "un très long message " * 10), tags=("erreur",))
    app.sources[iid] = tmp_path / "OF-1" / "x.pdf"
    app.arbre.selection_set(iid)
    app.root.update()
    texte = app.var_statut.get()
    assert str(tmp_path / "OF-1" / "x.pdf") in texte and texte.count("un très long message") == 10
