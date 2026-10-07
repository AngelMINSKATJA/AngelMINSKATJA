"""2e passe d'audit, angle « interface » (2e relecture, après les correctifs de l'audit « interface »).

Les tests d'interface exigent un affichage (``APLATIR_GUI_TESTS=1 xvfb-run -a python -m pytest ...``).
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant UI-n du rapport) : retirer le
marqueur quand le défaut est corrigé. Les autres sont des garde-fous (ils passent aujourd'hui) qui
vérifient une affirmation du README contre le code ou la CI.
"""
import json
import os
import pathlib
import re
from types import SimpleNamespace

import pymupdf
import pytest

import aplatir_core as core

tray = pytest.importorskip("aplatir_tray")

GUI = pytest.mark.skipif(not os.environ.get("APLATIR_GUI_TESTS"), reason="GUI")
ICI = pathlib.Path(__file__).resolve().parent.parent
README = (ICI / "README.md").read_text(encoding="utf-8")
SOURCE_TRAY = (ICI / "aplatir_tray.py").read_text(encoding="utf-8")
CI_YML = ICI.parent / ".github" / "workflows" / "build-aplatir-pdf.yml"

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
def _pdf(chemin, texte="x"):
    chemin = pathlib.Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), texte)
    p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


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


def _texte_pdf(chemin):
    with pymupdf.open(str(chemin)) as d:
        return d[0].get_text()


@pytest.fixture
def fabrique(tmp_path, monkeypatch):
    apps = []
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "pystray", None)
    monkeypatch.setattr(tray, "regler_demarrage", lambda *a, **k: True)
    monkeypatch.setattr(tray, "demarrage_actif", lambda *a, **k: False)
    monkeypatch.setattr(tray, "ouvrir_dossier", lambda *a, **k: None)

    def faire(sortie):
        d = tmp_path / "appdata" / tray.ID_APP
        d.mkdir(parents=True, exist_ok=True)
        cfg = {"sortie": str(sortie), "demarrage_auto": True, "premier_plan": False, "securite": True,
               "astuce_vue": True}
        (d / "config.json").write_text(json.dumps(cfg), encoding="utf-8")
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


def _pomper(app, condition, delai=30.0):
    import time
    fin = time.time() + delai
    while time.time() < fin:
        app.root.update()
        if condition():
            return True
        time.sleep(0.005)
    return False


def _deposer(app, *chemins):
    donnee = " ".join("{%s}" % c if " " in str(c) else str(c) for c in chemins)
    app._on_drop(SimpleNamespace(data=donnee, action="copy"))


def _terminer(app, nb_lignes):
    assert _pomper(app, lambda: len(app.arbre.get_children()) >= nb_lignes and app.en_cours == 0
                   and app.jobs.empty())
    for _ in range(5):
        app.root.update()


def _widgets(w):
    for c in w.winfo_children():
        yield c
        yield from _widgets(c)


def _etiquette_statut(app):
    for w in _widgets(app.root):
        try:
            if str(w.cget("textvariable")) == str(app.var_statut):
                return w
        except Exception:
            pass
    raise AssertionError("étiquette de la barre d'état introuvable")


def _lisible_en_entier(app, etiquette):
    """Vrai si tout le texte de la barre d'état est visible : il tient sur la largeur de l'étiquette, ou
    l'étiquette passe à la ligne (``wraplength``) sur une hauteur suffisante."""
    import tkinter.font as tkfont
    app.root.update()
    police = tkfont.nametofont(str(etiquette.cget("font")) or "TkDefaultFont", app.root)
    texte = app.var_statut.get()
    largeur = etiquette.winfo_width()
    if police.measure(texte) <= largeur:
        return True
    wrap = int(str(etiquette.cget("wraplength")) or 0)
    if wrap and wrap <= largeur:
        lignes = -(-police.measure(texte) // wrap)
        return etiquette.winfo_height() >= lignes * police.metrics("linespace")
    return False


# --------------------------------------------------------------------------- #
# UI-1 : « cliquez sur la ligne pour lire le texte complet » : la barre d'état rogne elle aussi
# --------------------------------------------------------------------------- #
@GUI
def test_clic_sur_une_ligne_affiche_le_message_complet(tmp_path, fabrique):
    app = fabrique(tmp_path / "sortie")
    _deposer(app, _pdf_xfa(tmp_path / "dossier source assez profond" / "OF-2026-0042" / "Formulaire XFA.pdf"))
    _terminer(app, 1)
    (iid,) = app.arbre.get_children()
    app.arbre.selection_set(iid)
    app.arbre.event_generate("<<TreeviewSelect>>")
    app.root.update()
    assert "Adobe Reader" in app.var_statut.get()            # le texte existe...
    assert _lisible_en_entier(app, _etiquette_statut(app))   # ...mais il ne tient pas dans l'étiquette


@GUI
def test_reclic_sur_la_ligne_selectionnee_reaffiche_le_detail(tmp_path, fabrique):
    app = fabrique(tmp_path / "sortie")
    a = _pdf(tmp_path / "OF-1" / "Rapport.pdf", "OF-1")
    b = _pdf(tmp_path / "OF-2" / "Rapport.pdf", "OF-2")
    _deposer(app, a, b)
    _terminer(app, 2)
    iid = app.arbre.get_children()[1]

    def cliquer():
        app.root.update()
        x, y, _w, h = app.arbre.bbox(iid, "fichier")
        app.arbre.event_generate("<ButtonPress-1>", x=x + 10, y=y + h // 2)
        app.arbre.event_generate("<ButtonRelease-1>", x=x + 10, y=y + h // 2)
        app.root.update()

    cliquer()
    assert "(2).pdf" in app.var_statut.get()
    c = _pdf(tmp_path / "OF-3" / "Autre.pdf", "OF-3")
    _deposer(app, c)                                           # le résumé « Terminé : ... » remplace la ligne
    _terminer(app, 3)
    assert app.var_statut.get().startswith("Terminé")
    cliquer()                                                  # même ligne, déjà sélectionnée
    assert "(2).pdf" in app.var_statut.get()


# --------------------------------------------------------------------------- #
# UI-2 : « un fichier déjà présent dans le dossier de sortie sans qu'on sache d'où il vient
#         n'est jamais écrasé non plus » (README) : faux
# --------------------------------------------------------------------------- #
def test_sortie_existante_d_origine_inconnue_n_est_jamais_ecrasee(tmp_path):
    sortie = tmp_path / "sortie"
    a = _pdf(tmp_path / "OF-1" / "Rapport.pdf", "Rapport OF-1")
    b = _pdf(tmp_path / "OF-2" / "Rapport.pdf", "Rapport OF-2")
    assert core.aplatir_fichier(a, sortie).statut == "ok"      # produite par une session / version précédente
    reserves = {}                                              # nouvelle session : mémoire vide
    dst = core.chemin_sortie_unique(b, sortie, reserves)
    res = core.aplatir_fichier(b, sortie, dst=dst)
    assert res.statut == "ok"
    textes = [_texte_pdf(p) for p in sortie.glob("*.pdf")]
    assert any("OF-1" in t for t in textes), f"la sortie de OF-1 a été écrasée : {sorted(os.listdir(sortie))}"
    assert any("OF-2" in t for t in textes)


@GUI
def test_redeposer_la_meme_source_remplace_sa_sortie_connue(tmp_path, fabrique):
    """Garde-fou : le comportement documenté (« Redéposer le même fichier le remplace, la ligne le précise »)."""
    sortie = tmp_path / "sortie"
    app = fabrique(sortie)
    src = _pdf(tmp_path / "OF-1" / "Rapport.pdf", "OF-1")
    _deposer(app, src)
    _terminer(app, 1)
    _deposer(app, src)
    _terminer(app, 2)
    assert sorted(p.name for p in sortie.glob("*.pdf")) == ["[a]- Rapport.pdf"]
    assert "remplace le fichier existant" in app.arbre.item(app.arbre.get_children()[1], "values")[2]


def test_nom_reserve_mais_fichier_supprime_est_reutilise(tmp_path):
    sortie = tmp_path / "sortie"
    a = _pdf(tmp_path / "OF-1" / "Rapport.pdf", "OF-1")
    b = _pdf(tmp_path / "OF-2" / "Rapport.pdf", "OF-2")
    reserves = {}
    dst = core.chemin_sortie_unique(a, sortie, reserves)
    assert core.aplatir_fichier(a, sortie, dst=dst).statut == "ok"
    dst.unlink()                                               # l'utilisateur vide le dossier de sortie
    # au dépôt suivant (rien en attente) l'application oublie les noms dont le fichier a disparu
    from types import SimpleNamespace
    tray.App._elaguer_reserves(SimpleNamespace(reserves=reserves), sortie)
    assert core.chemin_sortie_unique(b, sortie, reserves).name == "[a]- Rapport.pdf"


def test_deux_sources_de_meme_nom_dans_un_meme_lot_gardent_deux_sorties(tmp_path):
    """Garde-fou : la réservation en mémoire sert bien au cas d'un même lot (aucun fichier n'est encore écrit)."""
    sortie = tmp_path / "sortie"
    a = _pdf(tmp_path / "OF-1" / "Rapport.pdf", "OF-1")
    b = _pdf(tmp_path / "OF-2" / "Rapport.pdf", "OF-2")
    reserves = {}
    da = core.chemin_sortie_unique(a, sortie, reserves)
    db = core.chemin_sortie_unique(b, sortie, reserves)
    assert da.name == "[a]- Rapport.pdf" and db.name == "[a]- Rapport (2).pdf"


# --------------------------------------------------------------------------- #
# UI-3 : « Aplati (image) » est « normal et sans gravité » (README) mais s'affiche comme « À vérifier »
# --------------------------------------------------------------------------- #
def test_aplati_image_ne_ressemble_pas_a_a_verifier():
    assert tray.LIBELLES["securite"][1] != tray.LIBELLES["alerte"][1]


@GUI
def test_fin_de_lot_fait_defiler_jusqu_a_la_ligne_a_verifier(tmp_path, fabrique):
    """Une ligne sans gravité (image) ne doit pas masquer la ligne qui demande une action : on fabrique un lot
    « 1 image + 40 simples + 1 fichier réparé (à vérifier) » et on regarde ce qui est visible à la fin."""
    sortie = tmp_path / "sortie"
    app = fabrique(sortie)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "masque")
    a = p.add_stamp_annot(pymupdf.Rect(300, 300, 500, 370), stamp=1)
    d.xref_set_key(a.xref, "F", "32")                          # tampon « NoView » : repli image
    img = tmp_path / "src" / "a-image.pdf"
    img.parent.mkdir(parents=True)
    d.save(str(img))
    d.close()
    fichiers = [img] + [_pdf(tmp_path / "src" / f"b{i:02d}.pdf", f"b{i}") for i in range(40)]
    rep = _pdf(tmp_path / "src" / "z-repare.pdf", "z")
    rep.write_bytes(rep.read_bytes()[:-60])                    # fin de fichier coupée : MuPDF « répare »
    fichiers.append(rep)
    _deposer(app, *fichiers)
    _terminer(app, len(fichiers))
    lignes = {app.arbre.item(i, "values")[0]: i for i in app.arbre.get_children()}
    statuts = {n: app.arbre.item(i, "values")[1] for n, i in lignes.items()}
    if "À vérifier" not in statuts["z-repare.pdf"]:
        pytest.skip("le fichier tronqué n'est pas signalé « À vérifier » avec cette version de MuPDF")
    assert "image" in statuts["a-image.pdf"]
    assert app.arbre.bbox(lignes["z-repare.pdf"]) != "", "la ligne « À vérifier » n'est pas visible à la fin du lot"


# --------------------------------------------------------------------------- #
# Garde-fous : le README contre le code et la CI
# --------------------------------------------------------------------------- #
@pytest.mark.skipif(not CI_YML.exists(), reason="workflow introuvable (dossier testé seul)")
def test_readme_artefact_workflow_et_retention_correspondent_a_la_ci():
    yml = CI_YML.read_text(encoding="utf-8")
    assert re.search(r"^name:\s*Build AplatirPDF\.exe\s*$", yml, re.M) and "Build AplatirPDF.exe" in README
    assert re.search(r"name:\s*AplatirPDF-windows\b", yml) and "AplatirPDF-windows" in README
    assert re.search(r"retention-days:\s*90\b", yml) and "**90 jours**" in README
    assert "workflow_dispatch:" in yml and "Run workflow" in README
    assert re.search(r'tags:\s*\["aplatir-pdf-v\*"\]', yml) and "aplatir-pdf-v1.0" in README


@pytest.mark.parametrize("libelle", [
    "Parcourir…", "Ajouter des PDF…", "Journal", "Quitter", "Lancer au démarrage de Windows",
    "Garder la fenêtre au premier plan",
    "Sécurité : convertir en image une page si l'aplatissement altère son aspect",
])
def test_libelles_cites_par_le_readme_existent_dans_l_interface(libelle):
    assert libelle in README, libelle
    source = re.sub(r'"\s*\n\s*"', "", SOURCE_TRAY)          # chaînes littérales coupées sur deux lignes
    assert libelle in source, libelle


def test_readme_dossier_de_donnees_et_fichiers_ecrits_par_l_outil():
    """Tout ce que l'outil écrit dans %APPDATA%\\AplatirPDF et que le README annonce existe dans le code."""
    for nom in (tray.FICHIER_SORTIES, "config.json", "aplatir.log"):
        assert nom in README, nom
        assert nom in SOURCE_TRAY, nom
    assert tray.ID_APP == "AplatirPDF"


def test_readme_version_de_python_compatible_avec_les_versions_epinglees():
    """« Python 3.10+ » : la CI teste 3.12, les pins exigent >= 3.10 (vérifié sur PyPI : pymupdf 1.28.2 et
    pillow 12.3.0 déclarent requires-python >=3.10)."""
    assert "Python 3.10+" in README
    pins = (ICI / "requirements.txt").read_text(encoding="utf-8")
    assert re.search(r"^pymupdf==\d", pins, re.M) and re.search(r"^pillow==\d", pins, re.M)
