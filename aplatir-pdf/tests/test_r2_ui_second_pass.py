"""2e passe d'audit, angle « interface » : ce que l'utilisateur voit réellement (liste, barre d'état,
bulle d'aide, README) après les correctifs de la 1re passe.

Les tests d'interface exigent un affichage (``APLATIR_GUI_TESTS=1 xvfb-run -a python -m pytest ...``).
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant R2-UI-n du rapport) : retirer le
marqueur quand le défaut est corrigé. Les autres sont des garde-fous (ils passent aujourd'hui).
"""
import json
import os
import pathlib
import re
import time
from types import SimpleNamespace

import pymupdf
import pytest

tray = pytest.importorskip("aplatir_tray")

GUI = pytest.mark.skipif(not os.environ.get("APLATIR_GUI_TESTS"), reason="GUI")
ICI = pathlib.Path(__file__).resolve().parent.parent

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
def _pdf(chemin, texte="x", tampon=True, noview=False):
    chemin = pathlib.Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), texte)
    if tampon:
        p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    if noview:       # tampon que MuPDF n'affiche pas mais que bake grave : repli « Aplati (image) »
        a = p.add_stamp_annot(pymupdf.Rect(300, 300, 500, 370), stamp=1)
        d.xref_set_key(a.xref, "F", "32")
    d.save(str(chemin))
    d.close()
    return chemin


class FauxIcone:
    def __init__(self, *a, **k):
        self.appels = []

    def run_detached(self, *a):
        pass

    def stop(self):
        self.appels.append("stop")

    def update_menu(self):
        pass

    def notify(self, message, titre=None):
        self.appels.append(("notify", message))


class FauxMenu:
    SEPARATOR = object()

    def __init__(self, *items):
        pass


@pytest.fixture
def fabrique(tmp_path, monkeypatch):
    apps = []
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "pystray", None)
    monkeypatch.setattr(tray, "regler_demarrage", lambda *a, **k: True)
    monkeypatch.setattr(tray, "demarrage_actif", lambda *a, **k: False)
    monkeypatch.setattr(tray, "ouvrir_dossier", lambda *a, **k: None)

    def faire(cfg=None, cache=False, icone=False):
        d = tmp_path / "appdata" / tray.ID_APP
        d.mkdir(parents=True, exist_ok=True)
        if cfg is not None:
            (d / "config.json").write_text(json.dumps(cfg), encoding="utf-8")
        if icone:
            monkeypatch.setattr(tray, "pystray", SimpleNamespace(
                Icon=FauxIcone, Menu=FauxMenu, MenuItem=lambda *a, **k: None))
        app = tray.App(cache=cache, fichiers=[], serveur=None)
        apps.append(app)
        return app

    yield faire
    for a in apps:
        try:
            a.jobs.put(None)
            a.root.destroy()
        except Exception:
            pass


def _pomper(app, condition, delai=30.0):
    fin = time.time() + delai
    while time.time() < fin:
        app.root.update()
        if condition():
            return True
        time.sleep(0.005)
    return False


def _deposer(app, *chemins):
    """Comme tkdnd : liste Tcl de chemins, puis traitement différé par ``after(20)``."""
    donnee = " ".join("{%s}" % c if " " in str(c) else str(c) for c in chemins)
    app._on_drop(SimpleNamespace(data=donnee, action="copy"))


def _terminer(app, nb_lignes):
    assert _pomper(app, lambda: len(app.arbre.get_children()) >= nb_lignes and app.en_cours == 0
                   and app.jobs.empty())
    for _ in range(5):
        app.root.update()


def _lignes(app):
    return [app.arbre.item(i, "values") for i in app.arbre.get_children()]


def _config(tmp_path, sortie, **plus):
    return {"sortie": str(sortie), "demarrage_auto": True, "premier_plan": False, "securite": True,
            "astuce_vue": True, **plus}


def _widgets(w):
    for c in w.winfo_children():
        yield c
        yield from _widgets(c)


def _prefixe_visible(app, colonne, texte):
    """Début du ``texte`` qui tient réellement dans la colonne (la fin est rognée, sans « … »)."""
    import tkinter.font as tkfont
    police = tkfont.nametofont("TkDefaultFont", app.root)
    largeur = int(app.arbre.column(colonne, "width")) - 2 * app._px(4)
    visible = ""
    for c in texte:
        if police.measure(visible + c) > largeur:
            break
        visible += c
    return visible


def _texte_accessible(app, iid, attendu):
    """Vrai si ``attendu`` est lisible QUELQUE PART pour l'utilisateur quand il désigne la ligne :
    dans la partie visible de la cellule, dans la barre d'état après sélection de la ligne, ou dans un
    autre widget texte (bulle d'aide). Adapter ce test si une autre façon de lire le texte complet est
    ajoutée (par exemple une bulle au survol)."""
    detail = app.arbre.item(iid, "values")[2]
    if attendu in _prefixe_visible(app, "detail", detail):
        return True
    app.arbre.selection_set(iid)
    app.arbre.focus(iid)
    app.arbre.event_generate("<<TreeviewSelect>>")
    app.root.update()
    candidats = [app.var_statut.get()]
    for w in _widgets(app.root):
        try:
            candidats.append(str(w.cget("text")))
        except Exception:
            pass
    return any(attendu in c for c in candidats)


# --------------------------------------------------------------------------- #
# R2-UI-1 : le texte de la colonne « Détail » est rogné sans moyen de le lire en entier
# --------------------------------------------------------------------------- #
XFA_ATTENDU = "Adobe Reader"


def _pdf_xfa(chemin):
    chemin = pathlib.Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    d.new_page().insert_text((72, 72), "Please wait...")
    x = d.get_new_xref()
    d.update_object(x, "<<>>")
    d.update_stream(x, b"<template/>")
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</XFA[(template) %d 0 R]/Fields[]>>" % x)
    d.save(str(chemin))
    d.close()
    return chemin


@GUI
def test_message_d_erreur_xfa_lisible_en_entier(tmp_path, fabrique):
    sortie = tmp_path / "sortie"
    app = fabrique(cfg=_config(tmp_path, sortie))
    _deposer(app, _pdf_xfa(tmp_path / "src" / "Formulaire.pdf"))
    _terminer(app, 1)
    (iid,) = app.arbre.get_children()
    assert app.arbre.item(iid, "values")[1].startswith("✖")
    assert XFA_ATTENDU in app.arbre.item(iid, "values")[2]          # le message complet est bien produit...
    assert _texte_accessible(app, iid, XFA_ATTENDU)                  # ...mais l'utilisateur ne peut pas le lire


@GUI
def test_deux_sources_de_meme_nom_se_distinguent_dans_la_liste(tmp_path, fabrique):
    sortie = tmp_path / "sortie"
    app = fabrique(cfg=_config(tmp_path, sortie))
    a = _pdf(tmp_path / "OF-1" / "Rapport de fin de fabrication.pdf", "OF-1")
    b = _pdf(tmp_path / "OF-2" / "Rapport de fin de fabrication.pdf", "OF-2")
    _deposer(app, a, b)
    _terminer(app, 2)
    ids = app.arbre.get_children()
    assert (sortie / "[a]- Rapport de fin de fabrication (2).pdf").exists()    # le correctif 1re passe : OK
    # l'utilisateur doit pouvoir lire « (2) » (ou le dossier d'origine) pour savoir quelle ligne est laquelle
    assert _texte_accessible(app, ids[1], "(2)")


# --------------------------------------------------------------------------- #
# R2-UI-2 : « aucun n'écrase l'autre » (README) n'est vrai que pendant une session de l'outil
# --------------------------------------------------------------------------- #
@GUI
def test_meme_nom_apres_redemarrage_n_ecrase_pas_une_autre_source(tmp_path, fabrique):
    sortie = tmp_path / "sortie"
    a = _pdf(tmp_path / "OF-1" / "Rapport.pdf", "OF-1")
    b = _pdf(tmp_path / "OF-2" / "Rapport.pdf", "OF-2")
    app1 = fabrique(cfg=_config(tmp_path, sortie))
    _deposer(app1, a)
    _terminer(app1, 1)
    app1.quitter(confirmer=False)                                    # « redémarrage » : nouvelle App, nouvelle table
    app2 = fabrique(cfg=_config(tmp_path, sortie))
    _deposer(app2, b)
    _terminer(app2, 1)
    textes = {p.name: pymupdf.open(str(p))[0].get_text() for p in sortie.glob("*.pdf")}
    assert any("OF-1" in t for t in textes.values()), f"la sortie de OF-1 a été écrasée : {sorted(textes)}"
    assert any("OF-2" in t for t in textes.values())


@GUI
def test_meme_nom_dans_une_session_garde_les_deux_sorties(tmp_path, fabrique):
    """Garde-fou : le correctif de la 1re passe fonctionne bien dans la même session."""
    sortie = tmp_path / "sortie"
    app = fabrique(cfg=_config(tmp_path, sortie))
    _deposer(app, _pdf(tmp_path / "OF-1" / "Rapport.pdf", "OF-1"), _pdf(tmp_path / "OF-2" / "Rapport.pdf", "OF-2"))
    _terminer(app, 2)
    assert sorted(p.name for p in sortie.glob("*.pdf")) == ["[a]- Rapport (2).pdf", "[a]- Rapport.pdf"]
    # reglisser la même source retrouve son nom
    _deposer(app, tmp_path / "OF-2" / "Rapport.pdf")
    _terminer(app, 3)
    assert sorted(p.name for p in sortie.glob("*.pdf")) == ["[a]- Rapport (2).pdf", "[a]- Rapport.pdf"]
    assert "remplace le fichier existant" in _lignes(app)[-1][2]


# --------------------------------------------------------------------------- #
# R2-UI-3 : le résumé « N à vérifier » compte aussi les pages converties en image par sécurité
# --------------------------------------------------------------------------- #
@GUI
def test_resume_a_verifier_correspond_a_une_ligne_a_verifier(tmp_path, fabrique):
    sortie = tmp_path / "sortie"
    app = fabrique(cfg=_config(tmp_path, sortie))
    _deposer(app, _pdf(tmp_path / "s" / "Tampon masque.pdf", tampon=False, noview=True))
    _terminer(app, 1)
    statut = app.var_statut.get()
    assert _lignes(app)[0][1].startswith("✔ Aplati (image)"), _lignes(app)
    if "à vérifier" in statut:
        assert any("À vérifier" in v[1] for v in _lignes(app)), (statut, _lignes(app))


# --------------------------------------------------------------------------- #
# R2-UI-4 : fichiers confiés à l'instance déjà lancée (glisser sur l'exe) : aucune trace visible
# --------------------------------------------------------------------------- #
@GUI
def test_fichiers_confies_a_l_instance_cachee_montrent_la_fenetre(tmp_path, fabrique):
    sortie = tmp_path / "sortie"
    app = fabrique(cfg=_config(tmp_path, sortie), cache=True, icone=True)
    assert app.root.state() == "withdrawn"
    app._message_instance({"cmd": "files", "paths": [str(_pdf(tmp_path / "s" / "a.pdf"))]})
    _pomper(app, lambda: app.arbre.get_children() and app.en_cours == 0 and app.jobs.empty())
    for _ in range(5):
        app.root.update()
    assert app.root.state() != "withdrawn"


# --------------------------------------------------------------------------- #
# R2-UI-5 : fenêtre plus haute que l'écran quand la résolution logique est petite
# --------------------------------------------------------------------------- #
@GUI
def test_la_fenetre_tient_dans_l_ecran(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "pystray", None)
    monkeypatch.setattr(tray, "regler_demarrage", lambda *a, **k: True)
    monkeypatch.setattr(tray, "demarrage_actif", lambda *a, **k: False)
    d = tmp_path / "appdata" / tray.ID_APP
    d.mkdir(parents=True)
    (d / "config.json").write_text(json.dumps(_config(tmp_path, tmp_path / "s")), encoding="utf-8")
    original = tray.TkinterDnD.Tk

    class TkEchelle(original):
        def __init__(self, *a, **k):
            super().__init__(*a, **k)
            hauteur = self.winfo_screenheight()
            k_voulu = 1.2 * hauteur / 580.0              # échelle pour laquelle 580 px logiques dépassent l'écran
            self.tk.call("tk", "scaling", k_voulu * 96 / 72.0)

    monkeypatch.setattr(tray.TkinterDnD, "Tk", TkEchelle)
    app = tray.App(cache=False, fichiers=[], serveur=None)
    try:
        app.root.update()
        assert app.root.winfo_height() <= app.root.winfo_screenheight()
    finally:
        app.jobs.put(None)
        app.root.destroy()


# --------------------------------------------------------------------------- #
# Garde-fous : bulle de la première fermeture et migration de config.json
# --------------------------------------------------------------------------- #
@GUI
@pytest.mark.parametrize("cfg", [
    {"sortie": "x", "demarrage_auto": True, "premier_plan": True, "securite": True},      # ancienne config
    {"astuce_vue": "oui"},                                                                  # mauvais type
    {"astuce_vue": 1},                                                                      # entier, pas booléen
])
def test_bulle_affichee_une_seule_fois_et_memorisee(tmp_path, fabrique, cfg):
    app = fabrique(cfg=cfg, icone=True)
    assert app.cfg["astuce_vue"] is False
    app.afficher()
    app.root.update()
    app.fermer_fenetre()
    notifs = [a for a in app.icone.appels if isinstance(a, tuple)]
    assert len(notifs) == 1 and "reste actif" in notifs[0][1]
    assert json.loads((tmp_path / "appdata" / tray.ID_APP / "config.json").read_text())["astuce_vue"] is True
    app.afficher()
    app.fermer_fenetre()
    assert len([a for a in app.icone.appels if isinstance(a, tuple)]) == 1       # pas une 2e fois


@GUI
def test_titre_avec_version(fabrique, monkeypatch):
    monkeypatch.setattr(tray, "VERSION", "12-abcdef0")
    assert "version 12-abcdef0" in fabrique().root.title()
    monkeypatch.setattr(tray, "VERSION", "dev")
    assert fabrique().root.title() == tray.NOM_APP


# --------------------------------------------------------------------------- #
# README : chaque option visible dans la fenêtre y est expliquée ; docstring à jour
# --------------------------------------------------------------------------- #
README = (ICI / "README.md").read_text(encoding="utf-8")


def test_readme_explique_la_case_securite():
    assert re.search(r"Sécurité", README)


def test_docstring_sans_envoyer_vers():
    assert "Envoyer vers" not in (ICI / "aplatir_tray.py").read_text(encoding="utf-8").split('"""')[1]


def test_readme_libelles_identiques_a_ceux_de_la_liste():
    """Garde-fou : les six résultats du tableau du README portent les libellés exacts de la liste."""
    for statut, (texte, _tag) in tray.LIBELLES.items():
        sans_symbole = texte.split(" ", 1)[1]
        assert sans_symbole.replace(" (image)", "") in README, (statut, texte)
    assert "À vérifier" in README and "Aplati (image)" in README


def test_readme_noms_de_fichiers_de_l_outil_corrects():
    assert tray.dossier_config().name == "AplatirPDF"
    assert "%APPDATA%\\AplatirPDF\\config.json" in README and "aplatir.log" in README
    assert "`[a]- <nom d'origine>.pdf`" in README


def test_readme_sans_reglisser():
    assert not re.search(r"\bReglisser\b|\breglisser\b", README)


def test_message_image_sans_repetition_des_pages(tmp_path):
    import aplatir_core as core
    src = _pdf(tmp_path / "s" / "Tampon masque.pdf", tampon=False, noview=True)
    res = core.aplatir_fichier(src, tmp_path / "sortie")
    assert res.statut == "securite", (res.statut, res.message)
    assert not re.search(r"page\(s\) ([\d, ]+) ; page\(s\) \1 convertie", res.message), res.message
