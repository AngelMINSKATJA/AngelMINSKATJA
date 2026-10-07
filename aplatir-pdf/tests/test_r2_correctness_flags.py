"""2e passe d'audit (angle « correction du cœur ») : drapeau « Invisible » (bit 1 de /F) sur TOUS les
types de marques (signature signée, surlignage, texte libre, tampon).

MuPDF ne dessine pas une annotation ou un champ dont /F a le bit 1 ; ISO 32000-1 §12.5.3 limite pourtant
ce bit aux annotations NON standard, et poppler / pdfium / Acrobat les affichent. Le contrôle visuel compare
au rendu de MuPDF, ``bake`` ne grave pas la marque : elle disparaît de la sortie avec le statut « ok »
(R2-COR-4, même cause que R2-FA-5, ici pour les signatures signées et les autres types d'annotation).

Correctif validé : dans ``_normaliser_pour_rendu`` (donc sur ``doc`` ET ``orig``), pour chaque annotation et
chaque champ de la page, remplacer /F par ``F & ~1`` quand c'est un entier direct.
"""
import pymupdf
import pytest

import aplatir_core as core

RECT = (50, 100, 250, 150)           # espace page, origine en HAUT à gauche (300 x 300)


def _obj(d, corps, flux=None):
    x = d.get_new_xref()
    d.update_object(x, corps)
    if flux is not None:
        d.update_stream(x, flux)
    return x


def _pixels_non_blancs(chemin, rect):
    d = pymupdf.open(str(chemin))
    try:
        pix = d[0].get_pixmap(dpi=50, clip=pymupdf.Rect(*rect), alpha=False)
        s = pix.samples
        return sum(1 for i in range(0, len(s), 3) if s[i:i + 3] != b"\xff\xff\xff")
    finally:
        d.close()


def _pdf(tmp_path, kind, flags):
    d = pymupdf.open()
    p = d.new_page(width=300, height=300)
    p.insert_text((20, 30), "Rapport", fontsize=12)
    rect = pymupdf.Rect(*RECT)
    if kind == "sig":
        ap = _obj(d, "<</Type/XObject/Subtype/Form/BBox[0 0 200 50]/Resources<<>>>>", b"0 0 1 rg 0 0 200 50 re f")
        w = d.get_new_xref()
        d.update_object(w, "<</Type/Annot/Subtype/Widget/FT/Sig/T(S)/V<</Type/Sig/Filter/Adobe.PPKLite"
                           "/SubFilter/adbe.pkcs7.detached/ByteRange[0 0 0 0]/Contents<%s>>>/Rect[50 150 250 200]"
                           "/F 4/P %d 0 R/AP<</N %d 0 R>>>>" % ("00" * 32, p.xref, ap))
        d.xref_set_key(p.xref, "Annots", "[%d 0 R]" % w)
        d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</Fields[%d 0 R]/SigFlags 3>>" % w)
        x = w
    else:
        if kind == "stamp":
            a = p.add_stamp_annot(rect, stamp=0)
        elif kind == "highlight":
            a = p.add_highlight_annot(rect)
        else:
            a = p.add_freetext_annot(rect, "Bon pour accord", fontsize=14, fill_color=(1, 1, 0))
        x = a.xref
    d.xref_set_key(x, "F", str(flags))
    chemin = tmp_path / f"{kind}_f{flags}.pdf"
    d.save(str(chemin))
    d.close()
    return chemin


KINDS = ["sig", "stamp", "highlight", "freetext"]


@pytest.mark.parametrize("flags", [4, 0, 132], ids=["F4-Print", "F0", "F132-Print+Locked"])
@pytest.mark.parametrize("kind", KINDS)
def test_marque_visible_est_gravee(tmp_path, kind, flags):
    """Garde-fou : drapeaux ordinaires -> la marque reste visible dans la sortie, statut « ok »."""
    res = core.aplatir_fichier(_pdf(tmp_path, kind, flags), tmp_path / "s")
    assert res.statut == "ok", f"{res.statut} : {res.message}"
    assert _pixels_non_blancs(res.dst, RECT) > 300


@pytest.mark.parametrize("flags", [1, 5], ids=["F1-Invisible", "F5-Invisible+Print"])
@pytest.mark.parametrize("kind", KINDS)
def test_marque_avec_drapeau_invisible_est_gravee(tmp_path, kind, flags):
    res = core.aplatir_fichier(_pdf(tmp_path, kind, flags), tmp_path / "s")
    assert _pixels_non_blancs(res.dst, RECT) > 300, f"marque absente de la sortie ({res.statut} : {res.message})"
