"""Interface et processus : ce qui dépend de Windows (DPI, rappel de dépôt OLE, sortie du processus).

Exigent un affichage :  APLATIR_GUI_TESTS=1 xvfb-run -a python -m pytest tests/test_windows_gui.py
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant WIN-n du rapport).
"""
import json
import os
import pathlib
import socket
import subprocess
import sys
import time
from types import SimpleNamespace

import pymupdf
import pytest

tray = pytest.importorskip("aplatir_tray")

GUI = pytest.mark.skipif(not os.environ.get("APLATIR_GUI_TESTS"), reason="GUI")

pytestmark = pytest.mark.filterwarnings("ignore::pytest.PytestUnraisableExceptionWarning")


@pytest.fixture(autouse=True)
def _gc_sur_le_fil_principal_seulement():
    # un objet Tk ramassé par le GC depuis un autre fil bloque ~1 s par variable (voir test_gui_app.py)
    import gc
    gc.disable()
    yield
    gc.enable()
    gc.collect()


def _pdf(chemin):
    chemin = pathlib.Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "x")
    p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


@pytest.fixture
def faire_app(tmp_path, monkeypatch):
    """faire_app(sortie=True) -> App (sans icône, sans registre, sans explorateur)."""
    apps = []
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "pystray", None)
    monkeypatch.setattr(tray, "regler_demarrage", lambda *a, **k: True)
    monkeypatch.setattr(tray, "demarrage_actif", lambda *a, **k: False)
    monkeypatch.setattr(tray, "ouvrir_dossier", lambda *a, **k: None)

    def faire(sortie=True):
        d = tmp_path / "appdata" / tray.ID_APP
        d.mkdir(parents=True, exist_ok=True)
        cfg = {"sortie": str(tmp_path / "out") if sortie else "", "premier_plan": False}
        (d / "config.json").write_text(json.dumps(cfg), encoding="utf-8")
        app = tray.App(cache=False, fichiers=[], serveur=None)
        apps.append(app)
        return app

    yield faire
    for a in apps:
        try:
            a.jobs.put(None)
            a.root.destroy()
        except Exception:
            pass


# --------------------------------------------------------------------------- #
# WIN-2 : hauteur des lignes du tableau de résultats avec une mise à l'échelle Windows élevée
# --------------------------------------------------------------------------- #
@GUI
@pytest.mark.parametrize("echelle", [1.5, 2.0], ids=["150%", "200%"])
def test_lignes_du_tableau_assez_hautes_pour_la_police(faire_app, monkeypatch, echelle):
    import tkinter.font as tkfont
    ClasseTk = tray.TkinterDnD.Tk

    class TkMisAEchelle(ClasseTk):
        """Reproduit ce que fait Windows : processus « DPI aware », police par défaut en pixels agrandis."""
        def __init__(self, *a, **k):
            super().__init__(*a, **k)
            self.tk.call("tk", "scaling", 96 * echelle / 72.0)
            tkfont.nametofont("TkDefaultFont", self).configure(size=-round(12 * echelle))   # Segoe UI 9 pt

    monkeypatch.setattr(tray.TkinterDnD, "Tk", TkMisAEchelle)
    app = faire_app()
    app.root.update()
    police = tkfont.nametofont("TkDefaultFont", app.root)
    iid = app.arbre.insert("", "end", values=("Rapport é.pdf", "✔ Aplati", "1 signature(s)"))
    app.root.update()
    boite = app.arbre.bbox(iid)
    assert boite, "ligne non affichée"
    hauteur_texte = police.metrics("linespace")
    assert boite[3] >= hauteur_texte, f"ligne de {boite[3]} px pour un texte de {hauteur_texte} px"


# --------------------------------------------------------------------------- #
# WIN-6 : le travail se fait DANS le rappel de dépôt OLE (l'Explorateur attend la fin de Drop)
# --------------------------------------------------------------------------- #
@GUI
def test_le_depot_est_traite_apres_le_retour_du_rappel_drop(faire_app, tmp_path):
    app = faire_app()
    pdf = _pdf(tmp_path / "in" / "a b.pdf")
    appels = []
    app.ajouter = lambda chemins: appels.append(list(chemins))

    rendu = app._on_drop(SimpleNamespace(data="{%s}" % pdf.as_posix(), action="copy"))

    assert rendu == "copy"
    assert appels == [], "ajouter() a été exécuté à l'intérieur du rappel <<Drop>>"
    fin = time.time() + 5
    while time.time() < fin and not appels:
        app.root.update()
        time.sleep(0.01)
    assert appels and appels[0] == [pdf.as_posix()], "le dépôt ne doit pas être perdu : traité juste après"


@GUI
def test_selecteur_de_dossier_jamais_ouvert_dans_le_rappel_drop(faire_app, tmp_path, monkeypatch):
    app = faire_app(sortie=False)
    pdf = _pdf(tmp_path / "in" / "a.pdf")
    dans_drop = []
    etat = {"drop": False}

    def faux_choisir():
        dans_drop.append(etat["drop"])
        return False

    monkeypatch.setattr(app, "choisir_sortie", faux_choisir)
    etat["drop"] = True
    app._on_drop(SimpleNamespace(data="{%s}" % pdf.as_posix(), action="copy"))
    etat["drop"] = False
    assert dans_drop == [], "le sélecteur de dossier (modal) a été ouvert pendant le rappel <<Drop>>"


# --------------------------------------------------------------------------- #
# WIN-5 : une exception qui s'échappe de main() après le démarrage de l'icône laisse un processus invisible
# --------------------------------------------------------------------------- #
_HARNAIS = r"""
import os, sys, threading, types
racine, port = sys.argv[1], sys.argv[2]
sys.path.insert(0, racine)
faux = types.ModuleType("pystray")                    # comme pystray (win32) : un fil NON démon qui ne finit jamais
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
chemin = os.path.join(racine, "aplatir_tray.py")
source = open(chemin, encoding="utf-8").read().replace("PORT_INSTANCE = 47653", "PORT_INSTANCE = " + port)
# une exception imprévue, après le démarrage de l'icône (ici dans la boucle principale)
assert "    app.lancer()\n" in source
source = source.replace("    app.lancer()\n", "    raise RuntimeError('exception imprevue')\n")
sys.argv = [chemin, "--tray"]
exec(compile(source, chemin, "exec"), {"__name__": "__main__", "__file__": chemin})
"""


@GUI
def test_le_processus_s_arrete_meme_si_main_leve(tmp_path):
    racine = str(pathlib.Path(tray.__file__).resolve().parent)
    appdata = tmp_path / "appdata"
    (appdata / tray.ID_APP).mkdir(parents=True)
    (appdata / tray.ID_APP / "config.json").write_text(json.dumps({"sortie": str(tmp_path / "out")}),
                                                       encoding="utf-8")
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    env = dict(os.environ, APPDATA=str(appdata), APLATIR_SANS_DIALOGUE="1")
    try:
        subprocess.run([sys.executable, "-c", _HARNAIS, racine, str(port)], capture_output=True, text=True,
                       timeout=6, env=env)
        termine = True
    except subprocess.TimeoutExpired:
        termine = False
    journal = appdata / tray.ID_APP / "aplatir.log"
    assert journal.is_file() and "démarrage" in journal.read_text(encoding="utf-8"), \
        "le harnais n'a pas atteint le point voulu (App démarrée, icône lancée)"
    if not termine:
        pytest.fail("le processus ne se termine pas après l'exception (fil d'icône non démon)")
