"""Annotations non-formulaire : ce que ``bake`` fait de chaque type, et la sensibilité du
contrôle visuel (``_marque_perdue``) dans les deux sens.

Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant ANN-n du rapport) ;
les autres sont des tests de non-régression.
"""
import re

import pymupdf
import pytest
from PIL import Image

import aplatir_core as core

ROUGE = "q 1 0 0 rg 0 0 100 40 re f Q"          # apparence : rectangle rouge 100 x 40
CENTRE = (350, 842 - 620)                         # centre (pt, origine en haut) d'une annotation [300 600 400 640]


# --------------------------------------------------------------------------- #
# Outils
# --------------------------------------------------------------------------- #
def _doc_base(nb_pages=1):
    d = pymupdf.open()
    for i in range(nb_pages):
        p = d.new_page()
        p.insert_text((72, 72), f"Rapport de fin de fabrication page {i + 1}", fontsize=14)
    return d


def _annot_brute(doc, page, corps, ap=None, bbox="0 0 100 40", ap_res="<<>>", ap_extra=""):
    """Ajoute une annotation écrite « à la main » (corps = contenu du dictionnaire)."""
    xref = doc.get_new_xref()
    if ap is not None:
        apx = doc.get_new_xref()
        doc.update_object(apx, f"<</Type/XObject/Subtype/Form/BBox[{bbox}]/Resources{ap_res}{ap_extra}>>")
        doc.update_stream(apx, ap.encode())
        corps += f"/AP<</N {apx} 0 R>>"
    doc.update_object(xref, f"<<{corps}>>")
    cur = doc.xref_get_key(page.xref, "Annots")
    if cur[0] == "array":
        doc.xref_set_key(page.xref, "Annots", f"[{cur[1].strip()[1:-1]} {xref} 0 R]")
    else:
        doc.xref_set_key(page.xref, "Annots", f"[{xref} 0 R]")
    return xref


def _tampon(doc, page, flags=4, extra="", rect="[300 600 400 640]"):
    return _annot_brute(doc, page, f"/Type/Annot/Subtype/Stamp/Rect{rect}/F {flags}{extra}", ROUGE)


def _enregistrer(doc, chemin):
    doc.save(str(chemin))
    doc.close()
    return chemin


def _rendu(chemin_ou_doc, page=0, annots=True, dpi=72):
    ouvert = not isinstance(chemin_ou_doc, pymupdf.Document)
    d = pymupdf.open(str(chemin_ou_doc)) if ouvert else chemin_ou_doc
    try:
        pix = d[page].get_pixmap(dpi=dpi, annots=annots, alpha=False)
        return Image.frombytes("RGB", (pix.width, pix.height), pix.samples)
    finally:
        if ouvert:
            d.close()


def _rouge(im):
    """Nombre de pixels franchement rouges."""
    return sum(1 for r, g, b in im.get_flattened_data() if r > 200 and g < 70 and b < 70) \
        if hasattr(im, "get_flattened_data") else \
        sum(1 for r, g, b in im.getdata() if r > 200 and g < 70 and b < 70)


def _bbox_rouge(im):
    """Boîte englobante des pixels franchement rouges (None s'il n'y en a pas)."""
    from PIL import ImageChops
    r, g, b = im.split()
    masque = ImageChops.darker(r.point(lambda v: 255 if v > 200 else 0),
                               ImageChops.darker(g.point(lambda v: 255 if v < 70 else 0),
                                                 b.point(lambda v: 255 if v < 70 else 0)))
    return masque.getbbox()


def _rouge_au_centre(im):
    r, g, b = im.getpixel(CENTRE)
    return r > 200 and g < 70 and b < 70


def _aplatir(src, tmp_path, **kw):
    return core.aplatir_fichier(src, tmp_path / "sortie", **kw)


# --------------------------------------------------------------------------- #
# ANN-1 : /Resources hérité (dictionnaire direct dans le nœud /Pages)
# --------------------------------------------------------------------------- #
def _pdf_ressources_heritees(chemin, nb_pages=3):
    """PDF écrit à la main : le nœud /Pages porte un /Resources DIRECT (hérité par les pages),
    les pages n'ont pas de /Resources, chacune porte un tampon rouge."""
    objs = {}
    n_pages = 2
    kids = []
    nxt = 5
    for i in range(nb_pages):
        page, cont, annot, ap = nxt, nxt + 1, nxt + 2, nxt + 3
        nxt += 4
        kids.append(page)
        objs[page] = (f"<</Type/Page/Parent {n_pages} 0 R/MediaBox[0 0 595 842]/Contents {cont} 0 R"
                      f"/Annots[{annot} 0 R]>>").encode()
        texte = f"BT /F1 20 Tf 72 700 Td (Page {i + 1}) Tj ET".encode()
        objs[cont] = b"<</Length %d>>\nstream\n" % len(texte) + texte + b"\nendstream"
        objs[annot] = (f"<</Type/Annot/Subtype/Stamp/Rect[300 600 400 640]/F 4/AP<</N {ap} 0 R>>>>").encode()
        flux = ROUGE.encode()
        objs[ap] = (b"<</Type/XObject/Subtype/Form/BBox[0 0 100 40]/Resources<<>>/Length %d>>\nstream\n"
                    % len(flux)) + flux + b"\nendstream"
    objs[1] = b"<</Type/Catalog/Pages 2 0 R>>"
    objs[2] = (f"<</Type/Pages/Count {nb_pages}/Kids[{' '.join(f'{k} 0 R' for k in kids)}]"
               f"/Resources<</Font<</F1 3 0 R>>>>>>").encode()
    objs[3] = b"<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>"
    objs[4] = b"<<>>"
    out = bytearray(b"%PDF-1.7\n")
    offsets = {}
    for num in sorted(objs):
        offsets[num] = len(out)
        out += b"%d 0 obj\n" % num + objs[num] + b"\nendobj\n"
    debut_xref = len(out)
    top = max(objs) + 1
    out += b"xref\n0 %d\n0000000000 65535 f \n" % top
    for num in range(1, top):
        out += b"%010d 00000 n \n" % offsets[num]
    out += b"trailer\n<</Size %d/Root 1 0 R>>\nstartxref\n%d\n%%%%EOF\n" % (top, debut_xref)
    chemin.write_bytes(bytes(out))


@pytest.mark.xfail(strict=True, reason="ANN-1: bake + save(garbage>=2) avec /Resources hérité direct "
                                       "-> références nulles, tampons perdus, statut 'ok'")
def test_ressources_heritees_tampons_conserves(tmp_path):
    src = tmp_path / "heritees.pdf"
    _pdf_ressources_heritees(src)
    avant = [_rouge_au_centre(_rendu(src, i)) for i in range(3)]
    assert avant == [True, True, True]            # le fichier d'origine est sain
    res = _aplatir(src, tmp_path)
    for i in range(3):
        assert _rouge_au_centre(_rendu(res.dst, i, annots=False)), \
            f"page {i + 1} : tampon perdu (statut {res.statut!r}, {res.message})"


# --------------------------------------------------------------------------- #
# ANN-2 : annotations invisibles à l'écran (NoView, /OC désactivé) qui deviennent visibles
# --------------------------------------------------------------------------- #
def _pdf_tampon(tmp_path, nom, flags=4, extra="", prepare=None):
    d = _doc_base()
    if prepare:
        prepare(d)
    _tampon(d, d[0], flags, extra)
    return _enregistrer(d, tmp_path / nom)


@pytest.mark.parametrize("flags", [32, 36], ids=["NoView", "NoView+Print"])
@pytest.mark.xfail(strict=True, reason="ANN-2: annotation NoView cuite dans la page -> devient visible")
def test_annotation_noview_reste_invisible(tmp_path, flags):
    src = _pdf_tampon(tmp_path, "noview.pdf", flags)
    assert not _rouge_au_centre(_rendu(src))      # MuPDF (et la norme) : invisible à l'écran
    res = _aplatir(src, tmp_path)
    assert not _rouge_au_centre(_rendu(res.dst, annots=False)), res.message


def _ocg_desactive(d):
    ocg = d.get_new_xref()
    d.update_object(ocg, "<</Type/OCG/Name(Calque)>>")
    d.xref_set_key(d.pdf_catalog(), "OCProperties",
                   f"<</OCGs[{ocg} 0 R]/D<</OFF[{ocg} 0 R]/Order[{ocg} 0 R]>>>>")
    return ocg


@pytest.mark.xfail(strict=True, reason="ANN-2: annotation dont /OC est désactivé -> devient visible")
def test_annotation_calque_masque_reste_invisible(tmp_path):
    d = _doc_base()
    ocg = _ocg_desactive(d)
    _tampon(d, d[0], 4, f"/OC {ocg} 0 R")
    src = _enregistrer(d, tmp_path / "oc.pdf")
    assert not _rouge_au_centre(_rendu(src))
    res = _aplatir(src, tmp_path)
    assert not _rouge_au_centre(_rendu(res.dst, annots=False)), res.message


@pytest.mark.xfail(strict=True, reason="ANN-2: champ « masqué mais imprimable » (F=36) -> devient visible")
def test_champ_noview_reste_invisible(tmp_path):
    d = _doc_base()
    w = pymupdf.Widget()
    w.field_type, w.field_name, w.field_value = pymupdf.PDF_WIDGET_TYPE_TEXT, "nom", "Jean"
    w.rect = pymupdf.Rect(300, 202, 400, 242)
    w.fill_color = (1, 0, 0)
    d[0].add_widget(w)
    d.xref_set_key([x for x in d[0].widgets()][0].xref, "F", "36")
    src = _enregistrer(d, tmp_path / "champ.pdf")
    assert _rouge(_rendu(src)) == 0
    res = _aplatir(src, tmp_path)
    assert _rouge(_rendu(res.dst, annots=False)) == 0, res.message


def test_annotation_cachee_reste_cachee(tmp_path):
    """F=2 (Hidden) : ni écran ni impression ; l'aplatissement ne doit pas la faire apparaître."""
    src = _pdf_tampon(tmp_path, "hidden.pdf", 2)
    res = _aplatir(src, tmp_path)
    assert res.statut in ("ok", "copie", "securite")
    assert not _rouge_au_centre(_rendu(res.dst, annots=False))


# --------------------------------------------------------------------------- #
# Détection de perte (sens « perdu ») et repli image
# --------------------------------------------------------------------------- #
def _pdf_noRotate_page_tournee(tmp_path):
    d = _doc_base()
    d.xref_set_key(d.page_xref(0), "Rotate", "90")
    _tampon(d, d[0], 20)                          # Print + NoRotate : cuit tel quel, il est mal orienté
    return _enregistrer(d, tmp_path / "norotate.pdf")


def test_noRotate_sur_page_tournee_detecte_et_rendu_conserve(tmp_path):
    src = _pdf_noRotate_page_tournee(tmp_path)
    ref = _bbox_rouge(_rendu(src))
    assert ref is not None
    res = _aplatir(src, tmp_path)
    assert res.statut in ("ok", "securite"), res.message
    sortie = _bbox_rouge(_rendu(res.dst, annots=False))
    assert sortie is not None
    assert all(abs(a - b) <= 4 for a, b in zip(ref, sortie)), (ref, sortie)   # même endroit, même orientation


def test_securite_desactivee_donne_une_alerte(tmp_path):
    src = _pdf_noRotate_page_tournee(tmp_path)
    res = _aplatir(src, tmp_path, securite=False)
    if res.statut == "ok":
        pytest.skip("cette version de MuPDF cuit correctement NoRotate : rien à détecter")
    assert res.statut == "alerte" and "à vérifier" in res.message
    assert res.pages_image == []


def test_perte_dans_ap_calque_masque_detectee(tmp_path):
    """/OC désactivé DANS l'apparence : MuPDF dessine l'annotation, mais une fois cuite elle
    disparaît -> doit être détecté et la page reconstruite en image (le tampon reste visible)."""
    d = _doc_base()
    ocg = _ocg_desactive(d)
    x = _tampon(d, d[0], 4)
    ap = int(d.xref_get_key(x, "AP")[1].split("/N")[1].split()[0])
    d.xref_set_key(ap, "OC", f"{ocg} 0 R")
    src = _enregistrer(d, tmp_path / "apoc.pdf")
    ref = _rouge(_rendu(src))
    assert ref > 1000
    res = _aplatir(src, tmp_path)
    assert res.statut in ("ok", "securite"), res.message
    assert _rouge(_rendu(res.dst, annots=False)) > 0.85 * ref


def test_tampon_sans_ap_au_nom_personnalise_reste_visible(tmp_path):
    """Tampon sans /AP : MuPDF en fabrique un à l'affichage mais ``bake`` le perd -> repli image."""
    d = _doc_base()
    _annot_brute(d, d[0], "/Type/Annot/Subtype/Stamp/Rect[100 600 300 660]/F 4/Name/SBApproved")
    src = _enregistrer(d, tmp_path / "sansap.pdf")
    zone = (100, 842 - 660, 300, 842 - 600)

    def marque(im):
        return im.crop(zone).convert("L").point(lambda v: 255 if v < 128 else 0).getbbox()

    if marque(_rendu(src)) is None:
        pytest.skip("cette version de MuPDF n'affiche pas les tampons sans apparence")
    res = _aplatir(src, tmp_path)
    assert res.statut in ("ok", "securite"), res.message
    assert marque(_rendu(res.dst, annots=False)) is not None, "le tampon a disparu"


# --------------------------------------------------------------------------- #
# Pas de faux positifs sur des documents sains (aucune conversion en image)
# --------------------------------------------------------------------------- #
def _page_dense(doc, mots=60):
    p = doc.new_page()
    for k in range(mots):
        p.insert_text((40, 50 + k * 12), "contrôle qualité conforme lot numéro dimension tolérance mesure " * 1, fontsize=9)
    return p


def test_pas_de_faux_positif_surlignage_multiply_et_transparence(tmp_path):
    d = pymupdf.open()
    p = _page_dense(d)
    _annot_brute(d, p, "/Type/Annot/Subtype/Highlight/Rect[40 600 400 640]/F 4/C[1 1 0]"
                       "/QuadPoints[40 640 400 640 40 600 400 600]",
                 "/G0 gs 1 1 0 rg 0 0 360 40 re f", "0 0 360 40", "<</ExtGState<</G0<</BM/Multiply>>>>>>")
    _annot_brute(d, p, "/Type/Annot/Subtype/Stamp/Rect[300 300 500 400]/F 4",
                 "/G0 gs 1 0 0 rg 0 0 200 100 re f 0 0 1 rg 50 20 100 60 re f", "0 0 200 100",
                 "<</ExtGState<</G0<</ca 0.5/CA 0.5>>>>>>")
    for k in range(10):
        p.add_highlight_annot(pymupdf.Rect(40, 100 + k * 30, 300, 112 + k * 30))
    p.add_ink_annot([[(100 + j * 3, 700 + (j % 7) * 4 * (-1) ** j) for j in range(80)]])
    src = _enregistrer(d, tmp_path / "sain.pdf")
    res = _aplatir(src, tmp_path)
    assert res.statut == "ok" and res.pages_image == [], res.message


def test_pas_de_faux_positif_beaucoup_de_pages(tmp_path):
    d = pymupdf.open()
    for _ in range(12):
        p = _page_dense(d, 40)
        for k in range(8):
            p.add_stamp_annot(pymupdf.Rect(40 + k * 60, 700, 90 + k * 60, 730), stamp=k)
        p.add_freetext_annot(pymupdf.Rect(72, 600, 300, 630), "Bon pour accord", fontsize=12)
    src = _enregistrer(d, tmp_path / "pages.pdf")
    res = _aplatir(src, tmp_path)
    assert res.statut == "ok" and res.pages_image == [], res.message
    out = pymupdf.open(str(res.dst))
    assert out.page_count == 12 and core.analyser(out).pages == {}
    out.close()


@pytest.mark.parametrize("flags", [0, 4, 12, 28])
def test_pas_de_faux_positif_drapeaux_usuels_page_droite(tmp_path, flags):
    src = _pdf_tampon(tmp_path, "f.pdf", flags)
    res = _aplatir(src, tmp_path)
    assert res.statut == "ok" and res.pages_image == [], res.message
    assert _rouge_au_centre(_rendu(res.dst, annots=False))


@pytest.mark.xfail(strict=True, reason="ANN-5: note (Text) sans /AP, Rect 20x20 : la page entière "
                                       "est convertie en image alors que rien n'est perdu")
def test_note_sans_ap_ne_declenche_pas_le_repli_image(tmp_path):
    d = _doc_base()
    _annot_brute(d, d[0], "/Type/Annot/Subtype/Text/Rect[100 600 120 620]/F 4/Contents(hi)/Name/Note")
    src = _enregistrer(d, tmp_path / "note.pdf")
    res = _aplatir(src, tmp_path)
    assert res.statut == "ok" and res.pages_image == [], res.message


# --------------------------------------------------------------------------- #
# Redact, liens, notes : comportement de bake
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("kw", [{}, {"fill": (0, 0, 0)}, {"text": "CACHE", "fill": (1, 1, 1)}],
                         ids=["plain", "fill", "overlay"])
def test_redact_ne_supprime_pas_le_contenu(tmp_path, kw):
    d = _doc_base()
    d[0].add_redact_annot(pymupdf.Rect(72, 60, 300, 80), **kw)
    src = _enregistrer(d, tmp_path / "redact.pdf")
    res = _aplatir(src, tmp_path)
    assert res.statut in ("ok", "securite"), res.message
    out = pymupdf.open(str(res.dst))
    try:
        assert "Rapport de fin de fabrication" in out[0].get_text()
        assert core.analyser(out).pages == {}
    finally:
        out.close()


def test_liens_conserves_sur_page_cuite(tmp_path):
    d = _doc_base()
    p = d[0]
    p.insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(72, 60, 300, 80), "uri": "https://example.org"})
    p.insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(72, 120, 300, 140), "page": 0,
                   "to": pymupdf.Point(0, 0)})
    p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    src = _enregistrer(d, tmp_path / "liens.pdf")
    res = _aplatir(src, tmp_path)
    assert res.statut == "ok", res.message
    out = pymupdf.open(str(res.dst))
    try:
        assert sorted(l["kind"] for l in out[0].get_links()) == [pymupdf.LINK_GOTO, pymupdf.LINK_URI]
    finally:
        out.close()


# --------------------------------------------------------------------------- #
# ANN-3 : pièces jointes d'annotation
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="ANN-3: le fichier joint d'une annotation FileAttachment est détruit "
                                       "sans avertissement (statut 'ok')")
def test_piece_jointe_d_annotation_conservee_ou_signalee(tmp_path):
    d = _doc_base()
    d[0].add_file_annot((400, 200), b"MESURES;1;2;3\n" * 20, "mesures.csv", desc="mesures")
    src = _enregistrer(d, tmp_path / "pj.pdf")
    res = _aplatir(src, tmp_path)
    out = pymupdf.open(str(res.dst))
    try:
        conserve = "mesures.csv" in out.embfile_names()
    finally:
        out.close()
    assert conserve or res.statut == "alerte", (res.statut, res.message)


# --------------------------------------------------------------------------- #
# ANN-4 : liens internes après repli image
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="ANN-4: repli image -> liens internes vers la page remplacée "
                                       "supprimés, liens de cette page retargetés sur elle-même")
def test_liens_internes_apres_repli_image(tmp_path, monkeypatch):
    d = pymupdf.open()
    for i in range(3):
        d.new_page().insert_text((72, 72), f"Page {i + 1}", fontsize=20)
    d[0].insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(72, 100, 200, 130), "page": 1,
                      "to": pymupdf.Point(0, 0)})
    d[1].insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(72, 200, 200, 230), "page": 2,
                      "to": pymupdf.Point(0, 0)})
    d[2].insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(72, 100, 200, 130), "page": 1,
                      "to": pymupdf.Point(0, 0)})
    d[1].add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    src = _enregistrer(d, tmp_path / "internes.pdf")
    monkeypatch.setattr(core, "_pages_alterees", lambda *a, **k: [2])   # force le repli sur la page 2
    res = _aplatir(src, tmp_path)
    assert res.statut == "securite" and res.pages_image == [2]
    out = pymupdf.open(str(res.dst))
    try:
        cibles = [[l.get("page") for l in out[i].get_links()] for i in range(3)]
    finally:
        out.close()
    assert cibles == [[1], [2], [1]], cibles
