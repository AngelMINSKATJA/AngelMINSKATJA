"""Robustesse : système de fichiers (noms exotiques, dossiers de sortie, doublons, collecte).
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant ROB-n du rapport).
"""
import errno
import os
import pathlib

import pymupdf
import pytest

import aplatir_core as core


def _pdf(chemin, texte="x", tampon=True):
    chemin = pathlib.Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), texte)
    if tampon:
        p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


def _texte(chemin):
    d = pymupdf.open(str(chemin))
    try:
        return d[0].get_text()
    finally:
        d.close()


def _traiter_lot(chemins, sortie):
    """Ce que fait la fenêtre : collecte puis traitement fichier par fichier."""
    pdfs, ignores = core.collecter_pdf(chemins)
    reserves: dict = {}          # comme l'application : un nom de sortie n'est attribué qu'à une source
    return pdfs, ignores, [core.aplatir_fichier(p, sortie, dst=core.chemin_sortie_unique(p, sortie, reserves))
                           for p in pdfs]


# --------------------------------------------------------------------------- #
# ROB-1 : deux fichiers de même nom (dossiers différents) -> le second écrase le premier, sans alerte
# --------------------------------------------------------------------------- #
def test_deux_sources_de_meme_nom_ne_s_ecrasent_pas(tmp_path):
    _pdf(tmp_path / "OF1" / "Rapport.pdf", "CONTENU OF1 signe par A")
    _pdf(tmp_path / "OF2" / "Rapport.pdf", "CONTENU OF2 signe par B")
    sortie = tmp_path / "sortie"
    pdfs, _, resultats = _traiter_lot([tmp_path / "OF1", tmp_path / "OF2"], sortie)
    assert len(pdfs) == 2 and [r.statut for r in resultats] == ["ok", "ok"]
    textes = sorted(_texte(p) for p in sortie.glob("*.pdf"))
    assert len(textes) == 2, [p.name for p in sortie.iterdir()]
    assert "OF1" in textes[0] and "OF2" in textes[1]
    assert resultats[0].dst != resultats[1].dst


def test_deux_sources_de_meme_nom_sans_element_ne_s_ecrasent_pas(tmp_path):
    _pdf(tmp_path / "A" / "Scan.pdf", "PREMIER", tampon=False)
    _pdf(tmp_path / "B" / "Scan.pdf", "SECOND", tampon=False)
    sortie = tmp_path / "sortie"
    _traiter_lot([tmp_path / "A", tmp_path / "B"], sortie)
    assert len(list(sortie.glob("*.pdf"))) == 2


def test_relancer_la_meme_source_remplace(tmp_path):
    src = _pdf(tmp_path / "in" / "a.pdf", "V1")
    assert core.aplatir_fichier(src, tmp_path / "out").statut == "ok"
    _pdf(src, "V2")
    assert core.aplatir_fichier(src, tmp_path / "out").statut == "ok"
    assert [p.name for p in (tmp_path / "out").iterdir()] == ["[a]- a.pdf"]
    assert "V2" in _texte(tmp_path / "out" / "[a]- a.pdf")


# --------------------------------------------------------------------------- #
# Noms de fichiers
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("nom", [
    "Rapport éàü ñ 日本語.pdf", "emoji 😀📄.pdf", "  espaces  .pdf", "a.b.c.pdf", "x .pdf", "MAJ.PDF", "mixte.PdF",
    "[a]-.pdf", "[a] - x.pdf", "[A]- majuscule.pdf", "100% #1 & co (v2) ; test.pdf",
])
def test_noms_exotiques(tmp_path, nom):
    src = _pdf(tmp_path / "in" / nom)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "ok", res.message
    assert res.dst.name == "[a]- " + nom and res.dst.exists()
    assert [p.name for p in (tmp_path / "out").iterdir()] == ["[a]- " + nom]


def test_nom_trop_long_donne_une_erreur_propre(tmp_path):
    nom = "a" * 251 + ".pdf"              # 5 octets de plus avec le préfixe : dépasse 255 sur la plupart des systèmes
    try:
        src = _pdf(tmp_path / "in" / nom)
    except OSError:
        pytest.skip("le système refuse déjà un nom de 255 caractères")
    res = core.aplatir_fichier(src, tmp_path / "out")
    if res.statut == "ok":
        pytest.skip("ce système accepte des noms de 256 caractères")
    assert res.statut == "erreur" and res.message
    assert not list((tmp_path / "out").glob(".aplatir-*")) if (tmp_path / "out").exists() else True


# --------------------------------------------------------------------------- #
# Dossier de sortie
# --------------------------------------------------------------------------- #
def test_dossier_de_sortie_cree_avec_parents(tmp_path):
    src = _pdf(tmp_path / "a.pdf")
    res = core.aplatir_fichier(src, tmp_path / "x" / "y" / "z")
    assert res.statut == "ok" and res.dst.exists()


def test_dossier_de_sortie_est_un_fichier(tmp_path):
    src = _pdf(tmp_path / "a.pdf")
    (tmp_path / "fichier").write_text("je suis un fichier")
    res = core.aplatir_fichier(src, tmp_path / "fichier")
    assert res.statut == "erreur" and res.message
    assert (tmp_path / "fichier").read_text() == "je suis un fichier"
    res = core.aplatir_fichier(src, tmp_path / "fichier" / "sous")
    assert res.statut == "erreur" and res.message


def test_destination_est_un_dossier(tmp_path):
    src = _pdf(tmp_path / "a.pdf")
    (tmp_path / "out" / "[a]- a.pdf").mkdir(parents=True)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur" and res.message
    assert not list((tmp_path / "out").glob(".aplatir-*"))


def test_sortie_dans_le_dossier_de_la_source(tmp_path):
    src = _pdf(tmp_path / "in" / "a.pdf")
    res = core.aplatir_fichier(src, src.parent)
    assert res.statut == "ok" and sorted(p.name for p in src.parent.iterdir()) == ["[a]- a.pdf", "a.pdf"]
    assert core.aplatir_fichier(res.dst, src.parent).statut == "ignore"


@pytest.mark.skipif(os.name != "posix" or (hasattr(os, "geteuid") and os.geteuid() == 0),
                    reason="droits POSIX non contraignants (Windows ou root)")
def test_dossier_de_sortie_en_lecture_seule(tmp_path):
    src = _pdf(tmp_path / "a.pdf")
    sortie = tmp_path / "ro"
    sortie.mkdir()
    sortie.chmod(0o555)
    try:
        for cible in (sortie, sortie / "sous"):
            res = core.aplatir_fichier(src, cible)
            assert res.statut == "erreur" and ("accès refusé" in res.message
                                               or "dossier de sortie inaccessible" in res.message), res.message
        assert list(sortie.iterdir()) == []
    finally:
        sortie.chmod(0o755)


@pytest.mark.skipif(os.name != "posix" or (hasattr(os, "geteuid") and os.geteuid() == 0),
                    reason="droits POSIX non contraignants (Windows ou root)")
def test_source_dans_un_dossier_en_lecture_seule(tmp_path):
    src = _pdf(tmp_path / "in" / "a.pdf")
    src.parent.chmod(0o555)
    try:
        res = core.aplatir_fichier(src, tmp_path / "out")
        assert res.statut == "ok", res.message
    finally:
        src.parent.chmod(0o755)


# --------------------------------------------------------------------------- #
# collecter_pdf
# --------------------------------------------------------------------------- #
def test_collecter_doublons_et_formes_de_chemins(tmp_path, monkeypatch):
    s = _pdf(tmp_path / "d" / "a.pdf")
    monkeypatch.chdir(tmp_path / "d")
    pdfs, ignores = core.collecter_pdf(["a.pdf", str(s), "./a.pdf", "../d/a.pdf", tmp_path / "d"])
    assert [p.name for p in pdfs] == ["a.pdf"] and ignores == []


def test_collecter_lien_symbolique_et_cible_dedoublonnes(tmp_path):
    s = _pdf(tmp_path / "d" / "a.pdf")
    try:
        os.symlink(s, tmp_path / "lien.pdf")
    except (OSError, NotImplementedError):
        pytest.skip("liens symboliques non disponibles")
    pdfs, _ = core.collecter_pdf([tmp_path / "lien.pdf", s])
    assert len(pdfs) == 1
    res = core.aplatir_fichier(tmp_path / "lien.pdf", tmp_path / "out")
    assert res.statut == "ok" and res.dst.name == "[a]- lien.pdf"


def test_collecter_lien_brise_et_dossier_nomme_pdf(tmp_path):
    try:
        os.symlink(tmp_path / "absent.pdf", tmp_path / "brise.pdf")
    except (OSError, NotImplementedError):
        pytest.skip("liens symboliques non disponibles")
    pdfs, ignores = core.collecter_pdf([tmp_path / "brise.pdf"])
    assert pdfs == [] and [r for _, r in ignores] == ["fichier introuvable"]
    _pdf(tmp_path / "dossier.pdf" / "interieur.pdf")
    pdfs, _ = core.collecter_pdf([tmp_path / "dossier.pdf"])
    assert [p.name for p in pdfs] == ["interieur.pdf"]


def test_collecter_extensions_et_prefixe(tmp_path):
    d = tmp_path / "junk"
    d.mkdir()
    for nom in ("a.pdf.txt", "b.pdf~", "c.PDF", "d.pdf.bak", "e", ".pdf", "[a]- f.pdf"):
        (d / nom).write_bytes(b"x")
    pdfs, _ = core.collecter_pdf([d])
    assert [p.name for p in pdfs] == ["c.PDF"]
    pdfs, ignores = core.collecter_pdf(sorted(d.iterdir()))
    assert {p.name for p in pdfs} == {"c.PDF", "[a]- f.pdf"}          # un fichier déposé explicitement est gardé…
    assert core.aplatir_fichier(d / "[a]- f.pdf", tmp_path / "out").statut == "ignore"   # …puis ignoré par le traitement
    assert {p.name for p, _ in ignores} == {"a.pdf.txt", "b.pdf~", "d.pdf.bak", "e", ".pdf"}


def test_collecter_ordre_naturel_et_sous_dossiers(tmp_path):
    for nom in ("Rapport 10.pdf", "Rapport 2.pdf", "rapport 1.pdf", "sous/B 2.pdf", "sous/B 10.pdf", "sous/a.pdf"):
        _pdf(tmp_path / "d" / nom, tampon=False)
    pdfs, _ = core.collecter_pdf([tmp_path / "d"])
    assert [str(p.relative_to(tmp_path / "d")).replace("\\", "/") for p in pdfs] == [
        "rapport 1.pdf", "Rapport 2.pdf", "Rapport 10.pdf", "sous/a.pdf", "sous/B 2.pdf", "sous/B 10.pdf"]


def test_collecter_exclut_le_sous_dossier_de_sortie(tmp_path):
    _pdf(tmp_path / "d" / "a.pdf", tampon=False)
    _pdf(tmp_path / "d" / "sortie" / "[a]- b.pdf", tampon=False)   # une sortie de l'outil
    pdfs, _ = core.collecter_pdf([tmp_path / "d"])
    assert [p.name for p in pdfs] == ["a.pdf"]


def test_dossier_depose_egal_au_dossier_de_sortie(tmp_path):
    _pdf(tmp_path / "d" / "a.pdf", tampon=False)
    _pdf(tmp_path / "d" / "[a]- deja.pdf", tampon=False)
    pdfs, ignores = core.collecter_pdf([tmp_path / "d"])
    assert [p.name for p in pdfs] == ["a.pdf"]


@pytest.mark.parametrize("nom", ["a1²2.pdf", "1²1.pdf", "pièce 3³4.pdf"])
def test_collecter_nom_avec_exposant_entre_chiffres(tmp_path, nom):
    (tmp_path / nom).write_bytes(b"x")
    (tmp_path / "b.pdf").write_bytes(b"x")
    pdfs, _ = core.collecter_pdf([tmp_path])
    assert len(pdfs) == 2


def test_collecter_erreur_systeme_sur_un_fichier(tmp_path, monkeypatch):
    _pdf(tmp_path / "d" / "a.pdf", tampon=False)
    _pdf(tmp_path / "d" / "illisible.pdf", tampon=False)
    reel = pathlib.Path.is_file

    def is_file(self, *a, **k):
        if self.name == "illisible.pdf":
            raise PermissionError(errno.EACCES, "Accès refusé")
        return reel(self, *a, **k)

    monkeypatch.setattr(pathlib.Path, "is_file", is_file)
    pdfs, ignores = core.collecter_pdf([tmp_path / "d"])      # ne doit pas lever
    assert "a.pdf" in [p.name for p in pdfs]


# --------------------------------------------------------------------------- #
# ROB-8 : messages d'erreur trompeurs ou techniques
# --------------------------------------------------------------------------- #
def test_message_dossier_de_sortie_injoignable(tmp_path, monkeypatch):
    src = _pdf(tmp_path / "a.pdf")

    def mkdir(self, *a, **k):
        raise FileNotFoundError(errno.ENOENT, "Le chemin d'accès spécifié est introuvable")

    monkeypatch.setattr(pathlib.Path, "mkdir", mkdir)
    res = core.aplatir_fichier(src, tmp_path / "Z_absent" / "Rapports")
    assert res.statut == "erreur"
    assert "fichier introuvable" not in res.message, res.message


def test_message_disque_plein_chemin_bake(tmp_path, monkeypatch):
    src = _pdf(tmp_path / "a.pdf")

    def save_plein(self, *a, **k):
        raise pymupdf.mupdf.FzErrorSystem("code=2: cannot fwrite: No space left on device")

    monkeypatch.setattr(pymupdf.Document, "save", save_plein)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur"
    assert "FzError" not in res.message and "code=" not in res.message, res.message


@pytest.mark.skipif(os.name != "posix" or (hasattr(os, "geteuid") and os.geteuid() == 0),
                    reason="droits POSIX non contraignants (Windows ou root)")
def test_message_source_illisible(tmp_path):
    src = _pdf(tmp_path / "a.pdf")
    src.chmod(0o000)
    try:
        res = core.aplatir_fichier(src, tmp_path / "out")
    finally:
        src.chmod(0o644)
    assert res.statut == "erreur"
    assert "corrompu" not in res.message and "illisible" not in res.message, res.message


def test_chemin_sortie_unique_attribue_un_nom_par_source(tmp_path):
    reserves: dict = {}
    a, b = tmp_path / "A" / "Rapport.pdf", tmp_path / "B" / "Rapport.pdf"
    for p in (a, b):
        _pdf(p)
    sortie = tmp_path / "out"
    n1 = core.chemin_sortie_unique(a, sortie, reserves)
    n2 = core.chemin_sortie_unique(b, sortie, reserves)
    n3 = core.chemin_sortie_unique(tmp_path / "C" / "Rapport.pdf", sortie, reserves)
    assert [n.name for n in (n1, n2, n3)] == ["[a]- Rapport.pdf", "[a]- Rapport (2).pdf", "[a]- Rapport (3).pdf"]
    assert core.chemin_sortie_unique(a, sortie, reserves) == n1        # même source : même nom (remplacée)
    assert core.chemin_sortie_unique(b, sortie, reserves) == n2
