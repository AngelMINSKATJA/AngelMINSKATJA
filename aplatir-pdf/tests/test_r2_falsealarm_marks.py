"""2e passe d'audit (angle « fausses alertes », partie 2) : le contrôle « marque ajoutée »
(``_marque_ajoutee``) et la conversion en image qui s'ensuit.

Quand MuPDF NE SAIT PAS afficher à l'écran une marque que ``bake`` sait graver (champ sans /T ou
sans /FT, annotation « Invisible »...), le contrôle croit à une marque « ajoutée » et remplace la
page par l'image de ce que MuPDF affichait... c'est-à-dire SANS la marque : une signature visible
dans Acrobat / poppler disparaît, avec seulement l'étiquette orange « Aplati (image) ».
Avant le correctif de la 1re passe (contrôle de la seule marque perdue) la signature était gravée.

Les tests ``xfail(strict=True)`` démontrent un défaut reproduit (identifiant R2-FA-n).
"""
import pymupdf
import pytest

import aplatir_core as core

RECT = (100, 600, 250, 650)          # espace utilisateur PDF (origine en bas à gauche), A4 = 595 x 842
GAUCHE_ROUGE = b"1 0 0 rg 0 0 75 50 re f 0 0 1 rg 75 0 75 50 re f"


def _formulaire(d, flux=GAUCHE_ROUGE):
    xref = d.get_new_xref()
    d.update_object(xref, "<</Type/XObject/Subtype/Form/BBox[0 0 150 50]/Resources<<>>>>")
    d.update_stream(xref, flux)
    return xref


def _doc():
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Rapport de fin de fabrication", fontsize=14)
    return d, p


def _ajouter_annot(d, p, xref):
    cur = d.xref_get_key(p.xref, "Annots")
    d.xref_set_key(p.xref, "Annots", (cur[1].rstrip("]") + " %d 0 R]" % xref) if cur[0] == "array"
                   else "[%d 0 R]" % xref)


def _pdf_signature(tmp_path, variante):
    """Signature SIGNÉE (visible : rouge | bleu) dont le widget est déformé de la façon indiquée."""
    d, p = _doc()
    ap = _formulaire(d)
    sigv = d.get_new_xref()
    d.update_object(sigv, "<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached"
                          "/Contents<00>/ByteRange[0 0 0 0]>>")
    w = d.get_new_xref()
    rect = "[%g %g %g %g]" % RECT
    commun = f"/Type/Annot/Subtype/Widget/Rect{rect}/F 132/P {p.xref} 0 R/AP<</N {ap} 0 R>>"
    nom = {"normal": "/T(Sig1)", "sans_T": "", "sans_FT": "/T(Sig1)"}[variante]
    ft = "" if variante == "sans_FT" else "/FT/Sig"
    d.update_object(w, f"<<{commun}{ft}{nom}/V {sigv} 0 R>>")
    _ajouter_annot(d, p, w)
    d.xref_set_key(d.pdf_catalog(), "AcroForm", "<</Fields[%d 0 R]/SigFlags 3>>" % w)
    chemin = tmp_path / f"signature_{variante}.pdf"
    d.save(str(chemin))
    d.close()
    return chemin


def _couleur_au_centre(chemin):
    d = pymupdf.open(str(chemin))
    try:
        p = d[0]
        pix = p.get_pixmap(dpi=72)
        x0, y0, x1, y1 = RECT
        # moitié gauche de la signature (rouge) : milieu vertical, 1/4 de la largeur
        return pix.pixel(int(x0 + (x1 - x0) / 4), int(p.rect.height - (y0 + y1) / 2))
    finally:
        d.close()


def _rouge(rgb):
    return rgb[0] > 200 and rgb[1] < 80 and rgb[2] < 80


def test_signature_normale_est_gravee(tmp_path):
    """Garde-fou : une signature signée, bien formée, reste visible dans la sortie."""
    res = core.aplatir_fichier(_pdf_signature(tmp_path, "normal"), tmp_path / "s")
    assert res.statut == "ok", f"{res.statut} : {res.message}"
    assert _rouge(_couleur_au_centre(res.dst))


def test_signature_signee_sans_nom_de_champ_reste_visible(tmp_path):
    res = core.aplatir_fichier(_pdf_signature(tmp_path, "sans_T"), tmp_path / "s")
    assert _rouge(_couleur_au_centre(res.dst)), \
        f"signature absente de la sortie ({res.statut} : {res.message})"


def test_widget_sans_ft_reste_visible(tmp_path):
    res = core.aplatir_fichier(_pdf_signature(tmp_path, "sans_FT"), tmp_path / "s")
    assert _rouge(_couleur_au_centre(res.dst)), \
        f"marque absente de la sortie ({res.statut} : {res.message})"


def test_marque_absente_de_l_ecran_jamais_silencieuse(tmp_path):
    """Si la marque ne peut pas rester dans la sortie, le statut doit au moins être « alerte »
    (et non l'étiquette « securite » habituelle, que l'utilisateur apprend à ignorer)."""
    res = core.aplatir_fichier(_pdf_signature(tmp_path, "sans_T"), tmp_path / "s")
    if _rouge(_couleur_au_centre(res.dst)):
        return                                 # marque conservée : rien à dire
    assert res.statut in ("alerte", "erreur"), f"{res.statut} : {res.message}"




# --------------------------------------------------------------------------- #
# R2-FA-5 : drapeau « Invisible » (bit 1 de /F) : MuPDF masque, la norme et poppler affichent
# --------------------------------------------------------------------------- #
def _pdf_tampon(tmp_path, flags):
    d, p = _doc()
    ap = _formulaire(d)
    x = d.get_new_xref()
    d.update_object(x, "<</Type/Annot/Subtype/Stamp/Rect[%g %g %g %g]/F %d/AP<</N %d 0 R>>/Name/Custom>>"
                    % (RECT + (flags, ap)))
    _ajouter_annot(d, p, x)
    chemin = tmp_path / f"tampon_f{flags}.pdf"
    d.save(str(chemin))
    d.close()
    return chemin


@pytest.mark.parametrize("flags", [0, 4], ids=["F0", "F4-Print"])
def test_tampon_visible_est_grave(tmp_path, flags):
    """Garde-fou."""
    res = core.aplatir_fichier(_pdf_tampon(tmp_path, flags), tmp_path / "s")
    assert res.statut == "ok", f"{res.statut} : {res.message}"
    assert _rouge(_couleur_au_centre(res.dst))


@pytest.mark.parametrize("flags", [1, 5], ids=["F1-Invisible", "F5-Invisible+Print"])
def test_tampon_avec_drapeau_invisible_est_grave(tmp_path, flags):
    res = core.aplatir_fichier(_pdf_tampon(tmp_path, flags), tmp_path / "s")
    assert _rouge(_couleur_au_centre(res.dst)), f"tampon absent ({res.statut} : {res.message})"


# --------------------------------------------------------------------------- #
# R2-FA-6 : annotations écrites EN LIGNE dans /Annots (``/Annots [ << ... >> ]``) : invisibles pour
# ``page.annots()`` -> aucune page signalée -> « copie » telle quelle, annotations toujours interactives
# --------------------------------------------------------------------------- #
def _pdf_annotation_en_ligne(tmp_path):
    d, p = _doc()
    ap = _formulaire(d)
    d.xref_set_key(p.xref, "Annots", "[<</Type/Annot/Subtype/Stamp/Rect[%g %g %g %g]/F 4/AP<</N %d 0 R>>"
                                     "/Name/Custom>>]" % (RECT + (ap,)))
    chemin = tmp_path / "annotation_en_ligne.pdf"
    d.save(str(chemin))
    d.close()
    return chemin


def test_annotation_en_ligne_dans_annots_est_gravee(tmp_path):
    src = _pdf_annotation_en_ligne(tmp_path)
    d = pymupdf.open(str(src))
    try:
        p = d[0]
        assert p.get_pixmap(dpi=36, annots=True).samples != p.get_pixmap(dpi=36, annots=False).samples, \
            "pré-requis : MuPDF affiche l'annotation"
    finally:
        d.close()
    res = core.aplatir_fichier(src, tmp_path / "s")
    o = pymupdf.open(str(res.dst))
    try:
        p = o[0]
        reste = o.xref_get_key(p.xref, "Annots")[1]
        sans_annots = p.get_pixmap(dpi=72, annots=False)
        gravee = _rouge(sans_annots.pixel(int(RECT[0] + 20), int(p.rect.height - 625)))
        assert gravee and "Stamp" not in reste, f"{res.statut} : {res.message} ; /Annots = {reste[:60]}"
    finally:
        o.close()
