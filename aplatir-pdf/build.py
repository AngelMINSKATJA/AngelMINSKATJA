# -*- coding: utf-8 -*-
"""Fabrique dist/AplatirPDF.exe avec PyInstaller (à lancer sous Windows).

    python build.py

Le numéro de version affiché dans la fenêtre vient de la variable d'environnement
APLATIR_VERSION (la CI y met « numéro de fabrication-commit »), sinon de la date.
"""
from __future__ import annotations

import os
import subprocess
import sys
import time

import PyInstaller.__main__

ICI = os.path.dirname(os.path.abspath(__file__))
os.chdir(ICI)

import aplatir_icon

EXE = os.path.join(ICI, "dist", "AplatirPDF.exe")

# Un exe qui tourne est verrouillé par Windows : message clair plutôt qu'une erreur PyInstaller obscure.
if sys.platform == "win32" and os.path.exists(EXE):
    try:
        os.remove(EXE)
    except OSError:
        print("\nImpossible de remplacer dist\\AplatirPDF.exe : il est en cours d'utilisation.\n"
              "Fermez Aplatir PDF (icône près de l'horloge, clic droit, Quitter) puis relancez.")
        sys.exit(1)

version = os.environ.get("APLATIR_VERSION") or time.strftime("local-%Y%m%d-%H%M")
with open(os.path.join(ICI, "_version.py"), "w", encoding="utf-8") as f:
    f.write(f'VERSION = "{version}"\n')
aplatir_icon.ecrire_ico("aplatir.ico")

PyInstaller.__main__.run([
    "aplatir_tray.py",
    "--name", "AplatirPDF",
    "--onefile",
    "--windowed",                      # pas de fenêtre console
    "--noconfirm", "--clean",
    "--icon", "aplatir.ico",
    "--collect-all", "tkinterdnd2",    # embarque la bibliothèque native tkdnd
    "--collect-submodules", "pystray",
    "--hidden-import", "pystray._win32",
    "--collect-submodules", "pymupdf",
])

if not os.path.exists(EXE if sys.platform == "win32" else os.path.join(ICI, "dist", "AplatirPDF")):
    print("\nÉCHEC : l'exécutable n'a pas été produit.")
    sys.exit(1)

if sys.platform == "win32":            # même auto-test que la CI : un exe cassé ne passe pas inaperçu
    rapport = os.path.join(ICI, "selftest.txt")
    if os.path.exists(rapport):
        os.remove(rapport)
    try:
        code = subprocess.run([EXE, "--selftest", rapport], timeout=180).returncode
    except subprocess.TimeoutExpired:
        code = -1
    if os.path.exists(rapport):
        print(open(rapport, encoding="utf-8").read())
    if code != 0:
        print("\nÉCHEC : l'auto-test de l'exécutable a échoué (voir ci-dessus).")
        sys.exit(1)

print("\nExécutable prêt et vérifié :", EXE, f"(version {version})")
