"""2e passe d'audit (angle « correction du cœur ») : les correctifs du cœur corrigent-ils vraiment,
et n'en cassent-ils pas d'autres ?

Les PDF sont fabriqués à la main avec PyMuPDF (aucun outil externe requis : tout est hermétique
et tourne aussi sur un runner Windows). Les tests ``xfail(strict=True)`` démontrent un vrai
défaut (identifiant R2-COR-n du rapport) : retirer le marqueur quand c'est corrigé.
"""
import random
import re

import pymupdf
import pytest

import aplatir_core as core


# --------------------------------------------------------------------------- #
# Outils
# --------------------------------------------------------------------------- #
def _obj(d, corps, flux=None):
    x = d.get_new_xref()
    d.update_object(x, corps)
    if flux is not None:
        d.update_stream(x, flux)
    return x


def _ajouter_annot(d, page, x):
    cur = d.xref_get_key(page.xref, "Annots")
    d.xref_set_key(page.xref, "Annots",
                   (cur[1].rstrip("]") + " %d 0 R]" % x) if cur[0] == "array" else "[%d 0 R]" % x)


def _tampon(page, rect=(300, 150, 500, 220), stamp=0):
    return page.add_stamp_annot(pymupdf.Rect(*rect), stamp=stamp)


def _tampon_noview(d, page, rect=(300, 300, 500, 370)):
    """Annotation que MuPDF n'affiche pas (NoView) mais que ``bake`` grave : force le repli image."""
    a = page.add_stamp_annot(pymupdf.Rect(*rect), stamp=1)
    d.xref_set_key(a.xref, "F", "32")
    return a


def _traiter(src, tmp_path, **kw):
    return core.aplatir_fichier(src, tmp_path / "sortie", **kw)


def _pixels_non_blancs(page, rect_haut_gauche, dpi=72):
    pix = page.get_pixmap(dpi=dpi, clip=pymupdf.Rect(*rect_haut_gauche), alpha=False)
    s = pix.samples
    return sum(1 for i in range(0, len(s), 3) if s[i:i + 3] != b"\xff\xff\xff")


# --------------------------------------------------------------------------- #
# (2) Champ de signature vide sans apparence : masqué quel que soit /F
# --------------------------------------------------------------------------- #
def _doc_signature_vide(tmp_path, fspec, parent=False, ap_indirect_f=None):
    d = pymupdf.open()
    p = d.new_page(width=612, height=792)
    p.insert_text((72, 72), "Rapport", fontsize=14)
    w = d.get_new_xref()
    f = fspec
    if ap_indirect_f is not None:
        f = "/F %d 0 R" % _obj(d, str(ap_indirect_f))
    base = "/Type/Annot/Subtype/Widget/Rect[100 500 300 550]/P %d 0 R%s" % (p.xref, f)
    if parent:
        champ = _obj(d, "<</FT/Sig/T(S)/Kids[%d 0 R]>>" % w)
        d.update_object(w, "<<%s/Parent %d 0 R>>" % (base, champ))
    else:
        champ = w
        d.update_object(w, "<<%s/FT/Sig/T(S)>>" % base)
    _ajouter_annot(d, p, w)
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</Fields[%d 0 R]/SigFlags 1>>" % champ)
    chemin = tmp_path / "vide.pdf"
    d.save(str(chemin))
    d.close()
    return chemin


@pytest.mark.parametrize("fspec,parent,indirect", [
    ("", False, None), ("/F 4", False, None), ("/F 132", False, None), ("/F 0", False, None),
    ("/F -1", False, None), ("/F 4294967295", False, None), ("/F 4.0", False, None),
    ("", False, 4), ("/F 4", True, None), ("", True, None), ("/F 32", False, None),
])
def test_signature_vide_sans_ap_aucune_pastille_quel_que_soit_f(tmp_path, fspec, parent, indirect):
    src = _doc_signature_vide(tmp_path, fspec, parent, indirect)
    res = _traiter(src, tmp_path)
    assert res.statut == "ok", res.message
    out = pymupdf.open(str(res.dst))
    # zone du champ (coordonnées PDF 100,500 -> 300,550) = (100, 242)-(300, 292) en haut-gauche
    assert _pixels_non_blancs(out[0], (100, 242, 300, 292)) == 0


def test_signature_vide_et_repli_image_ne_grave_pas_la_pastille(tmp_path, monkeypatch):
    """Le repli image part de l'exemplaire NORMALISÉ : pas de pastille « SIGN » dans l'image."""
    d = pymupdf.open()
    p = d.new_page(width=612, height=792)
    p.insert_text((72, 72), "Rapport", fontsize=14)
    w = d.get_new_xref()
    d.update_object(w, "<</Type/Annot/Subtype/Widget/FT/Sig/T(S)/Rect[100 500 300 550]/F 4/P %d 0 R>>" % p.xref)
    _ajouter_annot(d, p, w)
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</Fields[%d 0 R]/SigFlags 1>>" % w)
    _tampon(p)
    _tampon_noview(d, p)
    src = tmp_path / "a.pdf"
    d.save(str(src))
    d.close()
    for forcer_echec_bake in (False, True):
        if forcer_echec_bake:
            def boum(self, *a, **k):
                raise RuntimeError("boom")
            monkeypatch.setattr(pymupdf.Document, "bake", boum)
        res = core.aplatir_fichier(src, tmp_path / ("s%d" % forcer_echec_bake))
        assert res.statut == "securite" and res.pages_image == [1], res.message
        out = pymupdf.open(str(res.dst))
        assert _pixels_non_blancs(out[0], (100, 242, 300, 292)) == 0


# --------------------------------------------------------------------------- #
# (2) Menus déroulants [export, libellé] : le libellé est gravé, sous toutes ses formes
# --------------------------------------------------------------------------- #
def _doc_combo(tmp_path, V, opt):
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Doc", fontsize=14)
    w = d.get_new_xref()
    d.update_object(w, "<</Type/Annot/Subtype/Widget/FT/Ch/Ff 131072/T(c)/V%s/Opt%s/DA(/Helv 12 Tf 0 g)"
                       "/Rect[72 600 300 630]/F 4/P %d 0 R>>" % (V, opt, p.xref))
    _ajouter_annot(d, p, w)
    d.xref_set_key(d.pdf_catalog(), "AcroForm",
                   "<</Fields[%d 0 R]/DA(/Helv 12 Tf 0 g)/DR<</Font<</Helv<</Type/Font/Subtype/Type1"
                   "/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>>>>>>>" % w)
    chemin = tmp_path / "combo.pdf"
    d.save(str(chemin))
    d.close()
    return chemin


@pytest.mark.parametrize("V,opt,attendu", [
    ("(B)", "[[(A)(Alpha)][(B)(Beta)]]", "Beta"),
    ("(C)", "[[(A)(Alpha)][(B)(Beta)][(C)(Gamm\\(a\\))]]", "Gamm(a)"),
    ("(B)", "[[(A)(A)][(B)(B)]]", "B"),
    ("(Two)", "[(One)(Two)(Three)]", "Two"),
    ("(A)", "[[(A)<FEFF00E900E8>][(B)(x)]]", "éè"),
    ("<FEFF0041>", "[[(A)(Alpha)][(B)(Beta)]]", "Alpha"),
    ("(\\351t\\351)", "[[(\\351t\\351)(Printemps)][(B)(x)]]", "Printemps"),
    ("(a\\\\b)", "[[(a\\\\b)(label \\\\ x)][(B)(x)]]", "label \\ x"),
])
def test_combo_libelle_grave_formes_diverses(tmp_path, V, opt, attendu):
    res = _traiter(_doc_combo(tmp_path, V, opt), tmp_path)
    assert res.statut == "ok", res.message
    texte = pymupdf.open(str(res.dst))[0].get_text("text")
    assert attendu in texte, texte


# --------------------------------------------------------------------------- #
# (3) Repli image : l'objet page garde son numéro, quelle que soit la structure
# --------------------------------------------------------------------------- #
def _forcer_repli(monkeypatch):
    def boum(self, *a, **k):
        raise RuntimeError("boom")
    monkeypatch.setattr(pymupdf.Document, "bake", boum)


def _doc_rotation_heritee(tmp_path, rot, mode, crop, mbox):
    d = pymupdf.open()
    p = d.new_page(width=500, height=750)
    p.insert_text((60, 120), "Texte de page", fontsize=18)
    racine = int(d.xref_get_key(d.pdf_catalog(), "Pages")[1].split()[0])
    porteur = racine
    if mode == "mid":
        mid = d.get_new_xref()
        d.update_object(mid, "<</Type/Pages/Kids[%d 0 R]/Count 1/Parent %d 0 R>>" % (p.xref, racine))
        d.xref_set_key(racine, "Kids", "[%d 0 R]" % mid)
        d.xref_set_key(p.xref, "Parent", "%d 0 R" % mid)
        porteur = mid
    d.xref_set_key(porteur, "Rotate", str(rot))
    d.xref_set_key(p.xref, "MediaBox", "[%g %g %g %g]" % mbox)
    if crop:
        c = (mbox[0] + crop[0], mbox[1] + crop[1], mbox[0] + crop[2], mbox[1] + crop[3])
        d.xref_set_key(porteur, "CropBox", "[%g %g %g %g]" % c)
    # xref_set_key(..., "null") laisse « /Cle null » : on retire la clé pour de bon (hérite du nœud /Pages)
    d.xref_set_key(p.xref, "Rotate", "null")
    d.xref_set_key(p.xref, "CropBox", "null")
    d.update_object(p.xref, re.sub(r"/(Rotate|CropBox) null", "", d.xref_object(p.xref, compressed=True)))
    _tampon(p, (100 + (crop[0] if crop else 0), 300 + (crop[1] if crop else 0),
                220 + (crop[0] if crop else 0), 340 + (crop[1] if crop else 0)))
    _tampon_noview(d, p, (100, 400, 220, 440))
    chemin = tmp_path / f"rot_{rot}_{mode}_{int(bool(crop))}.pdf"
    d.save(str(chemin), garbage=0)
    d.close()
    return chemin


@pytest.mark.parametrize("rot", [90, 180, 270])
@pytest.mark.parametrize("mode", ["root", "mid"])
@pytest.mark.parametrize("crop", [None, (50, 60, 450, 700)])
@pytest.mark.parametrize("mbox", [(0, 0, 500, 750), (100, 100, 600, 850)])
def test_repli_image_rotation_et_cropbox_herites_du_noeud_pages(tmp_path, rot, mode, crop, mbox):
    src = _doc_rotation_heritee(tmp_path, rot, mode, crop, mbox)
    a = pymupdf.open(str(src))
    attendu = (tuple(a[0].rect), a[0].rotation)
    a.close()
    res = _traiter(src, tmp_path)
    assert res.statut == "securite" and res.pages_image == [1], res.message
    o = pymupdf.open(str(res.dst))
    assert o[0].rotation == 0                        # l'image est déjà tournée : pas de /Rotate hérité en plus
    assert tuple(o[0].rect) == attendu[0]            # même taille affichée que l'original


def _doc_riche(tmp_path):
    """Signets, liens internes, destinations nommées, étiquettes de pages, arbre de structure
    (OBJR vers un champ), vignette, perles : ce qu'un PDF balisé réel peut porter."""
    d = pymupdf.open()
    for i in range(4):
        p = d.new_page(width=612, height=792)
        p.insert_text((72, 72), f"Page {i + 1}", fontsize=14)
    pages = [pg.xref for pg in d]
    cat = d.pdf_catalog()
    d.set_toc([[1, "Ch1", 1], [1, "Ch2", 2], [1, "Ch3", 3]])
    d[0].insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(10, 10, 100, 30), "page": 1,
                      "to": pymupdf.Point(0, 100)})
    d[1].insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(10, 10, 100, 30), "page": 2,
                      "to": pymupdf.Point(0, 0)})
    d[1].insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(10, 40, 100, 60), "uri": "https://example.com"})
    d[2].insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(10, 10, 100, 30), "page": 0,
                      "to": pymupdf.Point(0, 0)})
    w = d.get_new_xref()
    ap = _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 100 20]>>", b"0 0 1 rg 0 0 100 20 re f")
    d.update_object(w, "<</Type/Annot/Subtype/Widget/FT/Tx/T(t)/V(x)/Rect[100 400 200 420]/F 4/P %d 0 R"
                       "/AP<</N %d 0 R>>/StructParent 7>>" % (pages[1], ap))
    _ajouter_annot(d, d[1], w)
    d.xref_set_key(cat, "AcroForm", "<</Fields[%d 0 R]>>" % w)
    racine = d.get_new_xref()
    d.update_object(racine, "<<>>")
    elem = d.get_new_xref()
    objr = _obj(d, "<</Type/OBJR/Obj %d 0 R/Pg %d 0 R>>" % (w, pages[1]))
    d.update_object(elem, "<</Type/StructElem/S/Form/P %d 0 R/Pg %d 0 R/K[%d 0 R]>>" % (racine, pages[1], objr))
    d.update_object(racine, "<</Type/StructTreeRoot/K[%d 0 R]/ParentTree<</Nums[7 %d 0 R]>>>>" % (elem, elem))
    d.xref_set_key(cat, "StructTreeRoot", "%d 0 R" % racine)
    d.xref_set_key(cat, "MarkInfo", "<</Marked true>>")
    d.xref_set_key(pages[1], "StructParents", "3")
    d.xref_set_key(pages[1], "Tabs", "/S")
    th = _obj(d, "<</Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8>>", b"\x80")
    d.xref_set_key(pages[1], "Thumb", "%d 0 R" % th)
    d.xref_set_key(cat, "Names", "<</Dests<</Names[(dest1) [%d 0 R /XYZ 0 700 null] (dest2) [%d 0 R /Fit]]>>>>"
                   % (pages[1], pages[2]))
    d.set_page_labels([{"startpage": 0, "prefix": "A-", "style": "D"}, {"startpage": 2, "style": "r"}])
    _tampon(d[1])
    _tampon_noview(d, d[1])
    _tampon(d[2], (300, 150, 500, 220), 2)
    _tampon_noview(d, d[2])
    chemin = tmp_path / "riche.pdf"
    d.save(str(chemin), garbage=0)
    d.close()
    return chemin


def test_repli_image_conserve_signets_liens_destinations_etiquettes(tmp_path):
    src = _doc_riche(tmp_path)
    avant = pymupdf.open(str(src))
    toc, noms = avant.get_toc(), avant.resolve_names()
    labels = [avant[i].get_label() for i in range(4)]
    liens = [[(l["kind"], l.get("page")) for l in p.get_links()] for p in avant]
    avant.close()
    res = _traiter(src, tmp_path)
    assert res.statut == "securite" and res.pages_image == [2, 3], res.message
    apres = pymupdf.open(str(res.dst))
    assert apres.page_count == 4
    assert apres.get_toc() == toc
    assert apres.resolve_names() == noms
    assert [apres[i].get_label() for i in range(4)] == labels
    assert [[(l["kind"], l.get("page")) for l in p.get_links()] for p in apres] == liens
    # pages converties : contenu image, pas de champ interactif résiduel
    assert core.analyser(apres).pages == {}


def test_repli_image_pdf_a_une_seule_page(tmp_path):
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Seule page")
    _tampon(p)
    _tampon_noview(d, p)
    src = tmp_path / "une.pdf"
    d.save(str(src))
    d.close()
    res = _traiter(src, tmp_path)
    assert res.statut == "securite" and res.pages_image == [1]
    o = pymupdf.open(str(res.dst))
    assert o.page_count == 1 and o[0].rect == pymupdf.Rect(0, 0, 595, 842)


# --------------------------------------------------------------------------- #
# (1) garbage=1 : /Resources hérités (direct, indirect, nœuds /Pages imbriqués), images
# --------------------------------------------------------------------------- #
def _doc_images_heritees(tmp_path, indirect, nested, shared_page_res, annot_pages):
    import io
    from PIL import Image

    def jpg(couleur):
        b = io.BytesIO()
        Image.new("RGB", (60, 80), couleur).save(b, "JPEG")
        return b.getvalue()

    d = pymupdf.open()
    N = 6
    for _ in range(N):
        d.new_page(width=300, height=400)
    couleurs = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255), (0, 255, 255)]
    ims = [_obj(d, "<</Type/XObject/Subtype/Image/Width 60/Height 80/ColorSpace/DeviceRGB"
                   "/BitsPerComponent 8/Filter/DCTDecode>>", jpg(c)) for c in couleurs]
    racine = int(d.xref_get_key(d.pdf_catalog(), "Pages")[1].split()[0])
    for i, p in enumerate(d):
        c = _obj(d, "<<>>", b"q 100 0 0 130 %d %d cm /Im%d Do Q" % (20 + 20 * i, 20 + 10 * i, i))
        d.xref_set_key(p.xref, "Contents", "%d 0 R" % c)
        d.xref_set_key(p.xref, "Resources", "null")
        d.update_object(p.xref, d.xref_object(p.xref, compressed=True).replace("/Resources null", ""))
    xo = "<</XObject<<" + "".join("/Im%d %d 0 R" % (i, ims[i]) for i in range(N)) + ">>>>"
    if indirect:
        d.xref_set_key(racine, "Resources", "%d 0 R" % _obj(d, xo))
    else:
        d.xref_set_key(racine, "Resources", xo)
    if nested:
        kids = d.xref_get_key(racine, "Kids")[1]
        mid = d.get_new_xref()
        d.update_object(mid, "<</Type/Pages/Kids%s/Count %d/Parent %d 0 R>>" % (kids, N, racine))
        d.xref_set_key(racine, "Kids", "[%d 0 R]" % mid)
        for p in d:
            d.xref_set_key(p.xref, "Parent", "%d 0 R" % mid)
    if shared_page_res:
        r3 = _obj(d, xo)
        for i in (2, 3):
            d.xref_set_key(d[i].xref, "Resources", "%d 0 R" % r3)
    for i in annot_pages:
        _tampon(d[i], (150, 20, 280, 70))
    chemin = tmp_path / "images.pdf"
    d.save(str(chemin), garbage=0)
    d.close()
    return chemin


@pytest.mark.parametrize("indirect", [False, True])
@pytest.mark.parametrize("nested", [False, True])
@pytest.mark.parametrize("shared", [False, True])
@pytest.mark.parametrize("annots", [(0,), (0, 2, 5), (3,)])
def test_resources_herites_pages_image_seule_conservees(tmp_path, indirect, nested, shared, annots):
    """Pages sans texte (scans) : seul le rendu peut révéler une image perdue à l'enregistrement."""
    src = _doc_images_heritees(tmp_path, indirect, nested, shared, annots)
    res = _traiter(src, tmp_path)
    assert res.statut == "ok", res.message
    a, b = pymupdf.open(str(src)), pymupdf.open(str(res.dst))
    for n in range(6):
        pa = core._image(core._pixmap(a[n], 50, 3000))
        pb = core._image(core._pixmap(b[n], 50, 3000))
        # comparaison DIRECTE du rendu : sur une page sans texte, c'est la seule façon de voir
        # une image perdue (le contrôle du fichier écrit ne compare que le texte des autres pages)
        ecart = core._ecart_max(pa, pb).histogram()
        assert sum(ecart[core.SEUIL_CANAL + 1:]) < core.SEUIL_PIXELS, f"page {n + 1}"


# --------------------------------------------------------------------------- #
# (6) PDF chiffré (mot de passe propriétaire seul) : copie déchiffrée et identique
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("alg", ["AES_256", "AES_128", "RC4_128", "RC4_40"])
def test_chiffre_proprietaire_copie_sans_restriction_et_identique(tmp_path, alg):
    d = pymupdf.open()
    for i in range(3):
        p = d.new_page()
        p.insert_text((72, 72), f"Page {i + 1} rapport")
        p.insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(10, 10, 60, 30), "uri": "https://e.com"})
    d.set_toc([[1, "A", 1], [1, "B", 3]])
    src = tmp_path / "chiffre.pdf"
    d.save(str(src), encryption=getattr(pymupdf, "PDF_ENCRYPT_" + alg), owner_pw="secret", user_pw="",
           permissions=pymupdf.PDF_PERM_PRINT)
    d.close()
    res = _traiter(src, tmp_path)
    assert res.statut == "copie", res.message
    a, b = pymupdf.open(str(src)), pymupdf.open(str(res.dst))
    assert not b.is_encrypted and b.xref_get_key(-1, "Encrypt")[0] == "null"
    assert b.permissions & pymupdf.PDF_PERM_ASSEMBLE
    assert [p.get_text() for p in a] == [p.get_text() for p in b]
    assert a.get_toc() == b.get_toc()
    assert [len(p.get_links()) for p in a] == [len(p.get_links()) for p in b]


# --------------------------------------------------------------------------- #
# (1)(7) Le contrôle du fichier ÉCRIT voit ce que la copie en mémoire ne montre pas
# --------------------------------------------------------------------------- #
def test_controle_ecrit_signale_une_perte_invisible_en_memoire(tmp_path):
    """Un flux de formulaire portant une clé /F (« fichier externe » pour un flux) se rend bien
    en mémoire après ``bake`` mais perd son contenu une fois enregistré compressé : seul le
    contrôle du fichier écrit peut le voir."""
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Doc", fontsize=14)
    ap = _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 228 25]/Resources<</Font<</Helv<</Type/Font"
                 "/Subtype/Type1/BaseFont/Helvetica>>>>>>/F/Name>>", b"BT /Helv 25 Tf 0 5 Td (VALEUR) Tj ET")
    w = d.get_new_xref()
    d.update_object(w, "<</Type/Annot/Subtype/Widget/FT/Tx/T(t)/V(VALEUR)/Rect[72 600 300 625]/F 4/P %d 0 R"
                       "/AP<</N %d 0 R>>>>" % (p.xref, ap))
    _ajouter_annot(d, p, w)
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</Fields[%d 0 R]>>" % w)
    src = tmp_path / "f_flux.pdf"
    d.save(str(src), garbage=0, deflate=False)
    d.close()
    # prérequis : la perte n'existe qu'à l'écriture (sinon cette version de MuPDF ne la reproduit plus)
    t = pymupdf.open(str(src))
    t.bake(annots=True, widgets=True)
    en_memoire = "VALEUR" in t[0].get_text()
    t.save(str(tmp_path / "t.pdf"), garbage=1, deflate=True, encryption=pymupdf.PDF_ENCRYPT_NONE)
    ecrit = "VALEUR" in pymupdf.open(str(tmp_path / "t.pdf"))[0].get_text()
    if not (en_memoire and not ecrit):
        pytest.skip("cette version de MuPDF ne reproduit pas la perte à l'écriture")
    res = _traiter(src, tmp_path)
    assert res.statut == "alerte", (res.statut, res.message)
    assert "différente" in res.message


# --------------------------------------------------------------------------- #
# (7) Jamais d'exception, jamais de fichier temporaire, sortie relisible (mini-fuzz déterministe)
# --------------------------------------------------------------------------- #
def _graine(tmp_path):
    d = pymupdf.open()
    for i in range(3):
        p = d.new_page()
        p.insert_text((72, 72), f"Page {i + 1} " + "lorem " * 10)
        _tampon(p)
        p.add_highlight_annot(pymupdf.Rect(72, 60, 200, 80))
        p.insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(10, 10, 60, 30), "uri": "https://e.com"})
        w = pymupdf.Widget()
        w.field_type = pymupdf.PDF_WIDGET_TYPE_COMBOBOX
        w.field_name = f"cb{i}"
        w.choice_values = [("A", "Alpha"), ("B", "Beta")]
        w.field_value = "B"
        w.rect = pymupdf.Rect(72, 250, 300, 275)
        p.add_widget(w)
        s = pymupdf.Widget()
        s.field_type = pymupdf.PDF_WIDGET_TYPE_SIGNATURE
        s.field_name = f"sig{i}"
        s.rect = pymupdf.Rect(100, 600, 300, 650)
        p.add_widget(s)
    d.set_toc([[1, "A", 1], [1, "B", 3]])
    chemin = tmp_path / "graine.pdf"
    d.save(str(chemin), garbage=0, deflate=False)
    d.close()
    return chemin


_VALEURS = ["null", "0", "-1", "99999999999", "(abc)", "[]", "<<>>", "/Name", "true", "[1 0 R]", "1.5",
            "<</A 1>>", "[(a) (b)]", "[[(A)(Alpha)][(B)]]", "<FEFFD83D>", "[0 0 0 0]",
            "[1e38 1e38 -1e38 -1e38]", "[100 100 50 50]"]
_CLES = ["F", "Rect", "AP", "N", "V", "Opt", "Parent", "Kids", "FT", "Ff", "RV", "DA", "MK", "AS", "Contents",
         "Resources", "Annots", "MediaBox", "CropBox", "Rotate", "Subtype", "T", "AcroForm", "Perms", "Pages",
         "Count", "Type", "Fields", "BBox", "Matrix"]


def test_mini_fuzz_aucune_exception_aucun_temporaire_sortie_relisible(tmp_path):
    graine = _graine(tmp_path)
    alea = random.Random(20260507)
    sortie = tmp_path / "sortie"
    fichiers = []
    for k in range(12):
        d = pymupdf.open(str(graine))
        for _ in range(alea.randint(1, 4)):
            x = alea.randrange(1, d.xref_length())
            try:
                if d.xref_is_stream(x):
                    continue
                cle = alea.choice(d.xref_get_keys(x) or _CLES) if alea.random() < 0.3 else alea.choice(_CLES)
                d.xref_set_key(x, cle, alea.choice(_VALEURS))
            except Exception:
                pass
        chemin = tmp_path / f"m{k:02d}.pdf"
        try:
            d.save(str(chemin), garbage=0, deflate=False)
            fichiers.append(chemin)
        except Exception:
            pass
        d.close()
    brut = graine.read_bytes()
    for k in range(8):                        # mutations « octets »
        b = bytearray(brut)
        mode = alea.choice(["flip", "del", "zero", "trunc"])
        if mode == "flip":
            for _ in range(alea.randint(1, 20)):
                b[alea.randrange(len(b))] = alea.randrange(256)
        elif mode == "del":
            a = alea.randrange(len(b))
            del b[a:a + alea.randint(1, 300)]
        elif mode == "zero":
            a = alea.randrange(len(b))
            n = alea.randint(1, 300)
            b[a:a + n] = bytes(n)
        else:
            b = b[:alea.randrange(len(b) // 3, len(b))]
        chemin = tmp_path / f"b{k:02d}.pdf"
        chemin.write_bytes(bytes(b))
        fichiers.append(chemin)
    assert len(fichiers) >= 14
    for chemin in fichiers:
        res = core.aplatir_fichier(chemin, sortie)          # ne doit jamais lever
        assert res.statut in {"ok", "copie", "securite", "alerte", "erreur", "ignore"}, chemin.name
        assert not list(sortie.glob(".aplatir-*")), chemin.name
        if res.statut in ("ok", "securite", "copie"):
            o = pymupdf.open(str(res.dst))
            assert o.page_count >= 1 and not o.is_repaired, chemin.name
            o.close()


# --------------------------------------------------------------------------- #
# BUGS REPRODUITS (xfail strict : retirer le marqueur quand c'est corrigé)
# --------------------------------------------------------------------------- #
def _doc_objstm(tmp_path):
    d = pymupdf.open()
    for i in range(40):
        p = d.new_page()
        p.insert_text((72, 72), f"Rapport page {i}", fontsize=12)
        for k in range(6):
            p.insert_text((72, 100 + 20 * k), f"ligne {k} " * 6, fontsize=9)
        p.insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(10, 10, 60, 30),
                       "uri": "https://example.com/%d" % i})
    _tampon(d[0])
    src = tmp_path / "objstm.pdf"
    d.save(str(src), garbage=4, deflate=True, use_objstms=True)
    d.close()
    return src


@pytest.mark.xfail(strict=True, reason="R2-COR-1: l'enregistrement n'utilise pas de flux d'objets (use_objstms) : un PDF "
                   "moderne (Word, Chrome, LibreOffice, Acrobat « optimisé ») qui en utilise ressort 3 à 5 fois plus gros")
def test_la_taille_de_sortie_reste_proche_de_celle_de_l_entree(tmp_path):
    src = _doc_objstm(tmp_path)
    res = _traiter(src, tmp_path)
    assert res.statut == "ok", res.message
    assert res.dst.stat().st_size <= 1.5 * src.stat().st_size, (src.stat().st_size, res.dst.stat().st_size)


@pytest.mark.xfail(strict=True, reason="R2-COR-2: message d'erreur technique (« FzErrorFormat : code=7: cycle in page "
                   "tree ») montré tel quel à l'utilisateur au lieu d'un message clair")
def test_arbre_de_pages_en_boucle_message_clair(tmp_path):
    d = pymupdf.open()
    for _ in range(3):
        d.new_page()
    racine = int(d.xref_get_key(d.pdf_catalog(), "Pages")[1].split()[0])
    d.xref_set_key(racine, "Kids", "[%d 0 R]" % racine)
    src = tmp_path / "boucle.pdf"
    d.save(str(src), garbage=0, deflate=False)
    d.close()
    res = _traiter(src, tmp_path)
    assert res.statut == "erreur"
    assert "FzError" not in res.message and "code=" not in res.message, res.message


@pytest.mark.xfail(strict=True, reason="R2-COR-3: /Resources de page invalide -> bake ne grave rien ; le fichier sort avec tampon "
                   "et signature encore interactifs ET /AcroForm purgé (perdus à la fusion Acrobat) : simple « alerte », "
                   "sans repli image automatique des pages restantes")
def test_pages_restees_interactives_apres_gravure_sont_converties_en_image(tmp_path):
    d = pymupdf.open()
    p = d.new_page(width=400, height=400)
    p.insert_text((30, 50), "texte")
    _tampon(p, (100, 150, 250, 200))
    w = d.get_new_xref()
    ap = _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 150 40]>>", b"0 0 1 rg 0 0 150 40 re f")
    d.update_object(w, "<</Type/Annot/Subtype/Widget/FT/Sig/T(S)/V<</Type/Sig/Filter/Adobe.PPKLite"
                       "/SubFilter/adbe.pkcs7.detached/ByteRange[0 0 0 0]/Contents<%s>>>/Rect[50 300 200 340]/F 132"
                       "/P %d 0 R/AP<</N %d 0 R>>>>" % ("00" * 32, p.xref, ap))
    _ajouter_annot(d, p, w)
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</Fields[%d 0 R]/SigFlags 3>>" % w)
    d.xref_set_key(p.xref, "Resources", "0")            # /Resources de type invalide
    src = tmp_path / "res_invalide.pdf"
    d.save(str(src), garbage=0)
    d.close()
    res = _traiter(src, tmp_path)
    out = pymupdf.open(str(res.dst))
    assert core.analyser(out).pages == {}, (res.statut, res.message)
