"""Tests de base du coeur de traitement (les cas tordus sont dans les autres fichiers)."""
import pymupdf
import pytest

import aplatir_core as core


def _pdf_avec_elements(chemin):
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Rapport de fin de fabrication", fontsize=16)
    p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    p.add_freetext_annot(pymupdf.Rect(72, 150, 280, 180), "Bon pour accord", fontsize=12)
    p.add_ink_annot([[(72, 400), (150, 380), (220, 420), (300, 390)]])
    w = pymupdf.Widget()
    w.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT
    w.field_name, w.field_value, w.rect = "nom", "Jean Dupont", pymupdf.Rect(72, 250, 300, 275)
    p.add_widget(w)
    d.save(str(chemin))
    d.close()


def _pdf_signature_image(chemin):
    """Champ signature dont l'apparence est un rectangle rouge (comme un outil de e-signature)."""
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Page signée")
    s = pymupdf.Widget()
    s.field_type = pymupdf.PDF_WIDGET_TYPE_SIGNATURE
    s.field_name, s.rect = "Signature1", pymupdf.Rect(100, 600, 300, 650)
    p.add_widget(s)
    xref_w = [w for w in p.widgets()][0].xref
    ap = d.get_new_xref()
    d.update_object(ap, "<</Type/XObject/Subtype/Form/BBox[0 0 200 50]/Resources<<>>>>")
    d.update_stream(ap, b"q 1 0 0 rg 0 0 200 50 re f Q")
    d.xref_set_key(xref_w, "AP", f"<</N {ap} 0 R>>")
    d.save(str(chemin))
    d.close()


def test_nom_de_sortie(tmp_path):
    assert core.chemin_sortie(tmp_path / "a" / "Rapport 1.pdf", tmp_path / "out").name == "[a]- Rapport 1.pdf"


def test_aplatit_et_prefixe(tmp_path):
    src = tmp_path / "entree.pdf"
    _pdf_avec_elements(src)
    res = core.aplatir_fichier(src, tmp_path / "sortie")
    assert res.statut == "ok", res.message
    assert res.dst == tmp_path / "sortie" / "[a]- entree.pdf"
    assert res.dst.exists() and src.exists()
    out = pymupdf.open(str(res.dst))
    assert core.analyser(out).pages == {}
    assert "Rapport de fin de fabrication" in out[0].get_text()   # texte conservé (vectoriel)
    out.close()


def test_signature_visible_apres_aplatissement_et_fusion(tmp_path):
    src = tmp_path / "sig.pdf"
    _pdf_signature_image(src)
    res = core.aplatir_fichier(src, tmp_path)
    assert res.statut in ("ok", "securite"), res.message
    assert res.signatures == 1
    fusion = pymupdf.open()
    fusion.insert_pdf(pymupdf.open(str(res.dst)))
    pix = fusion[0].get_pixmap(dpi=72, annots=False)
    # le point (200, 625) (centre de la signature) doit être rouge dans le PDF fusionné
    r, g, b = pix.pixel(200, 625)[:3]
    assert (r, g, b) == (255, 0, 0)


def test_rien_a_aplatir_copie_identique(tmp_path):
    src = tmp_path / "simple.pdf"
    d = pymupdf.open()
    d.new_page().insert_text((72, 72), "rien")
    d.save(str(src))
    d.close()
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "copie"
    assert res.dst.read_bytes() == src.read_bytes()


def test_deja_prefixe_ignore(tmp_path):
    src = tmp_path / "[a]- deja.pdf"
    _pdf_avec_elements(src)
    assert core.aplatir_fichier(src, tmp_path / "out").statut == "ignore"


@pytest.mark.parametrize("contenu", [b"", b"pas un pdf du tout", b"%PDF-1.4\nbidon"])
def test_fichiers_invalides_donnent_une_erreur_propre(tmp_path, contenu):
    src = tmp_path / "casse.pdf"
    src.write_bytes(contenu)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and res.message
    assert not list((tmp_path / "out").glob("*")) if (tmp_path / "out").exists() else True


def test_mot_de_passe(tmp_path):
    src = tmp_path / "secret.pdf"
    d = pymupdf.open()
    d.new_page()
    d.save(str(src), encryption=pymupdf.PDF_ENCRYPT_AES_256, user_pw="abc", owner_pw="def")
    d.close()
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and "mot de passe" in res.message


def test_collecter_pdf(tmp_path):
    (tmp_path / "d" / "sous").mkdir(parents=True)
    for n in ("Rapport 10.pdf", "Rapport 2.pdf", "[a]- vieux.pdf", "note.txt"):
        (tmp_path / "d" / n).write_bytes(b"x")
    (tmp_path / "d" / "sous" / "z.PDF").write_bytes(b"x")
    pdfs, ignores = core.collecter_pdf([tmp_path / "d", tmp_path / "d" / "Rapport 2.pdf",
                                        tmp_path / "d" / "note.txt", tmp_path / "absent.pdf"])
    assert [p.name for p in pdfs] == ["Rapport 2.pdf", "Rapport 10.pdf", "z.PDF"]   # ordre naturel, sans doublon
    assert {p.name for p, _ in ignores} == {"note.txt", "absent.pdf"}


def test_dossier_de_sortie_exclu(tmp_path):
    (tmp_path / "out").mkdir()
    (tmp_path / "out" / "a.pdf").write_bytes(b"x")
    (tmp_path / "b.pdf").write_bytes(b"x")
    pdfs, _ = core.collecter_pdf([tmp_path], exclure=tmp_path / "out")
    assert [p.name for p in pdfs] == ["b.pdf"]


def test_ecrasement_et_pas_de_fichier_temporaire_restant(tmp_path):
    src = tmp_path / "e.pdf"
    _pdf_avec_elements(src)
    out = tmp_path / "out"
    assert core.aplatir_fichier(src, out).statut == "ok"
    assert core.aplatir_fichier(src, out).statut == "ok"          # 2e passage : remplace
    assert sorted(p.name for p in out.iterdir()) == ["[a]- e.pdf"]
