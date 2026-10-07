"""Robustesse : fichiers cassés / protégés / atypiques, conservation de la structure du document,
écriture atomique (aucun .aplatir-*.tmp ne doit rester) et repli image sur beaucoup de pages.
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant ROB-n du rapport).
"""
import os
import random
import shutil

import pymupdf
import pytest

import aplatir_core as core


def _forcer_repli(monkeypatch, pages=None):
    monkeypatch.setattr(core, "_pages_alterees",
                        lambda orig, doc, numeros, zones=None: list(numeros) if pages is None else list(pages))


def _doc(pages=3, tampon_sur=(0, 1, 2)):
    d = pymupdf.open()
    for i in range(pages):
        p = d.new_page()
        p.insert_text((72, 72), f"Page {i + 1} contenu important")
        if i in tampon_sur:
            p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    return d


def _ecrire(d, chemin, **kw):
    d.save(str(chemin), **kw)
    d.close()
    return chemin


def _restes(dossier):
    return sorted(p.name for p in dossier.glob(".aplatir-*")) if dossier.exists() else []


# --------------------------------------------------------------------------- #
# Fichiers invalides : erreur propre, rien d'écrit, aucun temporaire
# --------------------------------------------------------------------------- #
def _pdf_zero_page():
    return (b"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n"
            b"2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\n"
            b"trailer<</Root 1 0 R/Size 3>>\n%%EOF\n")


@pytest.mark.parametrize("nom,contenu,attendu", [
    ("vide", b"", "vide"),
    ("texte", b"ceci n'est pas un pdf" * 100, "illisible"),
    ("entete_seul", b"%PDF-1.7\n", "illisible"),
    ("zero_page", _pdf_zero_page(), "aucune page"),
])
def test_fichier_invalide(tmp_path, nom, contenu, attendu):
    src = tmp_path / f"{nom}.pdf"
    src.write_bytes(contenu)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and attendu in res.message, res.message
    assert list((tmp_path / "out").glob("*")) == [] if (tmp_path / "out").exists() else True


def test_image_renommee_en_pdf(tmp_path):
    pix = pymupdf.Pixmap(pymupdf.csRGB, pymupdf.IRect(0, 0, 20, 20), False)
    pix.clear_with(200)
    src = tmp_path / "scan.pdf"
    src.write_bytes(pix.tobytes("png"))
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and res.message


@pytest.mark.parametrize("enc", ["AES_256", "AES_128", "RC4_128", "RC4_40"])
def test_mot_de_passe_utilisateur_toutes_methodes(tmp_path, enc):
    src = _ecrire(_doc(1), tmp_path / "secret.pdf", encryption=getattr(pymupdf, "PDF_ENCRYPT_" + enc),
                  user_pw="abc", owner_pw="def")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and "mot de passe" in res.message
    assert not (tmp_path / "out").exists() or list((tmp_path / "out").iterdir()) == []


@pytest.mark.parametrize("enc", ["AES_256", "AES_128", "RC4_128", "RC4_40"])
def test_mot_de_passe_proprietaire_seul_chemin_bake(tmp_path, enc):
    """Ouvrable sans mot de passe : traité, et le résultat n'est plus chiffré (fusionnable)."""
    src = _ecrire(_doc(2), tmp_path / "proprio.pdf", encryption=getattr(pymupdf, "PDF_ENCRYPT_" + enc),
                  user_pw="", owner_pw="def", permissions=pymupdf.PDF_PERM_PRINT)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    out = pymupdf.open(str(res.dst))
    try:
        assert not out.is_encrypted and out.page_count == 2
        assert core.analyser(out).pages == {}
        assert out.permissions & pymupdf.PDF_PERM_ASSEMBLE
    finally:
        out.close()


@pytest.mark.xfail(strict=True, reason="ROB-7: chemin « copie » : un PDF à mot de passe propriétaire seul est copié "
                                       "tel quel, restrictions (assemblage interdit) comprises, alors que le chemin "
                                       "bake produit un fichier sans restriction")
def test_mot_de_passe_proprietaire_seul_chemin_copie(tmp_path):
    src = _ecrire(_doc(1, tampon_sur=()), tmp_path / "proprio.pdf", encryption=pymupdf.PDF_ENCRYPT_AES_256,
                  user_pw="", owner_pw="def", permissions=pymupdf.PDF_PERM_PRINT)
    res = core.aplatir_fichier(src, tmp_path / "out")
    out = pymupdf.open(str(res.dst))
    try:
        assert out.permissions & pymupdf.PDF_PERM_ASSEMBLE or res.statut == "alerte", (res.statut, res.message)
    finally:
        out.close()


# --------------------------------------------------------------------------- #
# Fichiers réparables / atypiques : traités, résultat lisible, même nombre de pages
# --------------------------------------------------------------------------- #
def _variantes_reparables(brut):
    i = brut.rfind(b"startxref")
    return {
        "prefixe_poubelle": b"JUNK\n" * 40 + brut,
        "startxref_casse": brut.replace(b"startxref", b"startxreg"),
        "offset_xref_faux": brut[:i] + b"startxref\n99999\n%%EOF\n",
        "sans_eof": brut[:-6],
    }


@pytest.mark.parametrize("variante", ["prefixe_poubelle", "startxref_casse", "offset_xref_faux", "sans_eof"])
def test_pdf_reparable(tmp_path, variante):
    d = _doc(3)
    d.set_toc([[1, "A", 1], [1, "B", 3]])
    brut = _variantes_reparables(d.tobytes(garbage=0, deflate=False))[variante]
    src = tmp_path / "r.pdf"
    src.write_bytes(brut)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut in ("ok", "alerte"), res.message
    out = pymupdf.open(str(res.dst))
    try:
        assert out.page_count == 3
        assert core.analyser(out).pages == {}
        assert [t[1] for t in out.get_toc()] == ["A", "B"]
    finally:
        out.close()


@pytest.mark.parametrize("option", [{"use_objstms": True}, {"garbage": 4, "deflate": True}, {"clean": True}])
def test_variantes_de_structure(tmp_path, option):
    src = _ecrire(_doc(3), tmp_path / "v.pdf", **option)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message


def test_mutations_aleatoires_ne_plantent_jamais(tmp_path):
    """aplatir_fichier ne lève jamais, ne laisse aucun temporaire, et ne renvoie que des statuts connus."""
    d = _doc(3)
    for p in d:
        p.add_freetext_annot(pymupdf.Rect(72, 300, 280, 330), "Bon pour accord", fontsize=12)
        w = pymupdf.Widget()
        w.field_type, w.field_name, w.rect = pymupdf.PDF_WIDGET_TYPE_SIGNATURE, f"sig{p.number}", pymupdf.Rect(100, 600, 300, 650)
        p.add_widget(w)
    d.set_toc([[1, "A", 1], [1, "B", 3]])
    graine = d.tobytes(garbage=0, deflate=False)
    alea = random.Random(12345)
    out = tmp_path / "out"
    for k in range(40):
        b = bytearray(graine)
        mode = alea.choice(["flip", "del", "dup", "zero", "trunc"])
        a = alea.randrange(len(b) - 400)
        if mode == "flip":
            for _ in range(alea.randint(1, 20)):
                b[alea.randrange(len(b))] = alea.randrange(256)
        elif mode == "del":
            del b[a:a + alea.randint(1, 300)]
        elif mode == "dup":
            n = alea.randint(1, 300)
            b[a:a] = b[a:a + n]
        elif mode == "zero":
            n = alea.randint(1, 300)
            b[a:a + n] = bytes(n)
        else:
            b = b[:alea.randrange(len(b) // 3, len(b))]
        src = tmp_path / f"m{k}.pdf"
        src.write_bytes(bytes(b))
        res = core.aplatir_fichier(src, out)
        assert res.statut in ("ok", "copie", "securite", "alerte", "erreur"), (k, mode, res.statut)
        assert res.message
        assert _restes(out) == [], (k, mode)


# --------------------------------------------------------------------------- #
# ROB-5 : fichier source endommagé (réparé par MuPDF) traité comme un fichier sain
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="ROB-5: un PDF tronqué/corrompu réparé par MuPDF (Document.is_repaired) perd "
                                       "des signatures/annotations mais le statut reste « ok »/« securite »")
def test_source_tronquee_reparee_est_signalee(tmp_path):
    base = tmp_path / "base.pdf"
    d = _doc(6, tampon_sur=())
    d.save(str(base), garbage=0, deflate=False)       # révision 1, sans tampon
    d.close()
    avant = len(base.read_bytes())
    d = pymupdf.open(str(base))
    for p in d:
        p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)   # « signatures » ajoutées en mise à jour incrémentale
    d.saveIncr()
    d.close()
    complet = base.read_bytes()
    src = tmp_path / "coupe.pdf"
    src.write_bytes(complet[:avant + 1500])           # copie interrompue au milieu de la mise à jour
    t = pymupdf.open(str(src))
    reparee, annots = t.is_repaired, sum(len(list(p.annots())) for p in t)
    t.close()
    if not reparee:
        pytest.skip("cette version de MuPDF ne signale pas la réparation")
    assert annots < 6                                  # des tampons ont bien disparu de la source
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "alerte", (res.statut, res.message)


# --------------------------------------------------------------------------- #
# Structure du document conservée sur le chemin normal (signets, pièces jointes, destinations nommées…)
# --------------------------------------------------------------------------- #
def _doc_riche(chemin):
    d = _doc(4, tampon_sur=(0, 2))
    d.embfile_add("piece.txt", b"contenu joint", filename="piece.txt")
    d.set_metadata({"title": "Mon rapport", "author": "Moi"})
    d.set_page_labels([{"startpage": 0, "prefix": "A-", "style": "D", "firstpagenum": 1}])
    pages = [d[i].xref for i in range(4)]
    dests = d.get_new_xref()
    d.update_object(dests, "<</Names[" + "".join(f"(d{i + 1}) [{x} 0 R/Fit]" for i, x in enumerate(pages)) + "]>>")
    d.xref_set_key(d.pdf_catalog(), "Names", f"<</Dests {dests} 0 R>>")
    return _ecrire(d, chemin)


def _structure(chemin):
    d = pymupdf.open(str(chemin))
    try:
        return {
            "pages": d.page_count,
            "emb": d.embfile_names(),
            "meta": {k: v for k, v in d.metadata.items() if k in ("title", "author")},
            "labels": d.get_page_labels(),
            "noms": d.resolve_names(),
            "toc": [(t[0], t[1], t[2]) for t in d.get_toc()],
        }
    finally:
        d.close()


def test_structure_conservee_chemin_normal(tmp_path):
    src = _doc_riche(tmp_path / "riche.pdf")
    d = pymupdf.open(str(src))
    d.set_toc([[1, "Ch1", 1], [2, "Sec", 2], [1, "Ch2", 3], [1, "Ch3", 4]])
    d.saveIncr()
    d.close()
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok" and res.pages_elements == [1, 3], res.message
    assert _structure(src) == _structure(res.dst)


def _doc_signets_nommes(chemin):
    """3 pages, tampon page 2 ; signets et liens qui visent des destinations NOMMÉES (cas Word/LaTeX)."""
    d = _doc(3, tampon_sur=(1,))
    pages = [d[i].xref for i in range(3)]
    dests = d.get_new_xref()
    d.update_object(dests, "<</Names[" + "".join(f"(d{i + 1}) [{x} 0 R/Fit]" for i, x in enumerate(pages)) + "]>>")
    d.xref_set_key(d.pdf_catalog(), "Names", f"<</Dests {dests} 0 R>>")
    out = d.get_new_xref()
    items = [d.get_new_xref() for _ in range(2)]
    d.update_object(out, f"<</Type/Outlines/First {items[0]} 0 R/Last {items[1]} 0 R/Count 2>>")
    d.update_object(items[0], f"<</Title(Vers page 3 (nommee))/Dest(d3)/Parent {out} 0 R/Next {items[1]} 0 R>>")
    d.update_object(items[1], f"<</Title(Vers page 2 (nommee))/Dest(d2)/Parent {out} 0 R/Prev {items[0]} 0 R>>")
    d.xref_set_key(d.pdf_catalog(), "Outlines", f"{out} 0 R")
    return _ecrire(d, chemin)


def test_signets_nommes_chemin_normal(tmp_path):
    src = _doc_signets_nommes(tmp_path / "nommes.pdf")
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    out = pymupdf.open(str(res.dst))
    try:
        assert [(t[1], t[2]) for t in out.get_toc()] == [("Vers page 3 (nommee)", 3), ("Vers page 2 (nommee)", 2)]
        assert out.resolve_names()["d2"]["page"] == 1
    finally:
        out.close()


@pytest.mark.xfail(strict=True, reason="ROB-4: repli image -> get_toc/set_toc transforme les signets à destination "
                                       "NOMMÉE en signets morts (même vers des pages non remplacées) et la "
                                       "destination nommée de la page remplacée pointe dans le vide")
def test_signets_nommes_apres_repli_image(tmp_path, monkeypatch):
    src = _doc_signets_nommes(tmp_path / "nommes.pdf")
    _forcer_repli(monkeypatch)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "securite" and res.pages_image == [2], res.message
    out = pymupdf.open(str(res.dst))
    try:
        assert [(t[1], t[2]) for t in out.get_toc()] == [("Vers page 3 (nommee)", 3), ("Vers page 2 (nommee)", 2)]
        assert out.resolve_names()["d2"]["page"] == 1
        assert out.resolve_names()["d3"]["page"] == 2
    finally:
        out.close()


def test_toc_et_pieces_jointes_apres_repli_image(tmp_path, monkeypatch):
    src = _doc_riche(tmp_path / "riche.pdf")
    d = pymupdf.open(str(src))
    d.set_toc([[1, "Ch1", 1], [2, "Sec", 2], [1, "Ch2", 3], [1, "Ch3", 4]])
    d.saveIncr()
    d.close()
    _forcer_repli(monkeypatch)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "securite" and res.pages_image == [1, 3], res.message
    a, b = _structure(src), _structure(res.dst)
    for cle in ("pages", "emb", "meta", "labels", "toc"):
        assert a[cle] == b[cle], cle


# --------------------------------------------------------------------------- #
# ROB-2 : repli image -> chaque page convertie reste en mémoire NON compressée jusqu'à l'enregistrement
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="ROB-2: insert_image(pixmap=...) garde 3 octets/pixel par page convertie "
                                       "(≈ 11,6 Mo par page A4 à 200 dpi : 500 pages -> 5,6 Go de RAM) ; "
                                       "il faut ajouter l'image déjà compressée (Flate)")
def test_pages_converties_stockees_compressees_en_memoire():
    d = _doc(3)
    orig = pymupdf.open("pdf", d.tobytes())
    core._convertir_en_images(d, orig, [1, 2, 3], core.DPI_RASTER)
    try:
        for p in d:
            for img in p.get_images(full=True):
                xref = img[0]
                brut = len(d.xref_stream_raw(xref))
                w, h = int(d.xref_get_key(xref, "Width")[1]), int(d.xref_get_key(xref, "Height")[1])
                assert brut < w * h * 3 // 4, (brut, w * h * 3)    # page quasi blanche : doit tenir dans une fraction
    finally:
        orig.close()
        d.close()


# --------------------------------------------------------------------------- #
# Écriture atomique : aucun temporaire sur les chemins d'échec
# --------------------------------------------------------------------------- #
def test_echec_du_remplacement_pas_de_temporaire(tmp_path, monkeypatch):
    src = _ecrire(_doc(1), tmp_path / "a.pdf")

    def refuse(*a, **k):
        raise PermissionError(13, "Access is denied")

    monkeypatch.setattr(core.os, "replace", refuse)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and "accès refusé" in res.message
    assert _restes(tmp_path / "out") == [] and not (tmp_path / "out" / "[a]- a.pdf").exists()


def test_echec_de_l_enregistrement_pas_de_temporaire(tmp_path, monkeypatch):
    src = _ecrire(_doc(1), tmp_path / "a.pdf")

    def ecriture_partielle(tmp):
        with open(tmp, "wb") as f:
            f.write(b"%PDF-partiel")
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(core, "_enregistrer", lambda doc, dst: core._ecrire_atomique(dst, ecriture_partielle))
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and "disque" in res.message
    assert _restes(tmp_path / "out") == [] and not (tmp_path / "out" / "[a]- a.pdf").exists()


def test_echec_de_la_copie_pas_de_temporaire(tmp_path, monkeypatch):
    src = _ecrire(_doc(1, tampon_sur=()), tmp_path / "a.pdf")

    def copie_ratee(s, t):
        with open(t, "wb") as f:
            f.write(b"%PDF-partiel")
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(core.shutil, "copyfile", copie_ratee)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur"
    assert _restes(tmp_path / "out") == [] and not (tmp_path / "out" / "[a]- a.pdf").exists()


def test_source_qui_disparait_pendant_le_traitement(tmp_path, monkeypatch):
    src = _ecrire(_doc(1, tampon_sur=()), tmp_path / "a.pdf")
    reel = core._ecrire_atomique

    def apres_suppression(dst, ecrire):
        os.remove(src)
        return reel(dst, ecrire)

    monkeypatch.setattr(core, "_ecrire_atomique", apres_suppression)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and "introuvable" in res.message
    assert _restes(tmp_path / "out") == [] and not (tmp_path / "out" / "[a]- a.pdf").exists()


def test_ancien_resultat_intact_si_le_nouveau_echoue(tmp_path, monkeypatch):
    src = _ecrire(_doc(1), tmp_path / "a.pdf")
    out = tmp_path / "out"
    assert core.aplatir_fichier(src, out).statut == "ok"
    avant = (out / "[a]- a.pdf").read_bytes()

    def echec(doc, dst):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(core, "_enregistrer", echec)
    assert core.aplatir_fichier(src, out).statut == "erreur"
    assert (out / "[a]- a.pdf").read_bytes() == avant
    assert _restes(out) == []


def test_sortie_deja_traitee_et_source_en_lecture_seule(tmp_path):
    """Le dossier d'origine n'est jamais modifié ; relancer remplace proprement."""
    dossier = tmp_path / "in"
    dossier.mkdir()
    src = _ecrire(_doc(1), dossier / "a.pdf")
    avant = {p.name: p.read_bytes() for p in dossier.iterdir()}
    for _ in range(2):
        assert core.aplatir_fichier(src, tmp_path / "out").statut == "ok"
    assert {p.name: p.read_bytes() for p in dossier.iterdir()} == avant
    assert sorted(p.name for p in (tmp_path / "out").iterdir()) == ["[a]- a.pdf"]


@pytest.mark.skipif(shutil.which("qpdf") is None, reason="qpdf absent")
def test_sortie_valide_pour_qpdf_apres_repli_image(tmp_path, monkeypatch):
    import subprocess
    src = _ecrire(_doc(3), tmp_path / "a.pdf")
    _forcer_repli(monkeypatch)
    res = core.aplatir_fichier(src, tmp_path / "out")
    r = subprocess.run(["qpdf", "--check", str(res.dst)], capture_output=True, text=True)
    assert r.returncode == 0 and "WARNING" not in r.stdout + r.stderr, r.stdout + r.stderr
