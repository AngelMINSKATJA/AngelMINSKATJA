"""Le processus Aplatir PDF se termine VRAIMENT quand on quitte (bloc ``__main__`` : ``os._exit``),
même avec un fil d'icône non démon (comme pystray) et même pendant un traitement.

Exige un affichage :  APLATIR_GUI_TESTS=1 xvfb-run -a python -m pytest tests/test_gui_process.py
"""
import json
import os
import pathlib
import socket
import subprocess
import sys
import time

import pymupdf
import pytest

tray = pytest.importorskip("aplatir_tray")

GUI = pytest.mark.skipif(not os.environ.get("APLATIR_GUI_TESTS"), reason="GUI")

_HARNAIS = r"""
import gc, os, sys, threading, time, types
racine, port, occupe, pdf = sys.argv[1], sys.argv[2], sys.argv[3] == "1", sys.argv[4]
sys.path.insert(0, racine)
faux = types.ModuleType("pystray")                       # comme pystray : un fil NON démon qui ne finit jamais
class Icon:
    def __init__(self, *a, **k): pass
    def run_detached(self, *a): threading.Thread(target=threading.Event().wait, daemon=False).start()
    def stop(self): pass
    def update_menu(self): pass
    def notify(self, *a): pass
class Menu:
    SEPARATOR = object()
    def __init__(self, *a): pass
class MenuItem:
    def __init__(self, *a, **k): pass
faux.Icon, faux.Menu, faux.MenuItem = Icon, Menu, MenuItem
sys.modules["pystray"] = faux
import tkinter.messagebox
tkinter.messagebox.askyesno = lambda *a, **k: True        # « Quitter quand même ? » -> oui
if occupe:
    import aplatir_core
    aplatir_core.aplatir_fichier = lambda *a, **k: threading.Event().wait()   # traitement sans fin
def quitter():
    fin = time.time() + 20
    while time.time() < fin:
        for o in gc.get_objects():
            if type(o).__name__ == "App" and hasattr(o, "evenements") and (o.en_cours or not occupe):
                print("busy:", o.en_cours, flush=True)
                o._poster("quitter")
                return
        time.sleep(0.05)
threading.Thread(target=quitter, daemon=True).start()
chemin = os.path.join(racine, "aplatir_tray.py")
source = open(chemin, encoding="utf-8").read().replace("PORT_INSTANCE = 47653", "PORT_INSTANCE = " + port)
if os.environ.get("SANS_OS_EXIT"):
    source = source.replace("os._exit(code)", "sys.exit(code)")
sys.argv = [chemin, "--tray"] + ([pdf] if pdf else [])
exec(compile(source, chemin, "exec"), {"__name__": "__main__", "__file__": chemin})
"""


def _pdf(chemin):
    chemin = pathlib.Path(chemin)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "x")
    p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


def _port_libre():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def _lancer_main(tmp_path, occupe, extra_env=None, limite=20):
    racine = str(pathlib.Path(tray.__file__).resolve().parent)
    appdata = tmp_path / "appdata"
    (appdata / tray.ID_APP).mkdir(parents=True)
    (appdata / tray.ID_APP / "config.json").write_text(json.dumps({"sortie": str(tmp_path / "out")}),
                                                       encoding="utf-8")
    pdf = str(_pdf(tmp_path / "a.pdf")) if occupe else ""
    env = dict(os.environ, APPDATA=str(appdata), **(extra_env or {}))
    t0 = time.time()
    r = subprocess.run([sys.executable, "-c", _HARNAIS, racine, str(_port_libre()), "1" if occupe else "0", pdf],
                       capture_output=True, text=True, timeout=limite, env=env)
    return r, time.time() - t0


@GUI
@pytest.mark.parametrize("occupe", [False, True], ids=["inactif", "traitement_en_cours"])
def test_le_processus_se_termine_apres_quitter_malgre_le_fil_de_l_icone(tmp_path, occupe):
    r, duree = _lancer_main(tmp_path, occupe)
    assert r.returncode == 0, r.stderr[-800:]
    assert ("busy: 1" if occupe else "busy: 0") in r.stdout
    assert duree < 12


@GUI
def test_harnais_detecte_un_processus_qui_ne_se_termine_pas(tmp_path):
    """Garde-fou du test précédent : sans os._exit, le fil non démon de l'icône retient le processus."""
    with pytest.raises(subprocess.TimeoutExpired):
        _lancer_main(tmp_path, False, extra_env={"SANS_OS_EXIT": "1"}, limite=4)
