# -*- coding: utf-8 -*-
"""Fabrique dist/AplatirPDF.exe avec PyInstaller (à lancer sous Windows).

    python build.py
"""
from __future__ import annotations

import os
import sys

import PyInstaller.__main__

ICI = os.path.dirname(os.path.abspath(__file__))
os.chdir(ICI)

import aplatir_icon

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
print("\nExécutable prêt :", os.path.join(ICI, "dist", "AplatirPDF.exe"))
sys.exit(0)
