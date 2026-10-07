"""Robustesse : géométrie des pages (rotation, CropBox, origine, UserUnit, pages énormes/minuscules).

Chaque cas est joué dans les DEUX chemins : « normal » (bake) et « repli image » (page
remplacée par son rendu, forcé en monkeypatchant ``_pages_alterees``). La page doit garder
la même taille et la même orientation, la même apparence, et le même nombre de pages.
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant ROB-n du rapport).
"""
import pymupdf
import pytest
from PIL import Image, ImageChops

import aplatir_core as core


def _forcer_repli(monkeypatch):
    monkeypatch.setattr(core, "_pages_alterees", lambda orig, doc, numeros, zones=None: list(numeros))


def _pdf(chemin, mediabox=(0, 0, 595, 842), cropbox=None, rotate=0, userunit=None, pages=1):
    """Pages avec 3 carrés (rouge en bas à gauche, bleu en haut à droite, vert au centre de la
    MediaBox) + un tampon : toute erreur d'orientation, de taille ou de décalage se voit."""
    x0, y0, x1, y1 = mediabox
    d = pymupdf.open()
    for _ in range(pages):
        d.new_page(width=x1 - x0, height=y1 - y0)
    for p in d:
        s = min(x1 - x0, y1 - y0) * 0.1
        contenu = (f"q 1 0 0 rg {x0 + 10} {y0 + 10} {s} {s} re f "
                   f"0 0 1 rg {x1 - 10 - s} {y1 - 10 - s} {s} {s} re f "
                   f"0 .6 0 rg {(x0 + x1) / 2 - s / 2} {(y0 + y1) / 2 - s / 2} {s} {s} re f Q")
        xr = d.get_new_xref()
        d.update_object(xr, "<<>>")
        d.update_stream(xr, contenu.encode())
        d.xref_set_key(p.xref, "Contents", f"{xr} 0 R")
        d.xref_set_key(p.xref, "MediaBox", f"[{x0} {y0} {x1} {y1}]")
        if cropbox:
            d.xref_set_key(p.xref, "CropBox", "[%g %g %g %g]" % tuple(cropbox))
        if rotate:
            d.xref_set_key(p.xref, "Rotate", str(rotate))
        if userunit:
            d.xref_set_key(p.xref, "UserUnit", str(userunit))
    d = pymupdf.open("pdf", d.tobytes())
    for p in d:
        v = p.rect
        zone = pymupdf.Rect(v.x0 + v.width * .2, v.y0 + v.height * .2,
                            v.x0 + v.width * .5, v.y0 + v.height * .3) * p.derotation_matrix
        zone.normalize()
        p.add_stamp_annot(zone, stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


def _rendus(chemin, dpi, annots):
    d = pymupdf.open(str(chemin))
    try:
        res = []
        for p in d:
            pix = p.get_pixmap(dpi=dpi, annots=annots, alpha=False)
            res.append(Image.frombytes("RGB", (pix.width, pix.height), pix.samples))
        return res
    finally:
        d.close()


def _meme_rendu(src, dst):
    d = pymupdf.open(str(src))
    petit = min(d[0].rect.width, d[0].rect.height)
    d.close()
    dpi = max(36, int(60 * 72 / petit) + 1)          # au moins ~60 px sur le petit côté
    tolerance = 0.003 if petit >= 100 else 0.03      # page minuscule : l'image 200 dpi est agrandie
    avant, apres = _rendus(src, dpi, True), _rendus(dst, dpi, False)
    assert len(avant) == len(apres)
    for a, b in zip(avant, apres):
        assert a.size == b.size, (a.size, b.size)
        ecarts = ImageChops.difference(a, b).convert("L").histogram()[60:]
        assert sum(ecarts) <= tolerance * a.width * a.height, sum(ecarts)   # le rééchantillonnage seul


CAS = {
    "simple": dict(),
    "rot90": dict(rotate=90),
    "rot180": dict(rotate=180),
    "rot270": dict(rotate=270),
    "rot_negatif": dict(rotate=-90),
    "rot450": dict(rotate=450),
    "crop": dict(cropbox=(50, 60, 400, 700)),
    "crop_rot90": dict(cropbox=(50, 60, 400, 700), rotate=90),
    "origine": dict(mediabox=(100, 200, 695, 1042)),
    "origine_negative_rot90": dict(mediabox=(-300, -400, 295, 442), rotate=90),
    "origine_crop_rot180": dict(mediabox=(100, 200, 695, 1042), cropbox=(150, 260, 500, 900), rotate=180),
    "userunit2": dict(userunit=2),
    "A0": dict(mediabox=(0, 0, 2384, 3370)),
    "minuscule": dict(mediabox=(0, 0, 12, 12)),
}


@pytest.mark.parametrize("repli", [False, True], ids=["normal", "repli_image"])
@pytest.mark.parametrize("cas", list(CAS))
def test_geometrie_conservee(tmp_path, monkeypatch, cas, repli):
    src = _pdf(tmp_path / "g.pdf", pages=2, **CAS[cas])
    if repli:
        _forcer_repli(monkeypatch)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == ("securite" if repli else "ok"), res.message
    assert res.pages_image == ([1, 2] if repli else [])
    _meme_rendu(src, res.dst)


@pytest.mark.parametrize("repli", [False, True], ids=["normal", "repli_image"])
@pytest.mark.parametrize("mediabox", ["[0 0 0 0]", "[595 842 0 0]", "[0 0 -100 -100]", "null", "[0 0 595]",
                                      "[0 0 14400 14400]", "[0 0 50000 100]", "[0 0 1 1]"])
def test_pages_degenerees_ou_extremes_pas_de_plantage(tmp_path, monkeypatch, mediabox, repli):
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "x")
    p.add_stamp_annot(pymupdf.Rect(100, 150, 300, 220), stamp=0)
    d.xref_set_key(p.xref, "MediaBox", mediabox)
    src = tmp_path / "d.pdf"
    src.write_bytes(d.tobytes())
    if repli:
        _forcer_repli(monkeypatch)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut in ("ok", "securite"), res.message
    out = pymupdf.open(str(res.dst))
    try:
        assert out.page_count == 1
        assert not list(out[0].annots())
    finally:
        out.close()
    assert [x.name for x in (tmp_path / "out").iterdir()] == ["[a]- d.pdf"]


# --------------------------------------------------------------------------- #
# ROB-3 : /CropBox hérité du nœud /Pages -> la page image est rognée
# --------------------------------------------------------------------------- #
def _pdf_cropbox_heritee(chemin):
    d = pymupdf.open()
    for _ in range(2):
        p = d.new_page()
        p.draw_rect(pymupdf.Rect(30, 40, 90, 100), color=None, fill=(1, 0, 0))
        p.draw_rect(pymupdf.Rect(400, 40, 460, 100), color=None, fill=(0, 0, 1))
    pages = int(d.xref_get_key(d.pdf_catalog(), "Pages")[1].split()[0])
    d.xref_set_key(pages, "CropBox", "[20 30 500 700]")          # hérité par toutes les pages
    d = pymupdf.open("pdf", d.tobytes())
    for p in d:
        p.add_stamp_annot(pymupdf.Rect(150, 150, 350, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


def test_cropbox_heritee_chemin_normal(tmp_path):
    src = _pdf_cropbox_heritee(tmp_path / "h.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    _meme_rendu(src, res.dst)


@pytest.mark.xfail(strict=True, reason="ROB-3: repli image -> la nouvelle page n'a pas de /CropBox propre et hérite "
                                       "de celui du nœud /Pages : l'image est rognée (20 pt à gauche, 30 pt en bas)")
def test_cropbox_heritee_repli_image(tmp_path, monkeypatch):
    src = _pdf_cropbox_heritee(tmp_path / "h.pdf")
    _forcer_repli(monkeypatch)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "securite", res.message
    _meme_rendu(src, res.dst)
