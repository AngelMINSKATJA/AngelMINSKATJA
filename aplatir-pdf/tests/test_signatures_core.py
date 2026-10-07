"""Signatures électroniques réalistes : l'apparence survit-elle à l'aplatissement ?

Les PDF sont fabriqués à la main avec l'API bas niveau de PyMuPDF (comme le ferait Adobe
Sign / DocuSign / Acrobat) : widget de signature SIGNÉ (/V -> /Type/Sig), apparence /AP /N =
formulaire contenant une image avec /SMask + un tracé vectoriel, etc.

Les pixels attendus sont calculés à partir de la GÉOMÉTRIE DE LA FIXTURE (algorithme
ISO 32000-1 §12.5.5 : BBox x Matrix -> Rect, puis rotation de page, CropBox...), pas à partir
d'un second rendu du PDF d'origine.
"""
import pymupdf
import pytest

import aplatir_core as core

ROUGE, JAUNE, BLEU, VERT = (255, 0, 0), (255, 255, 0), (0, 0, 255), (0, 153, 0)
SCALE = 2
RECT = (100, 600, 300, 650)

SIG_DICT = ("<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached/ByteRange[0 0 0 0]"
            "/Contents<" + "00" * 64 + ">/M(D:20260301120000+01'00')/Name(Jean Dupont)>>")


# --------------------------------------------------------------------------- #
# Fabrication des PDF
# --------------------------------------------------------------------------- #
def _obj(d, corps, flux=None):
    x = d.get_new_xref()
    d.update_object(x, corps)
    if flux is not None:
        d.update_stream(x, flux)
    return x


def nouveau_doc(pages=1, mediabox=(0, 0, 612, 792), rotate=0, cropbox=None):
    d = pymupdf.open()
    for i in range(pages):
        p = d.new_page(width=mediabox[2] - mediabox[0], height=mediabox[3] - mediabox[1])
        p.insert_text((72, 72), f"Rapport de fin de fabrication - page {i + 1}", fontsize=14)
    for p in d:
        if mediabox != (0, 0, 612, 792):
            d.xref_set_key(p.xref, "MediaBox", "[%g %g %g %g]" % mediabox)
        if cropbox:
            d.xref_set_key(p.xref, "CropBox", "[%g %g %g %g]" % cropbox)
        if rotate:
            d.xref_set_key(p.xref, "Rotate", str(rotate))
    return d


def fond(d, page, rect, rgb=(1, 1, 0)):
    """Rectangle plein (coordonnées PDF) sous la zone de signature : révèle la transparence."""
    x = _obj(d, "<<>>", b"q %g %g %g rg %g %g %g %g re f Q\n"
             % (rgb + (rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1])))
    anciens = " ".join("%d 0 R" % o for o in page.get_contents())
    d.xref_set_key(page.xref, "Contents", "[%s %d 0 R]" % (anciens, x))


def apparence(d, w, h, matrix=None, bbox=None, imbrique=False):
    """AP /N type e-signature. Contenu défini relativement à la BBox :
    image rouge à gauche (moitié gauche de l'image opaque, moitié droite transparente via /SMask)
    + rectangle bleu vectoriel sur [.6,.9] x [.3,.7]."""
    bb = bbox or (0, 0, w, h)
    W, H = bb[2] - bb[0], bb[3] - bb[1]
    masque = _obj(d, "<</Type/XObject/Subtype/Image/Width 8/Height 8/ColorSpace/DeviceGray/BitsPerComponent 8>>",
                  bytes(([255] * 4 + [0] * 4) * 8))
    image = _obj(d, "<</Type/XObject/Subtype/Image/Width 8/Height 8/ColorSpace/DeviceRGB"
                    "/BitsPerComponent 8/SMask %d 0 R>>" % masque, bytes([255, 0, 0] * 64))
    flux = (b"q %g 0 0 %g %g %g cm /Im0 Do Q\n" % (W / 2, H, bb[0], bb[1])
            + b"0 0 1 rg %g %g %g %g re f\n" % (bb[0] + W * .6, bb[1] + H * .3, W * .3, H * .4))
    mt = "/Matrix[%s]" % " ".join("%g" % v for v in matrix) if matrix else ""
    interne = _obj(d, "<</Type/XObject/Subtype/Form/BBox[%g %g %g %g]%s/Resources<</XObject<</Im0 %d 0 R>>>>>>"
                   % (bb + (mt, image)), flux)
    if not imbrique:
        return interne
    # Acrobat : AP/N = formulaire qui appelle /FRM, lui-même appelant le contenu
    return _obj(d, "<</Type/XObject/Subtype/Form/BBox[%g %g %g %g]%s/Resources<</XObject<</FRM %d 0 R>>>>>>"
                % (bb + (mt, interne)), b"q 1 0 0 1 0 0 cm /FRM Do Q")


def ajouter_signature(d, page, rect, nom, *, signee=True, ap=None, flags=132, parent=False):
    sigv = _obj(d, SIG_DICT) if signee else None
    w = d.get_new_xref()
    d.update_object(w, "<<>>")
    base = "/Type/Annot/Subtype/Widget/Rect[%g %g %g %g]/F %d/P %d 0 R" % (rect + (flags, page.xref))
    if ap:
        base += "/AP<</N %d 0 R>>" % ap
    v = "/V %d 0 R" % sigv if sigv else ""
    if parent:   # widget sans /FT ni /V : hérités du champ parent
        champ = _obj(d, "<</FT/Sig/T(%s)%s/Kids[%d 0 R]>>" % (nom, v, w))
        d.update_object(w, "<<%s/Parent %d 0 R>>" % (base, champ))
    else:
        champ = w
        d.update_object(w, "<<%s/FT/Sig/T(%s)%s>>" % (base, nom, v))
    cur = d.xref_get_key(page.xref, "Annots")
    d.xref_set_key(page.xref, "Annots", (cur[1].rstrip("]") + " %d 0 R]" % w) if cur[0] == "array" else "[%d 0 R]" % w)
    cat = d.pdf_catalog()
    if d.xref_get_key(cat, "AcroForm")[0] == "null":
        d.xref_set_key(cat, "AcroForm", "<</Fields[%d 0 R]/SigFlags 3>>" % champ)
    else:
        f = d.xref_get_key(cat, "AcroForm/Fields")[1]
        d.xref_set_key(cat, "AcroForm/Fields", f.rstrip("]") + " %d 0 R]" % champ)
    return w, sigv


def certifier(d, sigv):
    ref = _obj(d, "<</Type/SigRef/TransformMethod/DocMDP/TransformParams<</Type/TransformParams/P 2/V/1.2>>>>")
    d.xref_set_key(sigv, "Reference", "[%d 0 R]" % ref)
    d.xref_set_key(d.pdf_catalog(), "Perms", "<</DocMDP %d 0 R>>" % sigv)


def ecrire(d, chemin):
    d.save(str(chemin))
    d.close()
    return chemin


# --------------------------------------------------------------------------- #
# Géométrie attendue
# --------------------------------------------------------------------------- #
def _mx(m, p):
    a, b, c, d, e, f = m
    return (a * p[0] + c * p[1] + e, b * p[0] + d * p[1] + f)


def points_attendus(rect, bbox=None, matrix=None, page=0, nom=""):
    """(page, nom, point utilisateur, couleur attendue) - ISO 32000-1 §12.5.5."""
    x0, y0, x1, y1 = rect
    W, H = x1 - x0, y1 - y0
    bb = bbox or (0, 0, W, H)
    m = matrix or (1, 0, 0, 1, 0, 0)
    coins = [_mx(m, c) for c in ((bb[0], bb[1]), (bb[2], bb[1]), (bb[2], bb[3]), (bb[0], bb[3]))]
    tx0, tx1 = min(c[0] for c in coins), max(c[0] for c in coins)
    ty0, ty1 = min(c[1] for c in coins), max(c[1] for c in coins)
    sx, sy = W / (tx1 - tx0), H / (ty1 - ty0)

    def pt(fx, fy):
        p = _mx(m, (bb[0] + fx * (bb[2] - bb[0]), bb[1] + fy * (bb[3] - bb[1])))
        return (x0 + (p[0] - tx0) * sx, y0 + (p[1] - ty0) * sy)

    return [(page, "rouge opaque " + nom, pt(.12, .5), ROUGE),
            (page, "transparent (jaune du fond) " + nom, pt(.38, .5), JAUNE),
            (page, "bleu vectoriel " + nom, pt(.75, .5), BLEU)]


def vers_affichage(pt, mediabox, cropbox, rotate):
    """Point de l'espace utilisateur -> pixel de la page affichée (rotation, CropBox, origine)."""
    cx0, cy0, cx1, cy1 = cropbox or mediabox
    cw, ch = cx1 - cx0, cy1 - cy0
    x, y = pt[0] - cx0, pt[1] - cy0
    u, v = {0: (x, ch - y), 90: (y, x), 180: (cw - x, y), 270: (ch - y, cw - x)}[rotate]
    return int(u * SCALE), int(v * SCALE)


def couleur(pix, px, py):
    return tuple(pix.pixel(px, py)[:3])


def proche(a, b, tol=40):
    return all(abs(x - y) <= tol for x, y in zip(a, b))


def verifier_pixels(chemin, points, mediabox=(0, 0, 612, 792), cropbox=None, rotate=0):
    """Rend la page SANS annotations (ce qui est gravé dans le contenu) et compare aux pixels attendus."""
    doc = pymupdf.open(str(chemin))
    try:
        pix_par_page = {}
        erreurs = []
        for pno, nom, pt, attendu in points:
            if pno not in pix_par_page:
                pix_par_page[pno] = doc[pno].get_pixmap(matrix=pymupdf.Matrix(SCALE, SCALE), annots=False,
                                                        alpha=False, colorspace=pymupdf.csRGB)
            pix = pix_par_page[pno]
            px, py = vers_affichage(pt, mediabox, cropbox, rotate)
            obtenu = couleur(pix, px, py)
            if not proche(obtenu, attendu):
                erreurs.append(f"page {pno + 1} {nom}: attendu {attendu}, obtenu {obtenu} en ({px},{py})")
        return erreurs
    finally:
        doc.close()


def verifier_pixels_pdfium(chemin, points, mediabox, cropbox, rotate):
    """Contre-vérification avec un second moteur de rendu, si disponible (facultatif)."""
    pdfium = pytest.importorskip("pypdfium2")
    pdf = pdfium.PdfDocument(str(chemin))
    erreurs = []
    try:
        for pno, nom, pt, attendu in points:
            im = pdf[pno].render(scale=SCALE, may_draw_forms=False).to_pil().convert("RGB")
            px, py = vers_affichage(pt, mediabox, cropbox, rotate)
            obtenu = im.getpixel((px, py))
            if not proche(obtenu, attendu):
                erreurs.append(f"pdfium page {pno + 1} {nom}: attendu {attendu}, obtenu {obtenu}")
    finally:
        pdf.close()
    return erreurs


def catalogue(chemin):
    d = pymupdf.open(str(chemin))
    try:
        cat = d.pdf_catalog()
        return {k: d.xref_get_key(cat, k)[0] for k in ("AcroForm", "Perms")}
    finally:
        d.close()


# --------------------------------------------------------------------------- #
# Cas : une signature signée, géométries variées
# --------------------------------------------------------------------------- #
CAS = {
    "base": {},
    "ap_imbrique_acrobat": dict(imbrique=True),
    "page_90": dict(rotate=90),
    "page_180": dict(rotate=180),
    "page_270": dict(rotate=270),
    "cropbox": dict(cropbox=(50, 550, 350, 700)),
    "cropbox_page_90": dict(cropbox=(50, 550, 350, 700), rotate=90),
    "origine_mediabox": dict(mediabox=(-100, -50, 512, 742), rect=(0, 500, 200, 550)),
    "origine_crop_page_270": dict(mediabox=(100, 200, 712, 992), cropbox=(150, 650, 450, 800),
                                  rotate=270, rect=(200, 700, 400, 750)),
    "matrix_echelle": dict(matrix=(2, 0, 0, .5, 10, 5)),
    "matrix_rot90": dict(bbox=(0, 0, 50, 200), matrix=(0, 1, -1, 0, 0, 0)),
    "matrix_rot270": dict(bbox=(0, 0, 50, 200), matrix=(0, -1, 1, 0, 0, 0)),
    "matrix_rot180": dict(bbox=(0, 0, 200, 50), matrix=(-1, 0, 0, -1, 0, 0)),
    "matrix_rot90_page_270": dict(bbox=(0, 0, 50, 200), matrix=(0, 1, -1, 0, 0, 0), rotate=270),
    "bbox_decalee": dict(bbox=(40, 30, 340, 130)),
    "bbox_plus_petite_que_rect": dict(bbox=(0, 0, 100, 25)),
    "champ_parent_kids": dict(parent=True),
    "flag_print_seul": dict(flags=4),
}


@pytest.mark.parametrize("nom", list(CAS))
def test_signature_signee_visible_apres_aplatissement(tmp_path, nom):
    c = dict(CAS[nom])
    mediabox, cropbox, rotate = c.get("mediabox", (0, 0, 612, 792)), c.get("cropbox"), c.get("rotate", 0)
    rect = c.get("rect", RECT)
    d = nouveau_doc(1, mediabox, rotate, cropbox)
    fond(d, d[0], rect)
    ap = apparence(d, rect[2] - rect[0], rect[3] - rect[1], c.get("matrix"), c.get("bbox"), c.get("imbrique", False))
    ajouter_signature(d, d[0], rect, "Sig", ap=ap, parent=c.get("parent", False), flags=c.get("flags", 132))
    src = ecrire(d, tmp_path / "in.pdf")
    pts = points_attendus(rect, c.get("bbox"), c.get("matrix"))

    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message          # pas de page convertie en image : le vectoriel est gravé
    assert res.signatures == 1 and not res.pages_image
    assert verifier_pixels(res.dst, pts, mediabox, cropbox, rotate) == []
    out = pymupdf.open(str(res.dst))
    try:
        assert core.analyser(out).pages == {}      # plus aucun widget / annotation interactive
        if cropbox is None and mediabox == (0, 0, 612, 792):
            assert "Rapport de fin de fabrication" in out[0].get_text()   # le texte reste du texte
    finally:
        out.close()
    assert catalogue(res.dst)["AcroForm"] == "null"


def test_signature_visible_contre_verification_pdfium(tmp_path):
    """Second moteur de rendu (facultatif) sur une page tournée + CropBox + AP tourné."""
    mediabox, cropbox, rotate = (0, 0, 612, 792), (50, 550, 350, 700), 90
    bbox, matrix = (0, 0, 50, 200), (0, 1, -1, 0, 0, 0)
    d = nouveau_doc(1, mediabox, rotate, cropbox)
    fond(d, d[0], RECT)
    ajouter_signature(d, d[0], RECT, "Sig", ap=apparence(d, 200, 50, matrix, bbox))
    src = ecrire(d, tmp_path / "in.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    assert verifier_pixels_pdfium(res.dst, points_attendus(RECT, bbox, matrix), mediabox, cropbox, rotate) == []


def test_plusieurs_signatures_sur_plusieurs_pages_et_fusion(tmp_path):
    d = nouveau_doc(3)
    pts = []
    for pno, rect, nom in ((0, (50, 100, 250, 150), "A"), (0, (300, 100, 500, 150), "B"),
                           (0, (50, 300, 250, 350), "C"), (1, (50, 100, 250, 150), "D"),
                           (2, (320, 500, 520, 560), "E")):
        fond(d, d[pno], rect)
        ajouter_signature(d, d[pno], rect, nom, ap=apparence(d, rect[2] - rect[0], rect[3] - rect[1]))
        pts += points_attendus(rect, page=pno, nom=nom)
    src = ecrire(d, tmp_path / "in.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    assert res.signatures == 5 and res.pages_elements == [1, 2, 3]
    assert verifier_pixels(res.dst, pts) == []
    # fusion de deux exemplaires, comme dans Acrobat : tout reste visible, dans l'ordre
    fusion = pymupdf.open()
    for _ in range(2):
        a = pymupdf.open(str(res.dst))
        fusion.insert_pdf(a)
        a.close()
    fus = tmp_path / "fusion.pdf"
    fusion.save(str(fus))
    fusion.close()
    pts2 = pts + [(p + 3, n, pt, c) for p, n, pt, c in pts]
    assert verifier_pixels(fus, pts2) == []


def test_meme_champ_initiales_sur_trois_pages(tmp_path):
    """Un seul champ (/Parent) dont les 3 widgets (/Kids) sont sur 3 pages différentes."""
    d = nouveau_doc(3)
    rect = (400, 40, 520, 80)
    sigv = _obj(d, SIG_DICT)
    champ = d.get_new_xref()
    kids, pts = [], []
    for i in range(3):
        fond(d, d[i], rect)
        w = _obj(d, "<</Type/Annot/Subtype/Widget/Rect[%g %g %g %g]/F 4/P %d 0 R/AP<</N %d 0 R>>/Parent %d 0 R>>"
                 % (rect + (d[i].xref, apparence(d, 120, 40), champ)))
        d.xref_set_key(d[i].xref, "Annots", "[%d 0 R]" % w)
        kids.append(w)
        pts += points_attendus(rect, page=i, nom="p%d" % i)
    d.update_object(champ, "<</FT/Sig/T(Initiales)/V %d 0 R/Kids[%s]>>" % (sigv, " ".join("%d 0 R" % k for k in kids)))
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</Fields[%d 0 R]/SigFlags 3>>" % champ)
    src = ecrire(d, tmp_path / "in.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    assert verifier_pixels(res.dst, pts) == []


# --------------------------------------------------------------------------- #
# Champs NON signés : le contrôle visuel (zones_vides) ne doit pas se tromper de zone
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("rotate,cropbox,mediabox,rect", [
    (0, None, (0, 0, 612, 792), RECT),
    (90, None, (0, 0, 612, 792), RECT),
    (180, None, (0, 0, 612, 792), RECT),
    (270, None, (0, 0, 612, 792), RECT),
    (90, (50, 550, 350, 700), (0, 0, 612, 792), RECT),
    (270, (150, 650, 450, 800), (100, 200, 712, 992), (200, 700, 400, 750)),
])
@pytest.mark.parametrize("avec_ap", [False, True])
def test_champ_non_signe_ne_declenche_pas_la_conversion_en_image(tmp_path, rotate, cropbox, mediabox, rect, avec_ap):
    d = nouveau_doc(1, mediabox, rotate, cropbox)
    fond(d, d[0], rect)
    ap = apparence(d, rect[2] - rect[0], rect[3] - rect[1]) if avec_ap else None
    ajouter_signature(d, d[0], rect, "Vide", signee=False, ap=ap)
    src = ecrire(d, tmp_path / "in.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok" and res.pages_image == [], res.message   # la zone est bien masquée, quelle que soit la rotation
    if avec_ap:   # l'apparence d'un champ vide est conservée (gravée) par l'aplatissement
        assert verifier_pixels(res.dst, points_attendus(rect), mediabox, cropbox, rotate) == []


# --------------------------------------------------------------------------- #
# BUGS REPRODUITS (xfail strict : retirer le marqueur quand c'est corrigé)
# --------------------------------------------------------------------------- #
def _zone_uniforme(chemin, rect, rgb=JAUNE, dpi=100, marge=3):
    """Vrai si tout l'intérieur du rectangle (coordonnées PDF, page non tournée) est de la couleur du fond."""
    d = pymupdf.open(str(chemin))
    try:
        h = d[0].rect.height
        clip = pymupdf.Rect(rect[0] + marge, h - rect[3] + marge, rect[2] - marge, h - rect[1] - marge)
        pix = d[0].get_pixmap(dpi=dpi, clip=clip, annots=False, alpha=False, colorspace=pymupdf.csRGB)
        autres = sum(1 for i in range(0, len(pix.samples), 3) if not proche(tuple(pix.samples[i:i + 3]), rgb, 12))
        return autres
    finally:
        d.close()


def _pdf_champ_vide_sans_ap(tmp_path):
    d = nouveau_doc(1)
    fond(d, d[0], RECT)
    ajouter_signature(d, d[0], RECT, "Visa", signee=False)       # champ vide, pas d'/AP (iText, LibreOffice, PDFBox...)
    return ecrire(d, tmp_path / "in.pdf")


def test_champ_signature_vide_sans_ap_pas_de_pastille_sign_dans_le_resultat(tmp_path):
    res = core.aplatir_fichier(_pdf_champ_vide_sans_ap(tmp_path), tmp_path / "out")
    assert res.statut == "ok"
    assert _zone_uniforme(res.dst, RECT) == 0      # le champ vide doit rester le fond de la page, sans artefact


def test_conversion_en_image_ne_grave_pas_la_pastille_sign(tmp_path, monkeypatch):
    src = _pdf_champ_vide_sans_ap(tmp_path)

    def boum(self, *a, **k):
        raise RuntimeError("bake impossible (simulé)")
    monkeypatch.setattr(pymupdf.Document, "bake", boum)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "securite" and res.pages_image == [1]
    assert _zone_uniforme(res.dst, RECT) == 0


@pytest.mark.parametrize("visible", [True, False])
def test_document_certifie_perms_docmdp_supprime(tmp_path, visible):
    d = nouveau_doc(1)
    rect = RECT if visible else (0, 0, 0, 0)       # signature de certification visible ou invisible (Rect nul)
    if visible:
        fond(d, d[0], rect)
    _, sigv = ajouter_signature(d, d[0], rect, "Certif", ap=apparence(d, 200, 50) if visible else None)
    certifier(d, sigv)
    src = ecrire(d, tmp_path / "in.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    assert catalogue(res.dst)["AcroForm"] == "null"          # (déjà vrai aujourd'hui)
    assert catalogue(res.dst)["Perms"] == "null"             # (faux aujourd'hui : <</DocMDP N 0 R>>)


def _pdf_tampon_sur_signature(tmp_path):
    d = nouveau_doc(1)
    fond(d, d[0], RECT)
    ajouter_signature(d, d[0], RECT, "Sig", ap=apparence(d, 200, 50))
    ap = _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 120 50]/Resources<<>>>>", b"0 .6 0 rg 0 0 120 50 re f")
    st = _obj(d, "<</Type/Annot/Subtype/Stamp/Rect[200 600 320 650]/F 4/AP<</N %d 0 R>>/P %d 0 R>>" % (ap, d[0].xref))
    cur = d.xref_get_key(d[0].xref, "Annots")[1]
    d.xref_set_key(d[0].xref, "Annots", cur.rstrip("]") + " %d 0 R]" % st)   # le tampon est APRÈS le widget : il le recouvre
    return ecrire(d, tmp_path / "in.pdf")


@pytest.mark.xfail(strict=True, reason="SIG-3: MuPDF dessine toujours les widgets au-dessus des autres annotations : "
                                       "le contrôle visuel rejette le bon résultat de bake() (ordre /Annots respecté) "
                                       "et la page est rastérisée avec le MAUVAIS ordre (signature par-dessus le tampon)")
def test_tampon_posterieur_recouvrant_une_signature_reste_au_dessus(tmp_path):
    res = core.aplatir_fichier(_pdf_tampon_sur_signature(tmp_path), tmp_path / "out")
    assert res.statut == "ok", res.message
    # (260, 625) : dans la partie du bleu de la signature recouverte par le tampon vert
    assert verifier_pixels(res.dst, [(0, "tampon sur signature", (260, 625), VERT)]) == []


def test_nombre_de_signatures_ne_compte_que_les_signees(tmp_path):
    d = nouveau_doc(1)
    ajouter_signature(d, d[0], (100, 600, 300, 650), "Signee", ap=apparence(d, 200, 50))
    ajouter_signature(d, d[0], (100, 400, 300, 450), "Vide", signee=False, ap=apparence(d, 200, 50))
    src = ecrire(d, tmp_path / "in.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.signatures == 1
