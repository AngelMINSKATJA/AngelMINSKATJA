# -*- coding: utf-8 -*-
"""Audit « exigences » : le livrable répond-il mot pour mot à la demande de l'utilisateur ?

  « glisser-déposer le ou les PDF ... le programme les aplatit direct, puis les enregistre avec le même nom de
    fichier plus le préfixe "[a]- " dans le répertoire de sortie renseigné au préalable »

Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant REQ-n du rapport).
"""
import hashlib
import re
from pathlib import Path

import pymupdf
import pytest

import aplatir_core as core

RACINE = Path(__file__).resolve().parents[1]
README = RACINE / "README.md"
WORKFLOW = RACINE.parent / ".github" / "workflows" / "build-aplatir-pdf.yml"


def _pdf(chemin, pages=1, tamponnees=(), texte="Rapport"):
    chemin = Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    for i in range(pages):
        p = d.new_page()
        p.insert_text((72, 72), f"{texte} p{i + 1}")
        if i + 1 in tamponnees:
            p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


def _sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


# --------------------------------------------------------------------------- #
# Régression : nom de sortie EXACT (« [a]- » + espace + même nom, même extension)
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("nom", [
    "Rapport.pdf",
    "Rapport de Fin de Fabrication OF-2024-00123 - indice B.pdf",
    "RAPPORT SIGNÉ é à ç.PDF",            # extension en capitales conservée telle quelle
    "a  b   c.pdf",                       # espaces multiples conservés
    "[a] déjà crochets.pdf",              # « [a] » sans tiret : n'est PAS le préfixe
])
@pytest.mark.parametrize("tampon", [False, True])
def test_nom_de_sortie_est_prefixe_espace_plus_nom_exact(tmp_path, nom, tampon):
    src = _pdf(tmp_path / "in" / nom, tamponnees=(1,) if tampon else ())
    res = core.aplatir_fichier(src, tmp_path / "sortie")
    assert res.statut in ("ok", "copie"), res.message
    assert [p.name for p in (tmp_path / "sortie").iterdir()] == ["[a]- " + nom]
    assert res.dst.name == "[a]- " + nom
    assert res.dst.name[:5] == "[a]- " and res.dst.name[4] == " "


def test_plusieurs_pdf_deposes_tous_produits_et_originaux_intacts(tmp_path):
    sources = [_pdf(tmp_path / "in" / "A signé.pdf", tamponnees=(1,)),
               _pdf(tmp_path / "in" / "B sans signature.pdf"),
               _pdf(tmp_path / "in" / "C trois pages.pdf", pages=3, tamponnees=(2, 3))]
    avant = {p: _sha(p) for p in sources}
    sortie = tmp_path / "sortie"
    pdfs, ignores = core.collecter_pdf(sources, exclure=sortie)
    assert pdfs == sources and not ignores
    resultats = [core.aplatir_fichier(p, sortie) for p in pdfs]
    assert [r.statut for r in resultats] == ["ok", "copie", "ok"]
    assert sorted(p.name for p in sortie.iterdir()) == sorted("[a]- " + p.name for p in sources)
    assert {p: _sha(p) for p in sources} == avant          # « fichiers d'origine jamais modifiés »
    for r in resultats:
        d = pymupdf.open(str(r.dst))
        try:
            assert not core.analyser(d).pages
        finally:
            d.close()


# --------------------------------------------------------------------------- #
# Cohérence README <-> code <-> CI (le non-développeur ne dispose que du README)
# --------------------------------------------------------------------------- #
def test_readme_cite_les_memes_noms_que_le_code_et_la_ci():
    tray = pytest.importorskip("aplatir_tray")
    texte = README.read_text(encoding="utf-8")
    assert core.PREFIXE in texte                                   # « [a]- » documenté
    assert f"%APPDATA%\\{tray.ID_APP}\\config.json" in texte       # emplacement de la configuration
    assert f"%APPDATA%\\{tray.ID_APP}\\aplatir.log" in texte       # emplacement du journal
    if not WORKFLOW.is_file():
        pytest.skip("workflow absent (arborescence différente)")
    wf = WORKFLOW.read_text(encoding="utf-8")
    nom_wf = re.search(r"^name:\s*(.+?)\s*$", wf, re.M).group(1)
    nom_artefact = re.search(r"name:\s*(AplatirPDF-\S+)", wf).group(1)
    assert f"**{nom_wf}**" in texte, "le README doit citer le nom exact du workflow tel qu'affiché par GitHub"
    assert f"**{nom_artefact}**" in texte, "le README doit citer le nom exact de l'artefact à télécharger"
    assert "AplatirPDF.exe" in wf


# --------------------------------------------------------------------------- #
# REQ-3 : le rapport d'origine donnait, fichier par fichier, les PAGES portant une signature
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="REQ-3: l'ancien script listait « page N: SIGNATURE ... » par fichier "
                                       "(aplatir_rapport.txt) ; ici le message affiché ne donne qu'un NOMBRE de "
                                       "pages (« 3 page(s) traitée(s) »), les numéros ne sont que dans le journal")
def test_le_message_donne_les_numeros_de_pages_traitees(tmp_path):
    src = _pdf(tmp_path / "in" / "r.pdf", pages=6, tamponnees=(2, 4, 5))
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok" and res.pages_elements == [2, 4, 5]
    nombres = re.findall(r"\d+", res.message)
    assert {"2", "4", "5"} <= set(nombres), res.message
