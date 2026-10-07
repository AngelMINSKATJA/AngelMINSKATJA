# -*- coding: utf-8 -*-
"""2e passe « ci » : preuves réelles de la CI Windows + packaging (build.py, _version.py, workflow).

Les vérifications du workflow sont faites SANS PyYAML (le fichier est lu comme du texte) pour
qu'elles s'exécutent aussi sur la CI, où PyYAML est absent. Les tests ``xfail(strict=True)``
démontrent un vrai défaut (identifiant CI-n du rapport de la 2e passe).
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

RACINE = Path(__file__).resolve().parents[1]
WORKFLOW = RACINE.parent / ".github" / "workflows" / "build-aplatir-pdf.yml"

LINUX_OU_MAC = pytest.mark.skipif(sys.platform == "win32",
                                  reason="build.py lance l'exe et son auto-test sous Windows")


@pytest.fixture(scope="module")
def texte_workflow():
    if not WORKFLOW.is_file():
        pytest.skip("workflow absent (arborescence différente)")
    return WORKFLOW.read_text(encoding="utf-8").replace("\r\n", "\n")   # CRLF éventuel (autocrlf)


def _etape(texte: str, fragment_nom: str) -> str:
    """Texte de l'étape (« - name: ... » jusqu'à l'étape suivante) dont le nom contient fragment_nom."""
    blocs = re.split(r"(?m)^      - ", texte)
    for b in blocs:
        if re.match(r"name:[^\n]*" + re.escape(fragment_nom), b):
            return b
    raise AssertionError(f"étape « {fragment_nom} » introuvable")


# --------------------------------------------------------------------------- #
# Garde-fous qui passent aujourd'hui
# --------------------------------------------------------------------------- #
FONCTIONS_ACTIONS = {"contains", "startsWith", "endsWith", "format", "join", "toJSON", "fromJSON",
                     "hashFiles", "success", "always", "cancelled", "failure"}


def test_expressions_du_workflow_n_utilisent_que_des_fonctions_existantes(texte_workflow):
    """Le run n° 5 de la CI a été rejeté en bloc (workflow invalide, aucun job) à cause de
    ``substr()`` : cette fonction n'existe pas dans les expressions ``${{ }}``."""
    inconnues = set()
    for expr in re.findall(r"\$\{\{(.*?)\}\}", texte_workflow, flags=re.S):
        for nom in re.findall(r"([A-Za-z_]\w*)\s*\(", expr):
            if nom not in FONCTIONS_ACTIONS:
                inconnues.add(nom)
    assert not inconnues, inconnues


def test_etape_version_ecrit_github_env_en_utf8_sans_bom(texte_workflow):
    """pwsh 7 : « -Encoding utf8 » n'écrit pas de BOM (Windows PowerShell 5.1 en écrirait un et le
    nom de la variable deviendrait « \\ufeffAPLATIR_VERSION »)."""
    e = _etape(texte_workflow, "Numéro de version")
    assert "shell: pwsh" in e
    assert "$env:GITHUB_ENV" in e and "-Append" in e and "-Encoding utf8" in e
    assert "Substring(0, 7)" in e and "github.run_number" in e
    # build.py est lancé APRÈS cette étape (sinon la version retombe sur « local-<date> »)
    assert texte_workflow.index("Numéro de version") < texte_workflow.index("python build.py")


def test_release_seulement_sur_etiquette_et_apres_le_build(texte_workflow):
    m = re.search(r"(?ms)^  release:\n(.*)", texte_workflow)
    assert m, "job release introuvable"
    job = m.group(1)
    assert "startsWith(github.ref, 'refs/tags/aplatir-pdf-v')" in job
    assert re.search(r"(?m)^    needs: build$", job)
    assert re.search(r"(?m)^      contents: write$", job)
    assert re.search(r'tags: \["aplatir-pdf-v\*"\]', texte_workflow)


@pytest.mark.skipif(not shutil.which("actionlint"), reason="actionlint absent (pip install actionlint-py)")
def test_actionlint_sans_erreur(texte_workflow):
    r = subprocess.run(["actionlint", str(WORKFLOW)], capture_output=True, text=True, timeout=60)
    assert r.returncode == 0, r.stdout + r.stderr


# --------------------------------------------------------------------------- #
# build.py -> _version.py -> aplatir_tray.VERSION (PyInstaller remplacé par un faux module)
# --------------------------------------------------------------------------- #
FAUX_PYINSTALLER = '''
import json, os

def run(args):
    with open(os.environ["FAUX_SORTIE"], "w", encoding="utf-8") as f:
        json.dump(args, f)
    if os.environ.get("FAUX_CREER_EXE") == "1":
        os.makedirs("dist", exist_ok=True)
        with open(os.path.join("dist", "AplatirPDF"), "wb") as f:
            f.write(b"x")
'''


def _lancer_build(tmp_path: Path, version: str | None, creer_exe: bool = True):
    ici = tmp_path / "projet"
    ici.mkdir()
    for nom in ("build.py", "aplatir_icon.py"):
        shutil.copy(RACINE / nom, ici / nom)
    faux = tmp_path / "faux" / "PyInstaller"
    faux.mkdir(parents=True)
    (faux / "__init__.py").write_text("", encoding="utf-8")
    (faux / "__main__.py").write_text(FAUX_PYINSTALLER, encoding="utf-8")
    env = {k: v for k, v in os.environ.items() if k != "APLATIR_VERSION"}
    env.update(PYTHONPATH=str(faux.parent), PYTHONIOENCODING="utf-8",
               FAUX_SORTIE=str(tmp_path / "args.json"), FAUX_CREER_EXE="1" if creer_exe else "0")
    if version is not None:
        env["APLATIR_VERSION"] = version
    r = subprocess.run([sys.executable, str(ici / "build.py")], cwd=str(tmp_path), env=env,
                       capture_output=True, text=True, encoding="utf-8", timeout=90)
    return ici, r


@LINUX_OU_MAC
def test_build_ecrit_la_version_de_la_ci_et_appelle_pyinstaller(tmp_path):
    ici, r = _lancer_build(tmp_path, "123-abcdef0")
    assert r.returncode == 0, r.stdout + r.stderr
    assert (ici / "_version.py").read_text(encoding="utf-8") == 'VERSION = "123-abcdef0"\n'
    assert "version 123-abcdef0" in r.stdout
    args = json.loads((tmp_path / "args.json").read_text(encoding="utf-8"))
    assert args[0] == "aplatir_tray.py"
    for drapeau in ("--onefile", "--windowed", "--clean", "--noconfirm"):
        assert drapeau in args
    for paire in (("--collect-all", "tkinterdnd2"), ("--hidden-import", "pystray._win32"),
                  ("--collect-submodules", "pymupdf"), ("--icon", "aplatir.ico")):
        assert any(args[i:i + 2] == list(paire) for i in range(len(args) - 1)), paire
    assert (ici / "aplatir.ico").stat().st_size > 0


@LINUX_OU_MAC
def test_build_sans_variable_utilise_un_numero_local_date(tmp_path):
    ici, r = _lancer_build(tmp_path, None)
    assert r.returncode == 0, r.stdout + r.stderr
    assert re.fullmatch(r'VERSION = "local-\d{8}-\d{4}"\n', (ici / "_version.py").read_text(encoding="utf-8"))


@LINUX_OU_MAC
def test_build_echoue_avec_un_code_non_nul_si_l_exe_n_est_pas_produit(tmp_path):
    _, r = _lancer_build(tmp_path, "1-aaaaaaa", creer_exe=False)
    assert r.returncode == 1
    assert "ÉCHEC" in r.stdout


@LINUX_OU_MAC
def test_la_version_ecrite_par_build_est_celle_lue_par_l_application(tmp_path):
    """Contrat build.py -> aplatir_tray : module « _version », attribut « VERSION »."""
    ici, r = _lancer_build(tmp_path, "7-1234567")
    assert r.returncode == 0, r.stdout + r.stderr
    code = ("import sys; sys.path.insert(0, sys.argv[1]); sys.path.insert(1, sys.argv[2]); "
            "import aplatir_tray as t; print(t.VERSION)")
    p = subprocess.run([sys.executable, "-c", code, str(ici), str(RACINE)], capture_output=True, text=True,
                       encoding="utf-8", timeout=60, env=dict(os.environ, PYTHONIOENCODING="utf-8"))
    assert p.returncode == 0, p.stderr
    assert p.stdout.strip().splitlines()[-1] == "7-1234567"


# --------------------------------------------------------------------------- #
# DÉFAUTS DE LA CI (xfail strict : retirer le marqueur quand c'est corrigé)
# --------------------------------------------------------------------------- #
def test_ci_execute_les_tests_de_controle_croise_et_montre_les_ignores(texte_workflow):
    requis = ((RACINE / "requirements-dev.txt").read_text(encoding="utf-8")
              + (RACINE / "requirements-test.txt").read_text(encoding="utf-8") + texte_workflow)
    for paquet in (r"\bpypdf\b", r"\bpypdfium2\b", r"(?i)\bpyyaml\b"):
        assert re.search(paquet, requis), paquet
    commande = _etape(texte_workflow, "Tests")
    assert re.search(r"pytest[^\n]*\s-r[a-zA-Z]*[sa]", commande), commande


def test_ci_lance_les_tests_d_interface_sous_windows(texte_workflow):
    assert "APLATIR_GUI_TESTS" in texte_workflow


def test_ci_supprime_l_ancien_rapport_avant_l_autotest(texte_workflow):
    e = _etape(texte_workflow, "Auto-test de l'exe")
    assert "Remove-Item" in e and e.index("Remove-Item") < e.index("Start-Process"), e
