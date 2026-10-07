# -*- coding: utf-8 -*-
"""Audit « exigences » côté interface : ce que l'utilisateur VOIT après un dépôt / en fermant la fenêtre.

Les tests d'interface exigent un affichage (``xvfb-run -a`` sous Linux) :
    APLATIR_GUI_TESTS=1 xvfb-run -a python -m pytest tests/test_requirements_gui.py
Les tests ``xfail(strict=True)`` démontrent un vrai défaut (identifiant REQ-n du rapport).
"""
import gc
import json
import os
import time
from pathlib import Path
from types import SimpleNamespace

import pymupdf
import pytest

GUI = pytest.mark.skipif(not os.environ.get("APLATIR_GUI_TESTS"), reason="GUI")
pytestmark = [GUI, pytest.mark.filterwarnings("ignore::pytest.PytestUnraisableExceptionWarning")]

tray = pytest.importorskip("aplatir_tray")


@pytest.fixture(autouse=True)
def _gc_sur_le_fil_principal_seulement():
    # un objet Tk ramassé depuis un autre fil bloque ~1 s par variable : on neutralise le GC pendant le test
    gc.disable()
    yield
    gc.enable()
    gc.collect()


class FauxIcone:
    def __init__(self, *a, **k):
        self.appels = []

    def run_detached(self, *a):
        self.appels.append("run_detached")

    def stop(self):
        self.appels.append("stop")

    def update_menu(self):
        self.appels.append("update_menu")

    def notify(self, message, titre=None):
        self.appels.append(("notify", message))


class FauxMenu:
    SEPARATOR = object()

    def __init__(self, *items):
        self.items = items


class FauxItem:
    def __init__(self, texte, action, **kw):
        pass


def _pdf(chemin):
    chemin = Path(chemin)
    chemin.parent.mkdir(parents=True, exist_ok=True)
    d = pymupdf.open()
    p = d.new_page()
    p.insert_text((72, 72), "Rapport")
    p.add_stamp_annot(pymupdf.Rect(300, 150, 500, 220), stamp=0)
    d.save(str(chemin))
    d.close()
    return chemin


@pytest.fixture
def faire_app(tmp_path, monkeypatch):
    apps = []
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    monkeypatch.setattr(tray, "regler_demarrage", lambda *a, **k: True)
    monkeypatch.setattr(tray, "demarrage_actif", lambda *a, **k: False)
    monkeypatch.setattr(tray, "ouvrir_dossier", lambda *a, **k: None)

    def faire(icone=False):
        monkeypatch.setattr(tray, "pystray", SimpleNamespace(Icon=FauxIcone, Menu=FauxMenu, MenuItem=FauxItem)
                            if icone else None)
        d = tmp_path / "appdata" / tray.ID_APP
        d.mkdir(parents=True, exist_ok=True)
        (d / "config.json").write_text(json.dumps({"sortie": str(tmp_path / "sortie")}), encoding="utf-8")
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


def _pomper(app, condition, delai=30.0):
    fin = time.time() + delai
    while time.time() < fin:
        app.root.update()
        if condition():
            return True
        time.sleep(0.005)
    return False


# --------------------------------------------------------------------------- #
# REQ-1 : plusieurs rapports aux noms voisins -> les lignes de la liste doivent être distinguables
# --------------------------------------------------------------------------- #
@pytest.fixture
def lot_de_rapports(faire_app, tmp_path):
    noms = [f"Rapport de Fin de Fabrication OF-2024-00{n} - Fiche de contrôle dimensionnel indice B.pdf"
            for n in (123, 124, 125, 126)]
    app = faire_app()
    app.ajouter([str(_pdf(tmp_path / "in" / n)) for n in noms])
    assert _pomper(app, lambda: app.en_cours == 0 and len(app.arbre.get_children()) == 4)
    assert [app.arbre.item(i, "values")[1] for i in app.arbre.get_children()] == ["✔ Aplati"] * 4
    return app, noms


@pytest.mark.xfail(strict=True, reason="REQ-1: la colonne « Fichier » est figée à 200 px (stretch=False) : quatre "
                                       "rapports « Rapport de Fin de Fabrication OF-2024-00123/124/125/126 - ... » "
                                       "affichent exactement le même début, même fenêtre agrandie ; le nom de sortie "
                                       "(« Détail ») est tronqué lui aussi à la taille par défaut")
def test_les_lignes_de_la_liste_sont_distinguables(lot_de_rapports):
    from tkinter import font as tkfont
    app, noms = lot_de_rapports
    police = tkfont.nametofont("TkDefaultFont")
    largeur = int(app.arbre.column("fichier", "width")) - 12        # marges de cellule

    def visible(nom):
        for n in range(len(nom), 0, -1):
            if police.measure(nom[:n]) <= largeur:
                return nom[:n]
        return ""

    assert len({visible(n) for n in noms}) == len(noms), [visible(n) for n in noms]


# --------------------------------------------------------------------------- #
# REQ-2 : « reste dans la barre des tâches » : fermer la fenêtre ne doit pas faire disparaître l'outil sans un mot
# --------------------------------------------------------------------------- #
@pytest.mark.xfail(strict=True, reason="REQ-2: la croix appelle masquer() (withdraw) : plus aucun bouton dans la "
                                       "barre des tâches et aucune notification « l'outil reste actif près de "
                                       "l'horloge » ; sous Windows 11 l'icône est de plus dans le menu « ^ » masqué "
                                       "par défaut : l'utilisateur croit avoir quitté ou ne retrouve plus l'outil")
def test_fermer_la_fenetre_previent_ou_reste_dans_la_barre_des_taches(faire_app):
    app = faire_app(icone=True)
    app.afficher()
    app.root.update()
    app.fermer_fenetre()
    app.root.update()
    prevenu = any(isinstance(a, tuple) and a[0] == "notify" for a in app.icone.appels)
    dans_la_barre = app.root.state() == "iconic"                  # réduite : bouton de barre des tâches
    assert prevenu or dans_la_barre, (app.icone.appels, app.root.state())
