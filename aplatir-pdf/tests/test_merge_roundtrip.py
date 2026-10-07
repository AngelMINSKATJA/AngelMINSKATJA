"""Promesse de bout en bout : « après aplatissement, la fusion ne perd plus les signatures ».

Les PDF sont écrits OCTET PAR OCTET (pas d'API PyMuPDF) pour maîtriser la structure exacte
(ressources héritées du noeud /Pages, etc.). Les contrôles utilisent, quand ils sont
installés, des outils INDEPENDANTS de MuPDF (pypdfium2 = moteur de Chrome, pypdf, qpdf,
pdfunite) : on y rejoue la fusion et on regarde les pixels. Sans ces outils, le test est ignoré.
"""
import shutil
import subprocess

import pymupdf
import pytest

import aplatir_core as core

CENTRE_GAUCHE = (150, 792 - 530)    # moitié gauche de la signature (image rouge opaque)
CENTRE_DROIT = (250, 792 - 530)     # moitié droite (image rendue transparente par /SMask -> fond jaune)
ROUGE, JAUNE = (255, 0, 0), (255, 255, 0)


# --------------------------------------------------------------------------- #
# Fabrication brute d'un PDF
# --------------------------------------------------------------------------- #
def _pdf_brut(objets: dict) -> bytes:
    """``objets`` : numéro -> corps (bytes) ou (dict_bytes, flux_bytes)."""
    sortie = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = {}
    for num in sorted(objets):
        offsets[num] = len(sortie)
        corps = objets[num]
        if isinstance(corps, tuple):
            d, flux = corps
            d = d.rstrip()
            assert d.startswith(b"<<") and d.endswith(b">>")
            d = d[:-2] + b"/Length %d>>" % len(flux)
            sortie += b"%d 0 obj\n%s\nstream\n%s\nendstream\nendobj\n" % (num, d, flux)
        else:
            sortie += b"%d 0 obj\n%s\nendobj\n" % (num, corps)
    debut_xref = len(sortie)
    n = max(objets) + 1
    sortie += b"xref\n0 %d\n0000000000 65535 f \n" % n
    for i in range(1, n):
        sortie += (b"%010d 00000 n \n" % offsets[i]) if i in offsets else b"0000000000 65535 f \n"
    sortie += b"trailer\n<</Size %d/Root 1 0 R>>\nstartxref\n%d\n%%%%EOF\n" % (n, debut_xref)
    return bytes(sortie)


def _pdf_signe(ressources_heritees: str | None, nb_pages: int = 3, page_signee: int = 2) -> bytes:
    """Document de ``nb_pages`` pages de texte ; la page ``page_signee`` porte une signature SIGNEE.

    ``ressources_heritees`` : texte du /Resources placé sur le noeud /Pages (les pages n'ont
    alors aucun /Resources propre). ``None`` : chaque page a ses ressources.
    Apparence = image RVB 2x1 (rouge) dont le /SMask rend la moitié droite transparente.
    """
    police = b"<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"
    o = {
        1: b"<</Type/Catalog/Pages 2 0 R/AcroForm 20 0 R>>",
        26: police,
        20: b"<</Fields[7 0 R]/SigFlags 3>>",
        21: (b"<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached"
             b"/ByteRange[0 0 0 0]/Contents<" + b"00" * 32 + b">>>"),
        7: (b"<</Type/Annot/Subtype/Widget/FT/Sig/T(Sig1)/V 21 0 R/F 4/Rect[100 500 300 560]"
            b"/P %d 0 R/AP<</N 8 0 R>>>>" % (2 + page_signee)),
        8: (b"<</Type/XObject/Subtype/Form/BBox[0 0 200 60]/Resources<</XObject<</Im0 9 0 R>>>>>>",
            b"q 200 0 0 60 0 0 cm /Im0 Do Q"),
        9: (b"<</Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace/DeviceRGB"
            b"/BitsPerComponent 8/SMask 10 0 R>>", bytes([255, 0, 0, 0, 0, 255])),
        10: (b"<</Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace/DeviceGray"
             b"/BitsPerComponent 8>>", bytes([255, 0])),
    }
    kids = []
    for i in range(1, nb_pages + 1):
        num_page, num_contenu = 2 + i, 30 + i
        kids.append(b"%d 0 R" % num_page)
        txt = b"BT /F1 24 Tf 72 720 Td (Texte de la page %d) Tj ET\n" % i
        txt += b"q 1 1 0 rg 100 500 200 60 re f Q\n"            # fond jaune sous la signature
        o[num_contenu] = (b"<<>>", txt)
        page = b"<</Type/Page/Parent 2 0 R/Contents %d 0 R" % num_contenu
        if ressources_heritees is None:
            page += b"/Resources<</Font<</F1 26 0 R>>>>/MediaBox[0 0 612 792]"
        if i == page_signee:
            page += b"/Annots[7 0 R]"
        o[num_page] = page + b">>"
    pages = b"<</Type/Pages/Count %d/Kids[%s]" % (nb_pages, b" ".join(kids))
    if ressources_heritees is not None:
        pages += b"/MediaBox[0 0 612 792]/Resources " + ressources_heritees.encode()
    o[2] = pages + b">>"
    return _pdf_brut(o)


def _pixel(chemin, num_page: int, point, annots: bool = False):
    d = pymupdf.open(str(chemin))
    try:
        pix = d[num_page].get_pixmap(dpi=72, annots=annots, alpha=False)
        return pix.pixel(*point)[:3]
    finally:
        d.close()


def _aplatir(tmp_path, contenu: bytes, nom="rapport.pdf"):
    src = tmp_path / nom
    src.write_bytes(contenu)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut in ("ok", "securite"), res.message
    return res


def _pixels_pdfium(chemin, num_page: int, point):
    pdfium = pytest.importorskip("pypdfium2")
    pdf = pdfium.PdfDocument(str(chemin))
    try:
        img = pdf[num_page].render(scale=1, may_draw_forms=False, draw_annots=False).to_pil().convert("RGB")
        return img.getpixel(point)
    finally:
        pdf.close()


# --------------------------------------------------------------------------- #
# MERGE-1 : /Resources direct hérité du noeud /Pages
# --------------------------------------------------------------------------- #
RESSOURCES_DIRECTES = "<</Font<</F1 26 0 R>>>>"


@pytest.mark.xfail(strict=True, reason="MERGE-1 (prolonge ANN-1) : /Resources direct herite du noeud /Pages : apres bake + "
                   "save(garbage=3) les polices de TOUTES les pages (meme sans annotation) pointent vers de "
                   "mauvais objets (/F1 -> XObject de la signature) ; poppler perd tout le texte")
def test_ressources_directes_heritees_polices_intactes(tmp_path):
    res = _aplatir(tmp_path, _pdf_signe(RESSOURCES_DIRECTES))
    d = pymupdf.open(str(res.dst))
    try:
        for page in d:
            assert page.get_fonts(), f"page {page.number + 1} : plus aucune police resolue"
            for f in page.get_fonts():
                assert d.xref_get_key(f[0], "Type")[1] == "/Font"
    finally:
        d.close()


def test_ressources_indirectes_heritees_ok(tmp_path):
    """Même structure mais /Resources hérité INDIRECT : fonctionne (régression)."""
    d = pymupdf.open("pdf", _pdf_signe(RESSOURCES_DIRECTES))
    try:
        nouveau = d.get_new_xref()
        d.update_object(nouveau, RESSOURCES_DIRECTES)
        d.xref_set_key(2, "Resources", f"{nouveau} 0 R")
        chemin = tmp_path / "indirect.pdf"
        d.save(str(chemin))
    finally:
        d.close()
    res = core.aplatir_fichier(chemin, tmp_path / "out")
    assert res.statut == "ok", res.message
    assert _pixel(res.dst, 1, CENTRE_GAUCHE) == ROUGE
    assert _pixel(res.dst, 1, CENTRE_DROIT) == JAUNE


# --------------------------------------------------------------------------- #
# Fusion du résultat avec des outils indépendants (régressions)
# --------------------------------------------------------------------------- #
@pytest.fixture
def aplati(tmp_path):
    res = _aplatir(tmp_path, _pdf_signe(None))
    return res.dst


def test_sortie_sans_element_interactif(aplati):
    d = pymupdf.open(str(aplati))
    try:
        assert d.page_count == 3
        assert core.analyser(d).pages == {}
        assert "/AcroForm" not in d.xref_object(d.pdf_catalog())
    finally:
        d.close()


def test_fusion_pymupdf_et_pdfium(tmp_path, aplati):
    fusion = tmp_path / "fusion.pdf"
    m = pymupdf.open()
    m.insert_pdf(pymupdf.open(str(aplati)))
    m.insert_pdf(pymupdf.open(str(aplati)))
    m.save(str(fusion))
    m.close()
    # annotations NON dessinées : seul le contenu de page compte (page 2 et page 5 de la fusion)
    for n in (1, 4):
        assert _pixel(fusion, n, CENTRE_GAUCHE) == ROUGE
        assert _pixel(fusion, n, CENTRE_DROIT) == JAUNE
    pytest.importorskip("pypdfium2")
    for n in (1, 4):
        r, g, b = _pixels_pdfium(fusion, n, CENTRE_GAUCHE)
        assert r > 200 and g < 60 and b < 60
        assert _pixels_pdfium(fusion, n, CENTRE_DROIT)[2] < 60


def test_fusion_pypdf_rendu_pdfium(tmp_path, aplati):
    pypdf = pytest.importorskip("pypdf")
    pytest.importorskip("pypdfium2")
    w = pypdf.PdfWriter()
    w.append(str(aplati))
    w.append(str(aplati))
    fusion = tmp_path / "fusion_pypdf.pdf"
    with open(fusion, "wb") as f:
        w.write(f)
    assert len(pypdf.PdfReader(str(fusion)).pages) == 6
    r, g, b = _pixels_pdfium(fusion, 1, CENTRE_GAUCHE)
    assert r > 200 and g < 60 and b < 60
    assert _pixels_pdfium(fusion, 4, CENTRE_DROIT)[2] < 60


@pytest.mark.skipif(not shutil.which("qpdf"), reason="qpdf absent")
def test_fusion_qpdf(tmp_path, aplati):
    fusion = tmp_path / "fusion_qpdf.pdf"
    subprocess.run(["qpdf", "--empty", "--pages", str(aplati), "1-z", str(aplati), "1-z", "--",
                    str(fusion)], check=True, capture_output=True)
    assert _pixel(fusion, 1, CENTRE_GAUCHE) == ROUGE
    assert _pixel(fusion, 4, CENTRE_DROIT) == JAUNE
    chk = subprocess.run(["qpdf", "--check", str(aplati)], capture_output=True, text=True)
    assert chk.returncode == 0 and "WARNING" not in chk.stderr + chk.stdout


@pytest.mark.skipif(not shutil.which("pdfunite"), reason="pdfunite absent")
def test_fusion_pdfunite(tmp_path, aplati):
    fusion = tmp_path / "fusion_unite.pdf"
    subprocess.run(["pdfunite", str(aplati), str(aplati), str(fusion)], check=True, capture_output=True)
    assert _pixel(fusion, 1, CENTRE_GAUCHE) == ROUGE
    assert _pixel(fusion, 5, CENTRE_DROIT) == JAUNE


# --------------------------------------------------------------------------- #
# MERGE-4 : scripts du formulaire conservés alors que les champs n'existent plus
# --------------------------------------------------------------------------- #
def _pdf_avec_javascript(chemin):
    d = pymupdf.open("pdf", _pdf_signe(None))
    cat = d.pdf_catalog()
    d.xref_set_key(cat, "OpenAction", "<</S/JavaScript/JS(app.alert\\(this.getField\\('Sig1'\\).name\\);)>>")
    d.xref_set_key(cat, "Names", "<</JavaScript<</Names[(init)<</S/JavaScript/JS(var f = this.getField\\('Sig1'\\);)>>]>>>>")
    d.xref_set_key(d[1].xref, "AA", "<</O<</S/JavaScript/JS(this.getField\\('Sig1'\\).setFocus\\(\\);)>>>>")
    d.save(str(chemin))
    d.close()


@pytest.mark.xfail(strict=True, reason="MERGE-4 : /OpenAction, /Names/JavaScript et /AA de page (qui appellent "
                   "this.getField sur des champs supprimés par bake) restent dans le résultat")
def test_javascript_de_formulaire_supprime(tmp_path):
    src = tmp_path / "js.pdf"
    _pdf_avec_javascript(src)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    d = pymupdf.open(str(res.dst))
    try:
        cat = d.pdf_catalog()
        assert d.xref_get_key(cat, "AcroForm")[0] == "null"                 # (déjà vrai)
        t, v = d.xref_get_key(cat, "OpenAction")
        assert t == "null" or "JavaScript" not in (d.xref_object(int(v.split()[0])) if t == "xref" else v)
        assert d.xref_get_key(cat, "Names/JavaScript")[0] == "null"
        assert d.xref_get_key(d[1].xref, "AA")[0] == "null"
    finally:
        d.close()


# --------------------------------------------------------------------------- #
# Rendu pdfium (moteur indépendant de MuPDF) : avant aplatissement == après aplatissement
# --------------------------------------------------------------------------- #
def _pdf_annotations_variees() -> bytes:
    """Signature (image + SMask), surligneur en mode Multiply, tampon semi-transparent."""
    base = pymupdf.open("pdf", _pdf_signe(None, nb_pages=1, page_signee=1))
    try:
        gs = base.get_new_xref()
        base.update_object(gs, "<</Type/ExtGState/BM/Multiply/ca 1/CA 1>>")
        gs2 = base.get_new_xref()
        base.update_object(gs2, "<</Type/ExtGState/ca .5/CA .5>>")
        ap1, ap2 = base.get_new_xref(), base.get_new_xref()
        base.update_object(ap1, "<</Type/XObject/Subtype/Form/BBox[0 0 260 20]/Resources<</ExtGState<</G0 %d 0 R>>>>>>" % gs)
        base.update_stream(ap1, b"/G0 gs 1 1 0 rg 0 0 260 20 re f")
        base.update_object(ap2, "<</Type/XObject/Subtype/Form/BBox[0 0 150 70]/Resources<</ExtGState<</G0 %d 0 R>>>>>>" % gs2)
        base.update_stream(ap2, b"/G0 gs 0 .6 0 rg 0 0 150 70 re f 1 0 0 rg 20 20 60 30 re f")
        hl, st = base.get_new_xref(), base.get_new_xref()
        base.update_object(hl, "<</Type/Annot/Subtype/Highlight/Rect[72 710 332 730]/QuadPoints[72 730 332 730 72 710 332 710]"
                               "/C[1 1 0]/F 4/AP<</N %d 0 R>>>>" % ap1)
        base.update_object(st, "<</Type/Annot/Subtype/Stamp/Rect[320 400 470 470]/F 4/AP<</N %d 0 R>>>>" % ap2)
        page = base[0]
        annots = base.xref_get_key(page.xref, "Annots")[1].rstrip("]")
        base.xref_set_key(page.xref, "Annots", "%s %d 0 R %d 0 R]" % (annots, hl, st))
        return base.tobytes()
    finally:
        base.close()


def test_rendu_pdfium_identique_avant_apres(tmp_path):
    pdfium = pytest.importorskip("pypdfium2")
    from PIL import ImageChops
    src = tmp_path / "varie.pdf"
    src.write_bytes(_pdf_annotations_variees())
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message

    def rendu(chemin, avec_annots):
        pdf = pdfium.PdfDocument(str(chemin))
        try:
            if avec_annots:
                pdf.init_forms()
            return pdf[0].render(scale=1, may_draw_forms=avec_annots, draw_annots=avec_annots).to_pil().convert("RGB")
        finally:
            pdf.close()

    avant = rendu(src, True)
    apres = rendu(res.dst, False)       # annotations NON dessinées : tout doit être dans le contenu de la page
    assert avant.size == apres.size
    r, g, b = ImageChops.difference(avant, apres).split()
    diff = sum(ImageChops.lighter(ImageChops.lighter(r, g), b).histogram()[61:])
    assert diff < 150, f"{diff} pixels differents entre l'original (annotations) et le resultat aplati"
