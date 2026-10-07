"""Sémantique des fichiers Windows (NTFS, antivirus, OneDrive, MAX_PATH), simulée sous Linux.

On ne peut pas déclencher ces erreurs ici : on fait lever à ``os.replace`` l'exception exacte que Python lève
sous Windows (``PermissionError`` WinError 32, ``FileNotFoundError`` WinError 3) pour vérifier la RÉACTION du coeur.
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant WIN-n du rapport).
"""
import errno
import os
import pathlib
import sys

import pymupdf
import pytest

import aplatir_core as core


def _pdf(chemin, tampon=True):
    chemin = pathlib.Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "x")
    if tampon:
        p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


def _temporaires(dossier):
    return sorted(p.name for p in pathlib.Path(dossier).iterdir() if p.name.startswith(".aplatir-"))


# --------------------------------------------------------------------------- #
# Régression : le remplacement atomique
# --------------------------------------------------------------------------- #
def test_verrou_persistant_message_acrobat_et_aucun_temporaire(tmp_path, monkeypatch):
    """Fichier de sortie ouvert dans Acrobat (verrou qui dure) : message clair, ancien fichier intact, pas de résidu."""
    src = _pdf(tmp_path / "a.pdf")
    out = tmp_path / "out"
    out.mkdir()
    ancien = out / "[a]- a.pdf"
    ancien.write_bytes(b"ANCIEN")

    def verrouille(a, b):
        raise PermissionError(13, "Le processus ne peut pas accéder au fichier car ce fichier est utilisé "
                                  "par un autre processus", None, 32)

    monkeypatch.setattr(core.os, "replace", verrouille)
    res = core.aplatir_fichier(src, out)
    assert res.statut == "erreur"
    assert "Acrobat" in res.message
    assert ancien.read_bytes() == b"ANCIEN"
    assert _temporaires(out) == []


# --------------------------------------------------------------------------- #
# WIN-3 : verrou TRANSITOIRE (antivirus, OneDrive, indexeur) sur le fichier qui vient d'être écrit
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("winerror", [5, 32])
def test_remplacement_survit_a_un_verrou_transitoire(tmp_path, monkeypatch, winerror):
    src = _pdf(tmp_path / "a.pdf")
    out = tmp_path / "out"
    reel = os.replace
    appels = []

    def capricieux(a, b):
        appels.append(1)
        if len(appels) <= 2:
            raise PermissionError(13, "fichier utilisé par un autre processus", None, winerror)
        return reel(a, b)

    monkeypatch.setattr(core.os, "replace", capricieux)
    res = core.aplatir_fichier(src, out)
    assert res.statut == "ok", f"{res.statut} : {res.message}"
    assert (out / "[a]- a.pdf").is_file()
    assert _temporaires(out) == []


# --------------------------------------------------------------------------- #
# WIN-4 : MAX_PATH (260) sans LongPathsEnabled : message trompeur « fichier introuvable »
# --------------------------------------------------------------------------- #
def _dossier_profond(base, longueur):
    """Dossier de sortie de ``longueur`` caractères exactement (composants de 40 caractères)."""
    d = pathlib.Path(base)
    while len(str(d)) + 41 < longueur:
        d = d / ("d" * 40)
    reste = longueur - len(str(d)) - 1
    if reste > 0:
        d = d / ("e" * reste)
    assert len(str(d)) == longueur
    return d


def test_chemin_de_sortie_trop_long_message_clair(tmp_path, monkeypatch):
    nom = "Rapport de fin de fabrication OF 123456 indice B.pdf"           # 52 caractères
    src = _pdf(tmp_path / "entree" / nom)
    # tmp (".aplatir-xxxxxxxx.tmp", 21 car.) tient dans 259 mais pas le nom final
    out = _dossier_profond(tmp_path, 215)
    dst = core.chemin_sortie(src, out)
    assert len(str(dst)) >= 260 > len(str(out)) + 1 + 21

    reel = os.replace
    prefixe = "\\\\?\\"           # préfixe « chemin étendu » de Win32 : \\?\

    def win32_sans_chemins_longs(a, b):
        a, b = os.fspath(a), os.fspath(b)
        for p in (a, b):
            if len(p) >= 260 and not p.startswith(prefixe):
                raise FileNotFoundError(errno.ENOENT, "Le chemin d'accès spécifié est introuvable", p, 3)
        # avec le préfixe, Win32 accepte le chemin : on le retire pour que Linux l'accepte aussi
        return reel(a.removeprefix(prefixe), b.removeprefix(prefixe))

    monkeypatch.setattr(core.os, "replace", win32_sans_chemins_longs)
    monkeypatch.setattr(sys, "platform", "win32")  # active les éventuelles branches « Windows » du correctif
    monkeypatch.setattr(core, "_chemins_longs_actifs", lambda: False)   # Windows par défaut (les runners de CI l'activent)
    res = core.aplatir_fichier(src, out)
    # soit le fichier est écrit (préfixe « \\?\ »), soit l'erreur dit la vraie cause
    if res.statut == "erreur":
        assert "introuvable" not in res.message, res.message
        assert "long" in res.message, res.message
    else:
        assert dst.is_file()
