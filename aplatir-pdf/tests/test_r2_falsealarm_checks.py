"""2e passe d'audit (angle « fausses alertes ») : les contrôles ajoutés par la 1re passe
(``is_repaired`` -> alerte, refus des XFA dynamiques) sont-ils proportionnés ET efficaces ?

Les tests ``xfail(strict=True)`` démontrent un défaut reproduit (identifiant R2-FA-n) ; les autres
sont des garde-fous de non-régression (pas de fausse alerte sur des documents ordinaires).
"""
import re

import pymupdf
import pytest

import aplatir_core as core

SIG_STAMP = (100, 400, 260, 450)


# --------------------------------------------------------------------------- #
# Outils
# --------------------------------------------------------------------------- #
def _doc(pages=4):
    d = pymupdf.open()
    for i in range(pages):
        p = d.new_page()
        p.insert_text((72, 72), f"Rapport de fin de fabrication - page {i + 1}", fontsize=14)
    return d


def _avec_tampon(d, page=0):
    a = d[page].add_stamp_annot(pymupdf.Rect(*SIG_STAMP), stamp=pymupdf.STAMP_Approved)
    a.update()


def _octets(d) -> bytes:
    """Octets d'un PDF « classique » (table xref texte, pas de flux d'objets)."""
    return d.tobytes(garbage=0, deflate=False)


def _ecrire(chemin, octets):
    chemin.write_bytes(octets)
    return chemin


def _aplatir(src, tmp_path):
    return core.aplatir_fichier(src, tmp_path / "sortie")


def _decaler_offset_xref(octets: bytes, numero: int, delta: int) -> bytes:
    """Fausse le décalage de l'objet ``numero`` dans la table xref classique."""
    m = re.search(rb"startxref\s+(\d+)", octets)
    debut = int(m.group(1))
    h = re.compile(rb"xref\s+(\d+) (\d+)\s*\n").match(octets, debut)
    assert h is not None, "table xref classique attendue"
    premier = int(h.group(1))
    # chaque entrée occupe 20 octets
    pos = h.end() + 20 * (numero - premier)
    entree = octets[pos:pos + 20]
    assert re.fullmatch(rb"\d{10} \d{5} n\s*\n?", entree[:20] if len(entree) == 20 else entree), entree
    neuf = b"%010d" % (int(entree[:10]) + delta) + entree[10:]
    return octets[:pos] + neuf + octets[pos + 20:]


# --------------------------------------------------------------------------- #
# R2-FA-1 : la réparation « paresseuse » de MuPDF n'est pas signalée
# --------------------------------------------------------------------------- #
def _pdf_offset_page_faux(tmp_path):
    d = _doc(4)
    _avec_tampon(d, 0)
    octets = _octets(d)
    d.close()
    p = pymupdf.open(stream=octets, filetype="pdf")
    cible = p[2].xref
    p.close()
    return _ecrire(tmp_path / "offset_page.pdf", _decaler_offset_xref(octets, cible, 11))


def test_reparation_detectee_pendant_le_parcours_des_pages_est_signalee(tmp_path):
    """MuPDF ne détecte parfois le défaut qu'EN CHARGEANT la page (``is_repaired`` est False à
    l'ouverture, True après) : le contrôle doit être fait APRÈS le parcours des pages."""
    src = _pdf_offset_page_faux(tmp_path)
    d = pymupdf.open(str(src))
    if d.is_repaired:
        pytest.skip("cette version de MuPDF répare dès l'ouverture")
    for p in d:
        p.get_text()
    paresseuse = d.is_repaired
    d.close()
    if not paresseuse:
        pytest.skip("cette version de MuPDF ne répare pas ce défaut")
    res = _aplatir(src, tmp_path)
    assert res.statut == "alerte", f"{res.statut} : {res.message}"




# --------------------------------------------------------------------------- #
# R2-FA-2 : données parasites / zéros APRÈS %%EOF (> 1 Kio) = fausse alerte « fichier endommagé »
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("queue", [b"\0" * 4096, b"Pied de page ajoute par une passerelle\n" * 100],
                         ids=["zeros", "texte"])
@pytest.mark.parametrize("annote", [False, True], ids=["copie", "tampon"])
def test_donnees_apres_eof_ne_sont_pas_un_fichier_endommage(tmp_path, queue, annote):
    """Scanners (Canon...), impression Firefox/cairo : le fichier est complet, seule la fin est
    rembourrée. MuPDF cherche `startxref` dans les 1024 derniers octets, « répare » (is_repaired)
    et n'a rien perdu : pas de raison d'écrire « signature(s) ou page(s) peut-être manquantes »."""
    d = _doc(3)
    if annote:
        _avec_tampon(d, 0)
    octets = _octets(d) + queue
    d.close()
    src = _ecrire(tmp_path / "rembourre.pdf", octets)
    t = pymupdf.open(str(src))
    reparee = t.is_repaired
    t.close()
    if not reparee:
        pytest.skip("cette version de MuPDF ne répare pas ce fichier")
    res = _aplatir(src, tmp_path)
    assert res.statut in ("ok", "copie"), f"{res.statut} : {res.message}"




def test_fichier_tronque_reste_signale(tmp_path):
    """Garde-fou : un fichier TRONQUÉ (fin coupée) doit continuer à déclencher l'alerte."""
    d = _doc(3)
    _avec_tampon(d, 0)
    octets = _octets(d)
    d.close()
    src = _ecrire(tmp_path / "tronque.pdf", octets[:-60])
    t = pymupdf.open(str(src))
    reparee = t.is_repaired
    t.close()
    if not reparee:
        pytest.skip("cette version de MuPDF ne répare pas ce fichier")
    res = _aplatir(src, tmp_path)
    assert res.statut == "alerte" and "endommagé" in res.message


def test_queue_courte_apres_eof_sans_reparation_reste_ok(tmp_path):
    """Garde-fou : un saut de ligne / un peu de bruit (< 1 Kio) après %%EOF n'a jamais été une alerte."""
    d = _doc(2)
    _avec_tampon(d, 0)
    octets = _octets(d) + b"\r\n" + b"\0" * 100
    d.close()
    res = _aplatir(_ecrire(tmp_path / "court.pdf", octets), tmp_path)
    assert res.statut == "ok", f"{res.statut} : {res.message}"


# --------------------------------------------------------------------------- #
# R2-FA-3 : détection XFA dynamique = « aucune page n'a d'élément » (ni trop large, ni assez)
# --------------------------------------------------------------------------- #
def _xfa_dans_catalogue(d, *, needs_rendering: bool):
    flux = d.get_new_xref()
    d.update_object(flux, "<<>>")
    d.update_stream(flux, b"<xdp:xdp xmlns:xdp='http://ns.adobe.com/xdp/'><template/></xdp:xdp>")
    cat = d.pdf_catalog()
    d.xref_set_key(cat, "AcroForm", "<</Fields[]/XFA[(template) %d 0 R]>>" % flux)
    if needs_rendering:
        d.xref_set_key(cat, "NeedsRendering", "true")


def _pdf_xfa_dynamique_avec_champ(tmp_path):
    """Formulaire XFA dynamique (/NeedsRendering true) : la seule page est le bandeau « Please
    wait... », mais elle porte un champ (ex. champ de signature de l'AcroForm)."""
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Please wait...", fontsize=18)
    p.insert_text((72, 100), "If this message is not eventually replaced by the proper contents of the "
                  "document, your PDF viewer may not be able to display this type of document.",
                  fontsize=8)
    w = pymupdf.Widget()
    w.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT
    w.field_name = "form1[0].Champ[0]"
    w.field_value = ""
    w.rect = pymupdf.Rect(72, 300, 300, 320)
    p.add_widget(w)
    _xfa_dans_catalogue(d, needs_rendering=True)
    chemin = tmp_path / "xfa_dynamique_champ.pdf"
    d.save(str(chemin))
    d.close()
    return chemin


def test_xfa_dynamique_dont_le_bandeau_porte_un_champ_est_refuse(tmp_path):
    res = _aplatir(_pdf_xfa_dynamique_avec_champ(tmp_path), tmp_path)
    assert res.statut == "erreur" and "XFA" in res.message, f"{res.statut} : {res.message}"


def test_xfa_residuel_sur_document_ordinaire_n_est_pas_une_erreur(tmp_path):
    d = _doc(3)
    _xfa_dans_catalogue(d, needs_rendering=False)
    src = tmp_path / "xfa_residuel.pdf"
    d.save(str(src))
    d.close()
    res = _aplatir(src, tmp_path)
    assert res.statut in ("copie", "ok"), f"{res.statut} : {res.message}"


def test_xfa_dynamique_sans_champ_reste_refuse(tmp_path):
    """Garde-fou : le cas déjà couvert (bandeau seul, aucun champ) reste une erreur claire."""
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Please wait...", fontsize=18)
    _xfa_dans_catalogue(d, needs_rendering=True)
    src = tmp_path / "xfa_bandeau.pdf"
    d.save(str(src))
    d.close()
    res = _aplatir(src, tmp_path)
    assert res.statut == "erreur" and "XFA" in res.message
