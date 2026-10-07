# -*- coding: utf-8 -*-
"""Audit « packaging » : relecture statique du workflow GitHub Actions.

Nécessite PyYAML (absent de requirements-dev : ces tests sont alors ignorés, par exemple sur la CI).
"""
from __future__ import annotations

from pathlib import Path

import pytest

WORKFLOW = Path(__file__).resolve().parents[2] / ".github" / "workflows" / "build-aplatir-pdf.yml"

# Versions majeures dont action.yml déclare « using: node20 » (vérifié dans les dépôts des actions)
NODE20 = {
    "actions/checkout": {"v4"},
    "actions/setup-python": {"v5"},
    "actions/upload-artifact": {"v4", "v5"},
    "actions/download-artifact": {"v4", "v5"},
    "softprops/action-gh-release": {"v2"},
}


@pytest.fixture(scope="module")
def wf():
    yaml = pytest.importorskip("yaml")
    if not WORKFLOW.is_file():
        pytest.skip("workflow absent (arborescence différente)")
    return yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))


def _declencheurs(wf):
    return wf.get("on", wf.get(True))      # PyYAML lit « on » comme le booléen True


def _utilisations(wf):
    for job in wf["jobs"].values():
        for step in job.get("steps", []):
            if "uses" in step:
                yield step["uses"]


def test_declencheurs_et_job_release_coherents(wf):
    d = _declencheurs(wf)
    assert "workflow_dispatch" in d
    assert d["push"]["tags"] == ["aplatir-pdf-v*"]
    cond = wf["jobs"]["release"]["if"]
    assert "refs/tags/aplatir-pdf-v" in cond
    assert wf["jobs"]["release"]["needs"] == "build"
    assert wf["jobs"]["release"]["permissions"] == {"contents": "write"}


def test_artefact_telecharge_est_celui_envoye(wf):
    noms = [s["with"]["name"] for j in wf["jobs"].values() for s in j["steps"]
            if str(s.get("uses", "")).startswith(("actions/upload-artifact", "actions/download-artifact"))]
    assert len(noms) == 2 and len(set(noms)) == 1


@pytest.mark.xfail(strict=True, reason="PKG-3: Start-Process -Wait sans délai : un plantage de l'exe fenêtré "
                                       "ouvre la boîte de dialogue modale de PyInstaller et bloque le job "
                                       "jusqu'au délai par défaut de 360 min")
def test_job_build_a_un_delai_maximal(wf):
    assert "timeout-minutes" in wf["jobs"]["build"]


@pytest.mark.xfail(strict=True, reason="PKG-4: actions ciblant Node 20 (obsolète, avertissement dans le journal CI)")
def test_actions_sans_node20(wf):
    anciennes = []
    for u in _utilisations(wf):
        depot, _, ref = u.partition("@")
        if ref in NODE20.get(depot, ()):
            anciennes.append(u)
    assert not anciennes, anciennes


@pytest.mark.xfail(strict=True, reason="PKG-5: le filtre de branche ne contient que la branche de travail "
                                       "temporaire : après fusion, un push sur main ne fabrique plus d'exe")
def test_push_sur_la_branche_par_defaut_declenche_la_fabrication(wf):
    assert "main" in _declencheurs(wf)["push"]["branches"]
