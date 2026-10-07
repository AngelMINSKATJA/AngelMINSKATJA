"""Champs de formulaire AcroForm (texte, case, radio, liste, bouton, XFA...) : les VALEURS
saisies sont-elles encore visibles apres aplatissement et apres fusion ?

Les PDF sont fabriques a la main (API bas niveau de PyMuPDF). Les tests ``xfail(strict=True)``
demontrent un vrai defaut (identifiant FORM-n du rapport) ; les autres sont des tests de
non-regression.
"""
import time

import pymupdf
import pytest

import aplatir_core as core

HELV = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"


# --------------------------------------------------------------------------- #
# Outils
# --------------------------------------------------------------------------- #
def _obj(d, corps, flux=None):
    x = d.get_new_xref()
    d.update_object(x, corps)
    if flux is not None:
        d.update_stream(x, flux)
    return x


def _doc(pages=1):
    d = pymupdf.open()
    for i in range(pages):
        p = d.new_page()
        p.insert_text((72, 72), f"Rapport de fin de fabrication page {i + 1}", fontsize=14)
    return d


def _annot(d, page, x):
    cur = d.xref_get_key(page.xref, "Annots")
    d.xref_set_key(page.xref, "Annots",
                   (cur[1].rstrip("]") + " %d 0 R]" % x) if cur[0] == "array" else "[%d 0 R]" % x)


def _widget(d, page, rect, corps, *, ap=None, flags=4):
    base = "/Type/Annot/Subtype/Widget/Rect[%g %g %g %g]/F %d/P %d 0 R" % (tuple(rect) + (flags, page.xref))
    if ap:
        base += "/AP<</N %d 0 R>>" % ap
    x = _obj(d, "<<%s%s>>" % (base, corps))
    _annot(d, page, x)
    return x


def _acroform(d, champs, extra=""):
    d.xref_set_key(d.pdf_catalog(), "AcroForm",
                   "<</Fields[%s]%s>>" % (" ".join("%d 0 R" % c for c in champs), extra))


def _ap_texte(d, texte, w=228, h=20):
    helv = _obj(d, HELV)
    return _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 %d %d]/Resources<</Font<</Helv %d 0 R>>>>>>"
                % (w, h, helv),
                ("/Tx BMC q 1 1 %d %d re W n BT /Helv 12 Tf 0 g 3 6 Td (%s) Tj ET Q EMC"
                 % (w - 2, h - 2, texte)).encode())


def _ecrire(d, chemin):
    d.save(str(chemin))
    d.close()
    return chemin


def _textes(chemin):
    d = pymupdf.open(str(chemin))
    try:
        return [p.get_text() for p in d]
    finally:
        d.close()


def _aplatir(tmp_path, src, **kw):
    return core.aplatir_fichier(src, tmp_path / "sortie", **kw)


def _sans_interactif(chemin):
    d = pymupdf.open(str(chemin))
    try:
        return (sum(len(list(p.widgets() or [])) for p in d) == 0
                and d.xref_get_key(d.pdf_catalog(), "AcroForm")[0] == "null")
    finally:
        d.close()


# --------------------------------------------------------------------------- #
# Non-regression : les valeurs survivent
# --------------------------------------------------------------------------- #
def _doc_champs_usuels():
    """Texte (avec accents et caracteres hors Latin-1), multiligne, liste, case a cocher :
    pas d'apparence stockee (/AP) + /NeedAppearances, comme un formulaire rempli par un script."""
    d = _doc()
    p = d[0]
    helv = _obj(d, HELV)
    champs = []
    champs.append(_widget(d, p, (72, 600, 300, 620), "/FT/Tx/T(t1)/V(Rep\\350re \\311l\\351phant)/DA(/Helv 11 Tf 0 g)"))
    champs.append(_widget(d, p, (72, 540, 300, 590), "/FT/Tx/Ff 4096/T(t2)/V(Ligne un\\nLigne deux)/DA(/Helv 10 Tf 0 g)"))
    champs.append(_widget(d, p, (72, 500, 300, 520), "/FT/Tx/T(t3)/V<FEFF041F04400438043204350442>/DA(/Helv 11 Tf 0 g)"))
    champs.append(_widget(d, p, (72, 460, 300, 480), "/FT/Tx/T(t4)/V(Herite le DA du formulaire)"))
    champs.append(_widget(d, p, (72, 420, 300, 440), "/FT/Ch/Ff 131072/T(c1)/Opt[(Alpha)(Beta)(Gamma)]/V(Beta)/DA(/Helv 11 Tf 0 g)"))
    _acroform(d, champs, "/NeedAppearances true/DA(/Helv 12 Tf 0 g)/DR<</Font<</Helv %d 0 R>>>>" % helv)
    return d


def test_valeurs_des_champs_sans_apparence_visibles(tmp_path):
    src = _ecrire(_doc_champs_usuels(), tmp_path / "formulaire.pdf")
    res = _aplatir(tmp_path, src)
    assert res.statut == "ok", res.message
    texte = _textes(res.dst)[0]
    for attendu in ("Repère Éléphant", "Ligne un", "Ligne deux", "Привет", "Herite le DA du formulaire", "Beta"):
        assert attendu in texte, (attendu, texte)
    assert _sans_interactif(res.dst)


def test_fusion_conserve_les_valeurs(tmp_path):
    a = _ecrire(_doc_champs_usuels(), tmp_path / "a.pdf")
    d = _doc()
    p = d[0]
    ap = _ap_texte(d, "VALEUR STOCKEE")
    x = _widget(d, p, (72, 600, 300, 620), "/FT/Tx/T(z)/V(VALEUR STOCKEE)/DA(/Helv 12 Tf 0 g)", ap=ap)
    _acroform(d, [x])
    b = _ecrire(d, tmp_path / "b.pdf")
    ra, rb = _aplatir(tmp_path, a), _aplatir(tmp_path, b)
    assert ra.statut == "ok" and rb.statut == "ok", (ra.message, rb.message)
    fusion = pymupdf.open()
    for r in (ra, rb):
        with pymupdf.open(str(r.dst)) as s:
            fusion.insert_pdf(s)
    out = tmp_path / "fusion.pdf"
    fusion.save(str(out))
    fusion.close()
    textes = " ".join(_textes(out))
    assert "Éléphant" in textes and "VALEUR STOCKEE" in textes
    assert _sans_interactif(out)


def test_champ_cache_non_grave(tmp_path):
    """/F Hidden : le champ n'est pas affiche dans le document d'origine, il ne doit pas apparaitre."""
    d = _doc()
    p = d[0]
    x1 = _widget(d, p, (72, 600, 300, 620), "/FT/Tx/T(v)/V(VISIBLE)", ap=_ap_texte(d, "VISIBLE"), flags=4)
    x2 = _widget(d, p, (72, 560, 300, 580), "/FT/Tx/T(h)/V(CACHE)", ap=_ap_texte(d, "CACHE"), flags=2)
    _acroform(d, [x1, x2])
    src = _ecrire(d, tmp_path / "cache.pdf")
    res = _aplatir(tmp_path, src)
    assert res.statut == "ok", res.message
    texte = _textes(res.dst)[0]
    assert "VISIBLE" in texte and "CACHE" not in texte


def test_champ_multi_widgets_sur_deux_pages(tmp_path):
    d = _doc(pages=2)
    parent = d.get_new_xref()
    d.update_object(parent, "<<>>")
    kids = []
    for pg, rect in ((d[0], (72, 600, 300, 620)), (d[0], (72, 500, 300, 520)), (d[1], (72, 600, 300, 620))):
        k = _widget(d, pg, rect, "/Parent %d 0 R" % parent, ap=_ap_texte(d, "VALEUR PARTAGEE"))
        kids.append(k)
    d.update_object(parent, "<</FT/Tx/T(shared)/V(VALEUR PARTAGEE)/Kids[%s]>>" % " ".join("%d 0 R" % k for k in kids))
    _acroform(d, [parent])
    src = _ecrire(d, tmp_path / "multi.pdf")
    res = _aplatir(tmp_path, src)
    assert res.statut == "ok", res.message
    t = _textes(res.dst)
    assert t[0].count("VALEUR PARTAGEE") == 2 and t[1].count("VALEUR PARTAGEE") == 1
    assert _sans_interactif(res.dst)


def test_champs_sans_widget_visible_copie_sans_perte(tmp_path):
    """/AcroForm avec des champs mais aucun widget sur les pages : copie telle quelle, rien de perdu."""
    d = _doc()
    x = _obj(d, "<</FT/Tx/T(nulle_part)/V(INVISIBLE)>>")
    _acroform(d, [x], "/NeedAppearances true")
    src = _ecrire(d, tmp_path / "sanswidget.pdf")
    res = _aplatir(tmp_path, src)
    assert res.statut == "copie"
    assert _textes(res.dst) == _textes(src)


# --------------------------------------------------------------------------- #
# FORM-1 : XFA dynamique = page « Please wait... », annoncee « Copie / rien a aplatir »
# --------------------------------------------------------------------------- #
def _doc_xfa_dynamique():
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 100), "Please wait...", fontsize=14)
    p.insert_text((72, 130), "If this message is not eventually replaced by the proper contents of the "
                  "document, your PDF viewer may not be able to display this type of document.", fontsize=7)
    tpl = _obj(d, "<<>>", b'<template xmlns="http://www.xfa.org/schema/xfa-template/3.3/"><subform name="form1">'
                          b'<field name="Quantite"/></subform></template>')
    dat = _obj(d, "<<>>", b'<xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/"><xfa:data>'
                          b'<form1><Quantite>1234</Quantite></form1></xfa:data></xfa:datasets>')
    d.xref_set_key(d.pdf_catalog(), "AcroForm",
                   "<</XFA[(template) %d 0 R (datasets) %d 0 R]/Fields[]>>" % (tpl, dat))
    return d


def test_xfa_dynamique_est_signale(tmp_path):
    src = _ecrire(_doc_xfa_dynamique(), tmp_path / "xfa.pdf")
    res = _aplatir(tmp_path, src)
    assert res.statut in ("alerte", "erreur"), (res.statut, res.message)


def test_xfa_statique_est_retire(tmp_path):
    """XFA « statique » : de vrais champs + /XFA. Apres aplatissement plus de /AcroForm du tout."""
    d = _doc()
    p = d[0]
    tpl = _obj(d, "<<>>", b'<template xmlns="http://www.xfa.org/schema/xfa-template/3.3/"/>')
    x = _widget(d, p, (72, 600, 300, 620), "/FT/Tx/T(Quantite)/V(1234)", ap=_ap_texte(d, "1234"))
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</XFA[(template) %d 0 R]/Fields[%d 0 R]>>" % (tpl, x))
    src = _ecrire(d, tmp_path / "xfa_statique.pdf")
    res = _aplatir(tmp_path, src)
    assert res.statut == "ok", res.message
    assert "1234" in _textes(res.dst)[0]
    assert _sans_interactif(res.dst)


# --------------------------------------------------------------------------- #
# FORM-2 : liste/combo avec options [valeur d'export, libelle] : MuPDF affiche l'export
# --------------------------------------------------------------------------- #
def _doc_combo_export(na: bool, avec_ap: bool):
    d = _doc()
    p = d[0]
    helv = _obj(d, HELV)
    ap = _ap_texte(d, "Beta") if avec_ap else None
    x = _widget(d, p, (72, 600, 300, 620),
                "/FT/Ch/Ff 131072/T(c1)/Opt[[(1)(Alpha)][(2)(Beta)][(3)(Gamma)]]/V(2)/DA(/Helv 11 Tf 0 g)", ap=ap)
    _acroform(d, [x], ("/NeedAppearances true" if na else "")
              + "/DA(/Helv 11 Tf 0 g)/DR<</Font<</Helv %d 0 R>>>>" % helv)
    return d


def test_combo_export_avec_apparence_stockee_ok(tmp_path):
    src = _ecrire(_doc_combo_export(False, True), tmp_path / "combo_ap.pdf")
    res = _aplatir(tmp_path, src)
    assert res.statut == "ok" and "Beta" in _textes(res.dst)[0]


def test_combo_export_sans_apparence_affiche_le_libelle(tmp_path):
    src = _ecrire(_doc_combo_export(False, False), tmp_path / "combo_noap.pdf")
    res = _aplatir(tmp_path, src)
    texte = _textes(res.dst)[0]
    assert "Beta" in texte, texte


def _rendu_zone(chemin, rect, sans_need_appearances=False):
    from PIL import Image
    d = pymupdf.open(str(chemin))
    try:
        if sans_need_appearances:      # montre l'apparence STOCKEE (celle qu'affichent pdfium/Chrome)
            d.xref_set_key(d.pdf_catalog(), "AcroForm/NeedAppearances", "false")
        pm = d[0].get_pixmap(dpi=100, clip=pymupdf.Rect(*rect))
        return Image.frombytes("RGB", (pm.width, pm.height), pm.samples)
    finally:
        d.close()


@pytest.mark.xfail(strict=True, reason="FORM-2: avec /NeedAppearances l'apparence stockee correcte (« Beta ») est ecrasee par le rendu MuPDF (« 2 »)")
def test_combo_export_need_appearances_garde_le_libelle(tmp_path):
    from PIL import ImageChops
    src = _ecrire(_doc_combo_export(True, True), tmp_path / "combo_na.pdf")
    res = _aplatir(tmp_path, src)
    zone = (60, 842 - 625, 310, 842 - 595)
    ref = _rendu_zone(src, zone, sans_need_appearances=True)       # « Beta »
    out = _rendu_zone(res.dst, zone)
    ecart = sum(ImageChops.difference(ref, out).convert("L").histogram()[100:])
    assert ecart < 20, (res.statut, ecart)


# --------------------------------------------------------------------------- #
# FORM-3 : /NeedAppearances true + apparences coherentes -> page convertie en image
# --------------------------------------------------------------------------- #
def _doc_na_coherent():
    d = _doc()
    p = d[0]
    helv = _obj(d, HELV)
    champs = [_widget(d, p, (72, 600 - 40 * i, 300, 620 - 40 * i),
                      "/FT/Tx/T(t%d)/V(%s)/DA(/Helv 12 Tf 0 g)" % (i, t), ap=_ap_texte(d, t))
              for i, t in enumerate(("Valeur coherente", "Autre valeur"))]
    _acroform(d, champs, "/NeedAppearances true/DA(/Helv 12 Tf 0 g)/DR<</Font<</Helv %d 0 R>>>>" % helv)
    return d


@pytest.mark.xfail(strict=True, reason="FORM-3: /NeedAppearances true -> page entiere rasterisee (texte perdu) meme quand les /AP sont justes")
def test_need_appearances_n_oblige_pas_a_rasteriser(tmp_path):
    src = _ecrire(_doc_na_coherent(), tmp_path / "na.pdf")
    res = _aplatir(tmp_path, src)
    assert res.pages_image == [], res.message
    assert "Valeur coherente" in _textes(res.dst)[0]


# --------------------------------------------------------------------------- #
# FORM-4 : champ avec /RV (texte riche) et sans /AP : MuPDF ne dessine rien -> valeur perdue, statut « ok »
# --------------------------------------------------------------------------- #
def test_champ_texte_riche_sans_apparence_garde_sa_valeur(tmp_path):
    d = _doc()
    p = d[0]
    helv = _obj(d, HELV)
    rv = '<body xmlns="http://www.w3.org/1999/xhtml"><p><span style="font-weight:bold">Observation importante</span></p></body>'
    x = _widget(d, p, (72, 600, 300, 640),
                "/FT/Tx/Ff 33554432/T(rtf)/V(Observation importante)/RV(%s)/DA(/Helv 12 Tf 0 g)" % rv)
    _acroform(d, [x], "/DA(/Helv 12 Tf 0 g)/DR<</Font<</Helv %d 0 R>>>>" % helv)
    src = _ecrire(d, tmp_path / "rtf.pdf")
    res = _aplatir(tmp_path, src)
    assert "Observation importante" in _textes(res.dst)[0], res.statut


# --------------------------------------------------------------------------- #
# FORM-6 : widget /F NoView (jamais affiche a l'ecran) rendu visible par l'aplatissement
# --------------------------------------------------------------------------- #
def test_champ_noview_reste_invisible(tmp_path):
    d = _doc()
    p = d[0]
    x = _widget(d, p, (72, 600, 300, 620), "/FT/Tx/T(nv)/V(NOVIEW)", ap=_ap_texte(d, "NOVIEW"), flags=32)
    _acroform(d, [x])
    src = _ecrire(d, tmp_path / "noview.pdf")
    assert "NOVIEW" not in _textes(src)[0]          # le document d'origine n'affiche pas ce champ
    res = _aplatir(tmp_path, src)
    assert "NOVIEW" not in _textes(res.dst)[0]


# --------------------------------------------------------------------------- #
# FORM-5 : champ « mot de passe » grave en clair
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="FORM-5: champ /Ff password sans /AP : MuPDF genere et grave la valeur en clair")
def test_champ_mot_de_passe_reste_masque(tmp_path):
    d = _doc()
    p = d[0]
    helv = _obj(d, HELV)
    x = _widget(d, p, (72, 600, 300, 620), "/FT/Tx/Ff 8192/T(pwd)/V(S3cretPIN)/DA(/Helv 11 Tf 0 g)")
    _acroform(d, [x], "/NeedAppearances true/DA(/Helv 11 Tf 0 g)/DR<</Font<</Helv %d 0 R>>>>" % helv)
    src = _ecrire(d, tmp_path / "pwd.pdf")
    res = _aplatir(tmp_path, src)
    assert "S3cretPIN" not in _textes(res.dst)[0]


@pytest.mark.xfail(strict=True, reason="FORM-5: apparence generee par MuPDF : « <= » et « >= » deviennent « · » (police de secours sans ces glyphes)")
def test_champ_sans_apparence_garde_inferieur_ou_egal(tmp_path):
    d = _doc()
    p = d[0]
    helv = _obj(d, HELV)
    v = "<FEFF" + "Tolerance \u2264 0,05 mm et \u2265 0,01".encode("utf-16-be").hex().upper() + ">"
    x = _widget(d, p, (72, 600, 400, 620), "/FT/Tx/T(tol)/V%s/DA(/Helv 11 Tf 0 g)" % v)
    _acroform(d, [x], "/NeedAppearances true/DA(/Helv 11 Tf 0 g)/DR<</Font<</Helv %d 0 R>>>>" % helv)
    src = _ecrire(d, tmp_path / "tol.pdf")
    res = _aplatir(tmp_path, src)
    texte = _textes(res.dst)[0]
    assert "\u2264" in texte and "\u2265" in texte, texte


# --------------------------------------------------------------------------- #
# FORM-7 : enregistrement (garbage=3) quadratique sur les gros PDF de formulaires
# --------------------------------------------------------------------------- #
def _doc_formulaire_lourd(pages, champs_par_page):
    d = pymupdf.open()
    helv = _obj(d, HELV)
    ap = _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 150 18]/Resources<</Font<</Helv %d 0 R>>>>>>" % helv,
              b"/Tx BMC q BT /Helv 11 Tf 0 g 2 5 Td (Valeur) Tj ET Q EMC")
    tous = []
    for _ in range(pages):
        p = d.new_page()
        for i in range(champs_par_page):
            # un /AP propre a chaque champ (comme Acrobat) : beaucoup d'objets quasi identiques
            apx = _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 150 18]/Resources<</Font<</Helv %d 0 R>>>>>>" % helv,
                       b"/Tx BMC q BT /Helv 11 Tf 0 g 2 5 Td (Valeur %d) Tj ET Q EMC" % (i % 7))
            tous.append(_widget(d, p, (40 + (i % 3) * 170, 700 - (i // 3) * 24, 190 + (i % 3) * 170, 718 - (i // 3) * 24),
                                "/FT/Tx/T(p%d_%d)/V(Valeur %d)" % (len(tous), i, i % 7), ap=apx))
    _acroform(d, tous)
    return d


def test_enregistrement_garbage3_pas_quadratique(tmp_path):
    src = _ecrire(_doc_formulaire_lourd(200, 24), tmp_path / "lourd.pdf")
    # reference : meme document, aplati puis enregistre avec garbage=2
    ref = pymupdf.open(str(src))
    ref.bake(annots=True, widgets=True)
    t0 = time.perf_counter()
    ref.save(str(tmp_path / "ref.pdf"), garbage=2, deflate=True)
    t_ref = time.perf_counter() - t0
    ref.close()
    d = pymupdf.open(str(src))
    d.bake(annots=True, widgets=True)
    t0 = time.perf_counter()
    core._enregistrer(d, tmp_path / "sortie" / "x.pdf")
    t_core = time.perf_counter() - t0
    d.close()
    assert t_core < 5 * t_ref + 0.3, (t_core, t_ref)
