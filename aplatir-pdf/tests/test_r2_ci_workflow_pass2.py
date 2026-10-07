# -*- coding: utf-8 -*-
"""2e passe « ci » (suite) : ce que la CI réelle (runs n° 9 et 10 sur Windows et Ubuntu) a montré.

Les vérifications du workflow lisent le YAML comme du texte (pas de PyYAML requis). Les tests
``xfail(strict=True)`` démontrent un vrai défaut (identifiant CI2-n du rapport) : retirer le marqueur
quand c'est corrigé. Les tests réservés à Windows n'ont pas pu être exécutés lors de la rédaction
(pas de Windows dans le bac à sable) : ils se SAUTENT si Windows se comporte autrement que prévu,
plutôt que d'échouer pour une mauvaise raison.
"""
from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

import pymupdf
import pytest

import aplatir_core as core

RACINE = Path(__file__).resolve().parents[1]
WORKFLOW = RACINE.parent / ".github" / "workflows" / "build-aplatir-pdf.yml"
TESTS = RACINE / "tests"


@pytest.fixture(scope="module")
def texte_workflow():
    if not WORKFLOW.is_file():
        pytest.skip("workflow absent (arborescence différente)")
    return WORKFLOW.read_text(encoding="utf-8").replace("\r\n", "\n")


def _job(texte: str, nom: str) -> str:
    m = re.search(rf"(?ms)^  {re.escape(nom)}:\n(.*?)(?=^  [A-Za-z0-9_-]+:\n|\Z)", texte)
    assert m, f"job « {nom} » introuvable"
    return m.group(1)


def _etapes(job: str) -> list[str]:
    return re.split(r"(?m)^      - ", job)[1:]


# --------------------------------------------------------------------------- #
# CI2-1 : le job Linux est le seul à exécuter qpdf / pdfunite / poppler (fusion), or le run n° 10
# (commit 9cb138c) y est resté 20 minutes dans « apt-get update » (miroir azure.archive.ubuntu.com
# muet) puis a été ANNULÉ : tout le run est « cancelled », malgré continue-on-error: true, alors que
# le README demande de prendre « la dernière exécution verte ».
# --------------------------------------------------------------------------- #
def test_etape_apt_get_a_un_delai_et_des_nouvelles_tentatives(texte_workflow):
    job = _job(texte_workflow, "tests-linux")
    apt = [e for e in _etapes(job) if "apt-get" in e]
    assert apt, "aucune étape apt-get"
    for e in apt:
        assert re.search(r"(?m)^\s+timeout-minutes:\s*\d+", e), e
        assert "Acquire::Retries" in e or "Acquire::http::Timeout" in e, e


# --------------------------------------------------------------------------- #
# CI2-2 : les tests d'interface ne sont lancés sous Windows que pour 4 fichiers nommés à la main
# --------------------------------------------------------------------------- #
def _fichiers_gui() -> set[str]:
    garde = re.compile(r'skipif\(not os\.environ\.get\("APLATIR_GUI_TESTS"\)')
    return {p.name for p in TESTS.glob("test_*.py") if garde.search(p.read_text(encoding="utf-8"))}


def test_etape_interface_windows_couvre_tous_les_fichiers_gui(texte_workflow):
    job = _job(texte_workflow, "build")
    etape = [e for e in _etapes(job) if "APLATIR_GUI_TESTS" in e]
    assert etape, "aucune étape d'interface sous Windows"
    nommes = set(re.findall(r"tests/([\w]+\.py)", etape[0]))
    if not nommes:        # pytest sans liste de fichiers : tout est couvert
        return
    manquants = _fichiers_gui() - nommes
    assert not manquants, f"jamais exécutés sous Windows : {sorted(manquants)}"


# --------------------------------------------------------------------------- #
# CI2-3 : accents illisibles dans les journaux pytest de Windows (« r�cente », « d�marrage »)
# --------------------------------------------------------------------------- #
def test_journaux_pytest_en_utf8(texte_workflow):
    job = _job(texte_workflow, "build")
    assert re.search(r"PYTHONIOENCODING|PYTHONUTF8", job) or re.search(r"PYTHONIOENCODING|PYTHONUTF8",
                                                                      texte_workflow.split("jobs:")[0])


# --------------------------------------------------------------------------- #
# CI2-4 : le test actionlint du dépôt n'est exécuté nulle part (ignoré sur Windows ET sur Linux)
# --------------------------------------------------------------------------- #
def test_actionlint_est_installe_dans_le_job_linux(texte_workflow):
    assert "actionlint" in _job(texte_workflow, "tests-linux")


# --------------------------------------------------------------------------- #
# Garde-fous indépendants de root et de Windows pour les messages d'erreur du système de fichiers
# (les tests POSIX équivalents sont SAUTÉS sous root et sous Windows : seul le job Linux non-root les voit,
#  et il y échoue : test_dossier_de_sortie_en_lecture_seule attend « accès refusé » alors que le message
#  est « dossier de sortie inaccessible (...) » ; test_message_source_illisible porte un xfail devenu périmé)
# --------------------------------------------------------------------------- #
def _pdf(chemin: Path) -> Path:
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "x")
    p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


def test_source_illisible_donne_un_message_d_acces_refuse_et_non_pdf_corrompu(tmp_path, monkeypatch):
    """ROB-8 est corrigé : le xfail strict de test_robustness_fs.py::test_message_source_illisible est périmé."""
    src = _pdf(tmp_path / "a.pdf")
    reel = open

    def ouvrir(chemin, *a, **k):
        if str(chemin) == str(src):
            raise PermissionError(13, "Permission denied", str(chemin))
        return reel(chemin, *a, **k)

    monkeypatch.setattr(core, "open", ouvrir, raising=False)
    res = core.aplatir_fichier(src, tmp_path / "out")
    assert res.statut == "erreur"
    assert "accès refusé" in res.message and "corrompu" not in res.message, res.message


def test_dossier_de_sortie_refuse_message_nomme_le_dossier(tmp_path, monkeypatch):
    src = _pdf(tmp_path / "a.pdf")
    cible = tmp_path / "ro" / "sous"

    def mkdir(self, *a, **k):
        raise PermissionError(13, "Permission denied", str(self))

    monkeypatch.setattr(Path, "mkdir", mkdir)
    res = core.aplatir_fichier(src, cible)
    assert res.statut == "erreur"
    assert "dossier de sortie inaccessible" in res.message and str(cible) in res.message, res.message


# --------------------------------------------------------------------------- #
# Vrais verrous Windows (les tests de tests/test_windows_fs.py ne font que SIMULER l'exception d'os.replace)
# --------------------------------------------------------------------------- #
WINDOWS = pytest.mark.skipif(sys.platform != "win32", reason="vrais verrous de fichiers Windows")


def _aucun_temporaire(dossier: Path) -> bool:
    return not [p for p in dossier.iterdir() if p.name.startswith(".aplatir-")]


@WINDOWS
def test_sortie_ouverte_ailleurs_garde_l_ancien_fichier_et_ne_laisse_aucun_temporaire(tmp_path):
    """Acrobat a « [a]- a.pdf » ouvert : le remplacement échoue ; message clair, ancien fichier intact."""
    src = _pdf(tmp_path / "a.pdf")
    sortie = tmp_path / "out"
    sortie.mkdir()
    cible = sortie / "[a]- a.pdf"
    cible.write_bytes(b"ANCIEN")
    with open(cible, "rb"):                     # poignée ouverte sans partage de suppression/renommage
        debut = time.monotonic()
        res = core.aplatir_fichier(src, sortie)
        duree = time.monotonic() - debut
    if res.statut == "ok":
        pytest.skip("cette version de Windows autorise le remplacement d'un fichier ouvert en lecture")
    assert res.statut == "erreur" and "accès refusé" in res.message, res.message
    assert cible.read_bytes() == b"ANCIEN"
    assert _aucun_temporaire(sortie)
    assert duree < 15, f"{duree:.1f} s d'attente avant d'abandonner"


@WINDOWS
def test_sortie_en_lecture_seule_est_signalee_sans_ecraser_ni_laisser_de_temporaire(tmp_path):
    """Un « [a]- a.pdf » marqué lecture seule (fichier validé, copie SharePoint) n'est pas écrasé en silence."""
    import stat

    src = _pdf(tmp_path / "a.pdf")
    sortie = tmp_path / "out"
    sortie.mkdir()
    cible = sortie / "[a]- a.pdf"
    cible.write_bytes(b"ANCIEN")
    os.chmod(cible, stat.S_IREAD)
    try:
        res = core.aplatir_fichier(src, sortie)
        if res.statut == "ok":
            pytest.skip("cette version de Windows remplace un fichier en lecture seule")
        assert res.statut == "erreur" and res.message, res
        assert cible.read_bytes() == b"ANCIEN"
        assert _aucun_temporaire(sortie)
    finally:
        os.chmod(cible, stat.S_IWRITE | stat.S_IREAD)


@WINDOWS
def test_source_verrouillee_en_exclusif_donne_acces_refuse(tmp_path):
    """Fichier source tenu en exclusivité par un autre programme (scanner, synchronisation) : pas « corrompu »."""
    import ctypes
    from ctypes import wintypes

    src = _pdf(tmp_path / "a.pdf")
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateFileW.restype = wintypes.HANDLE
    k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                                wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    GENERIC_READ, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL = 0x80000000, 3, 0x80
    poignee = k32.CreateFileW(str(src), GENERIC_READ, 0, None, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, None)
    if poignee in (None, wintypes.HANDLE(-1).value):
        pytest.skip("verrou exclusif impossible à poser")
    try:
        res = core.aplatir_fichier(src, tmp_path / "out")
    finally:
        k32.CloseHandle(poignee)
    assert res.statut == "erreur"
    assert "corrompu" not in res.message and "illisible" not in res.message, res.message
