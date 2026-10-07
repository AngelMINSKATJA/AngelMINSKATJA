#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Aplatir PDF - outil de barre des tâches.

Reste dans la zone de notification de Windows. On ouvre la fenêtre d'un clic sur l'icône,
on y glisse-dépose des PDF (ou des dossiers) : ils sont aplatis (signatures, tampons,
annotations et champs gravés dans la page) puis enregistrés sous ``[a]- <nom>`` dans
le dossier de sortie choisi.

Options de la ligne de commande :
    --tray            démarre caché dans la zone de notification (utilisé au démarrage de Windows)
    --selftest FICHIER  auto-test sans interface utilisateur, rapport écrit dans FICHIER
    fichier.pdf ...   PDF à traiter tout de suite (glisser des PDF sur l'exe, « Envoyer vers »...)
"""
from __future__ import annotations

import os
import sys


def _reparer_flux_standard() -> None:
    """Exe « fenêtré » (PyInstaller --windowed) : stdout/stderr valent None."""
    for nom in ("stdout", "stderr"):
        if getattr(sys, nom, None) is None:
            setattr(sys, nom, open(os.devnull, "w", encoding="utf-8"))


_reparer_flux_standard()

import argparse
import json
import logging
import logging.handlers
import queue
import socket
import subprocess
import threading
import traceback
from pathlib import Path

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

import aplatir_core as core
from aplatir_icon import dessiner_icone, icone_png_base64

try:
    from tkinterdnd2 import DND_FILES, TkinterDnD
except Exception:  # pragma: no cover - signalé clairement plus bas
    DND_FILES = TkinterDnD = None

try:
    import pystray
except Exception:     # pas d'affichage / pas de backend : on fonctionne sans icône
    pystray = None

NOM_APP = "Aplatir PDF"
ID_APP = "AplatirPDF"
PORT_INSTANCE = 47653
CLE_RUN = r"Software\Microsoft\Windows\CurrentVersion\Run"
DEFAUTS = {"sortie": "", "demarrage_auto": True, "premier_plan": True, "securite": True}

log = logging.getLogger(ID_APP)


# --------------------------------------------------------------------------- #
# Configuration et journal
# --------------------------------------------------------------------------- #
def dossier_config() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home() / ".config")
    return Path(base) / ID_APP


class Config:
    """Réglages sauvegardés dans %APPDATA%\\AplatirPDF\\config.json."""

    def __init__(self, chemin: Path | None = None):
        self.chemin = chemin or (dossier_config() / "config.json")
        self.valeurs = dict(DEFAUTS)

    def charger(self) -> bool:
        """Retourne False si c'est le tout premier lancement (pas de fichier)."""
        try:
            data = json.loads(self.chemin.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return False
        except Exception:
            log.warning("config illisible, valeurs par défaut", exc_info=True)
            return True
        if isinstance(data, dict):
            for k, v in DEFAUTS.items():
                if k in data and isinstance(data[k], type(v)):
                    self.valeurs[k] = data[k]
        return True

    def sauver(self) -> None:
        try:
            self.chemin.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.chemin.with_suffix(".tmp")
            tmp.write_text(json.dumps(self.valeurs, indent=2, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, self.chemin)
        except Exception:
            log.warning("impossible d'enregistrer la config", exc_info=True)

    def __getitem__(self, cle):
        return self.valeurs[cle]

    def __setitem__(self, cle, valeur):
        self.valeurs[cle] = valeur
        self.sauver()


def configurer_journal() -> None:
    log.setLevel(logging.INFO)
    try:
        d = dossier_config()
        d.mkdir(parents=True, exist_ok=True)
        h = logging.handlers.RotatingFileHandler(d / "aplatir.log", maxBytes=256_000,
                                                 backupCount=2, encoding="utf-8")
        h.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
        log.addHandler(h)
    except Exception:
        log.addHandler(logging.NullHandler())


# --------------------------------------------------------------------------- #
# Démarrage automatique avec Windows (clé « Run » de l'utilisateur, sans droits admin)
# --------------------------------------------------------------------------- #
def commande_demarrage() -> str:
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}" --tray'
    pythonw = Path(sys.executable).with_name("pythonw.exe")
    interpreteur = pythonw if pythonw.exists() else Path(sys.executable)
    return f'"{interpreteur}" "{Path(__file__).resolve()}" --tray'


def demarrage_actif(nom: str = ID_APP) -> bool:
    if sys.platform != "win32":
        return False
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, CLE_RUN) as cle:
            winreg.QueryValueEx(cle, nom)
            return True
    except OSError:
        return False


def regler_demarrage(actif: bool, nom: str = ID_APP) -> bool:
    """Ajoute/retire l'entrée de démarrage. Retourne True si l'opération a réussi."""
    if sys.platform != "win32":
        return False
    import winreg
    try:
        with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, CLE_RUN, 0, winreg.KEY_SET_VALUE) as cle:
            if actif:
                winreg.SetValueEx(cle, nom, 0, winreg.REG_SZ, commande_demarrage())
            else:
                try:
                    winreg.DeleteValue(cle, nom)
                except FileNotFoundError:
                    pass
        return True
    except OSError:
        log.warning("démarrage automatique : accès au registre refusé", exc_info=True)
        return False


# --------------------------------------------------------------------------- #
# Instance unique (petit serveur local : une 2e instance prévient la 1re puis s'arrête)
# --------------------------------------------------------------------------- #
def devenir_instance_principale():
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.bind(("127.0.0.1", PORT_INSTANCE))
        s.listen(5)
        return s
    except OSError:
        s.close()
        return None


def prevenir_instance_existante(message: dict) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", PORT_INSTANCE), timeout=3) as c:
            c.sendall((json.dumps(message) + "\n").encode("utf-8"))
            c.shutdown(socket.SHUT_WR)
            return c.recv(16).startswith(b"OK")
    except OSError:
        return False


def servir_instance(serveur: socket.socket, poster) -> None:
    serveur.settimeout(1.0)
    while True:
        try:
            c, _ = serveur.accept()
        except socket.timeout:
            continue
        except OSError:
            return
        with c:
            try:
                c.settimeout(3)
                data = b""
                while len(data) < (1 << 20):
                    morceau = c.recv(65536)
                    if not morceau:
                        break
                    data += morceau
                    if data.endswith(b"\n"):
                        break
                msg = json.loads(data.decode("utf-8"))
                if isinstance(msg, dict):
                    poster(msg)
                    c.sendall(b"OK")
            except Exception:
                log.warning("message d'instance invalide", exc_info=True)


# --------------------------------------------------------------------------- #
# Divers
# --------------------------------------------------------------------------- #
def ouvrir_dossier(chemin: Path) -> None:
    try:
        if sys.platform == "win32":
            os.startfile(str(chemin))          # noqa: S606 - action voulue
        elif sys.platform == "darwin":
            subprocess.Popen(["open", str(chemin)])
        else:
            subprocess.Popen(["xdg-open", str(chemin)])
    except Exception:
        log.warning("ouverture du dossier impossible", exc_info=True)


def activer_dpi_windows() -> None:
    if sys.platform == "win32":
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass


LIBELLES = {   # statut -> (texte, étiquette de couleur)
    "ok": ("✔ Aplati", "ok"),
    "copie": ("✔ Copié", "ok"),
    "securite": ("⚠ Aplati (image)", "alerte"),
    "alerte": ("⚠ À vérifier", "alerte"),
    "ignore": ("— Ignoré", "ignore"),
    "erreur": ("✖ Erreur", "erreur"),
}


# --------------------------------------------------------------------------- #
# Application
# --------------------------------------------------------------------------- #
class App:
    def __init__(self, cache: bool, fichiers: list, serveur: socket.socket | None):
        self.cfg = Config()
        premiere_fois = not self.cfg.charger()
        if getattr(sys, "frozen", False):          # garde le chemin de l'exe à jour
            regler_demarrage(self.cfg["demarrage_auto"])
        if premiere_fois:
            self.cfg.sauver()

        self.evenements: queue.Queue = queue.Queue()   # tout ce qui doit toucher à l'interface
        self.jobs: queue.Queue = queue.Queue()
        self.en_cours = 0
        self.stats = {"ok": 0, "alerte": 0, "erreur": 0, "ignore": 0}
        self.visible = False
        self.icone = None

        self.root = TkinterDnD.Tk()
        self.k = self.root.winfo_fpixels("1i") / 96.0
        self.root.report_callback_exception = self._erreur_tk
        self._construire()
        self.root.protocol("WM_DELETE_WINDOW", self.fermer_fenetre)

        threading.Thread(target=self._boucle_travail, daemon=True).start()
        if serveur is not None:
            threading.Thread(target=servir_instance, args=(serveur, self._message_instance),
                             daemon=True).start()
        self._demarrer_icone()
        self.root.after(100, self._pomper)

        if cache and not premiere_fois and self.icone is not None:
            self.root.withdraw()
        else:
            self.afficher()
        if fichiers:
            self.root.after(400, lambda: self.ajouter(fichiers))

    # ----- interface ------------------------------------------------------- #
    def _px(self, v: int) -> int:
        return int(v * self.k)

    def _construire(self) -> None:
        r = self.root
        r.title(NOM_APP)
        r.geometry(f"{self._px(640)}x{self._px(560)}")
        r.minsize(self._px(520), self._px(440))
        try:
            self._img_icone = tk.PhotoImage(data=icone_png_base64(64))
            r.iconphoto(True, self._img_icone)
        except Exception:
            pass

        main = ttk.Frame(r, padding=self._px(12))
        main.pack(fill="both", expand=True)
        main.columnconfigure(0, weight=1)
        main.rowconfigure(2, weight=1)

        # dossier de sortie
        ligne = ttk.Frame(main)
        ligne.grid(row=0, column=0, sticky="ew")
        ligne.columnconfigure(1, weight=1)
        ttk.Label(ligne, text="Dossier de sortie :").grid(row=0, column=0, padx=(0, 6))
        self.var_sortie = tk.StringVar(value=self.cfg["sortie"])
        ttk.Entry(ligne, textvariable=self.var_sortie, state="readonly").grid(row=0, column=1, sticky="ew")
        ttk.Button(ligne, text="Parcourir…", command=self.choisir_sortie).grid(row=0, column=2, padx=(6, 0))
        ttk.Button(ligne, text="Ouvrir", command=self.ouvrir_sortie).grid(row=0, column=3, padx=(6, 0))

        # zone de dépôt
        self.zone = tk.Label(main, relief="ridge", bd=2, bg="#eef3fb", fg="#1f4e8c",
                             font=("Segoe UI", 13, "bold"), height=4, justify="center")
        self.zone.grid(row=1, column=0, sticky="ew", pady=self._px(10))
        self._maj_texte_zone()

        # liste des résultats
        cadre = ttk.Frame(main)
        cadre.grid(row=2, column=0, sticky="nsew")
        cadre.columnconfigure(0, weight=1)
        cadre.rowconfigure(0, weight=1)
        self.arbre = ttk.Treeview(cadre, columns=("fichier", "statut", "detail"),
                                  show="headings", selectmode="extended")
        for col, texte, largeur, etire in (("fichier", "Fichier", 200, False),
                                           ("statut", "Résultat", 120, False),
                                           ("detail", "Détail", 280, True)):
            self.arbre.heading(col, text=texte)
            self.arbre.column(col, width=self._px(largeur), stretch=etire)
        self.arbre.tag_configure("ok", foreground="#1a7f37")
        self.arbre.tag_configure("alerte", foreground="#b35900")
        self.arbre.tag_configure("erreur", foreground="#c62828")
        self.arbre.tag_configure("ignore", foreground="#6b7280")
        self.arbre.tag_configure("attente", foreground="#6b7280")
        self.arbre.grid(row=0, column=0, sticky="nsew")
        asc = ttk.Scrollbar(cadre, orient="vertical", command=self.arbre.yview)
        asc.grid(row=0, column=1, sticky="ns")
        self.arbre.configure(yscrollcommand=asc.set)

        # boutons
        boutons = ttk.Frame(main)
        boutons.grid(row=3, column=0, sticky="ew", pady=(self._px(8), 0))
        ttk.Button(boutons, text="Ajouter des PDF…", command=self.choisir_fichiers).pack(side="left")
        ttk.Button(boutons, text="Vider la liste", command=self.vider_liste).pack(side="left", padx=6)
        ttk.Button(boutons, text="Journal", command=self.ouvrir_journal).pack(side="left")
        ttk.Button(boutons, text="Quitter", command=self.quitter).pack(side="right")

        # options
        opts = ttk.Frame(main)
        opts.grid(row=4, column=0, sticky="ew", pady=(self._px(8), 0))
        self.var_demarrage = tk.BooleanVar(value=self.cfg["demarrage_auto"])
        self.var_premier = tk.BooleanVar(value=self.cfg["premier_plan"])
        self.var_securite = tk.BooleanVar(value=self.cfg["securite"])
        cb = ttk.Checkbutton(opts, text="Lancer au démarrage de Windows", variable=self.var_demarrage,
                             command=lambda: self.definir_demarrage(self.var_demarrage.get()))
        cb.pack(anchor="w")
        if sys.platform != "win32":
            cb.state(["disabled"])
        ttk.Checkbutton(opts, text="Garder la fenêtre au premier plan", variable=self.var_premier,
                        command=self._maj_premier_plan).pack(anchor="w")
        ttk.Checkbutton(opts, text="Sécurité : convertir en image une page si l'aplatissement "
                                   "altère son aspect", variable=self.var_securite,
                        command=self._maj_securite).pack(anchor="w")

        self.var_statut = tk.StringVar(value="Prêt.")
        ttk.Label(main, textvariable=self.var_statut, anchor="w", foreground="#444").grid(
            row=5, column=0, sticky="ew", pady=(self._px(8), 0))

        # glisser-déposer : on enregistre la fenêtre et les zones principales
        for w in (self.root, self.zone, self.arbre):
            w.drop_target_register(DND_FILES)
            w.dnd_bind("<<Drop>>", self._on_drop)
        self.zone.dnd_bind("<<DropEnter>>", self._on_drop_enter)
        self.zone.dnd_bind("<<DropLeave>>", self._on_drop_leave)
        self._maj_premier_plan()

    def _maj_texte_zone(self) -> None:
        if self.cfg["sortie"]:
            self.zone.configure(text="Glissez-déposez vos PDF ici\n(ou des dossiers entiers)")
        else:
            self.zone.configure(text="1. Choisissez le dossier de sortie (Parcourir…)\n"
                                     "2. Glissez-déposez vos PDF ici")

    def _on_drop_enter(self, event):
        self.zone.configure(bg="#cfe0f7")
        return event.action

    def _on_drop_leave(self, event):
        self.zone.configure(bg="#eef3fb")
        return event.action

    def _on_drop(self, event):
        self.zone.configure(bg="#eef3fb")
        try:
            self.ajouter(list(self.root.tk.splitlist(event.data)))
        except Exception:
            self._erreur_tk(*sys.exc_info())
        return event.action

    def _erreur_tk(self, exc, val, tb) -> None:
        log.error("erreur interface\n%s", "".join(traceback.format_exception(exc, val, tb)))
        self.var_statut.set(f"Erreur interne : {val} (voir le journal)")

    # ----- fenêtre / barre des tâches ------------------------------------- #
    def afficher(self) -> None:
        self.root.deiconify()
        if self.root.state() == "iconic":
            self.root.state("normal")
        self.root.lift()
        self.root.attributes("-topmost", True)
        if not self.cfg["premier_plan"]:
            self.root.after(300, lambda: self.root.attributes("-topmost", False))
        try:
            self.root.focus_force()
        except tk.TclError:
            pass
        self.visible = True

    def masquer(self) -> None:
        self.root.withdraw()
        self.visible = False

    def fermer_fenetre(self) -> None:
        if self.icone is not None:
            self.masquer()          # le croix réduit dans la zone de notification
        else:
            self.quitter()

    def _maj_premier_plan(self) -> None:
        self.cfg["premier_plan"] = bool(self.var_premier.get())
        self.root.attributes("-topmost", bool(self.cfg["premier_plan"]))

    def _maj_securite(self) -> None:
        self.cfg["securite"] = bool(self.var_securite.get())

    def definir_demarrage(self, actif: bool) -> None:
        ok = regler_demarrage(actif)
        if not ok and sys.platform == "win32":
            messagebox.showwarning(NOM_APP, "Impossible de modifier le démarrage automatique.")
            actif = demarrage_actif()
        self.cfg["demarrage_auto"] = bool(actif)
        self.var_demarrage.set(bool(actif))
        if self.icone is not None:
            self.icone.update_menu()

    def notifier(self, message: str) -> None:
        if self.icone is not None and not self.visible:
            try:
                self.icone.notify(message, NOM_APP)
            except Exception:
                pass

    # ----- icône de la zone de notification ------------------------------- #
    def _poster(self, *evenement) -> None:
        self.evenements.put(evenement)

    def _message_instance(self, msg: dict) -> None:
        if msg.get("cmd") == "files":
            self._poster("fichiers", [str(p) for p in msg.get("paths", [])])
        else:
            self._poster("afficher")

    def _demarrer_icone(self) -> None:
        if pystray is None:
            return
        try:
            menu = pystray.Menu(
                pystray.MenuItem("Ouvrir Aplatir PDF", lambda *_: self._poster("afficher"), default=True),
                pystray.MenuItem("Choisir le dossier de sortie…", lambda *_: self._poster("choisir_sortie")),
                pystray.MenuItem("Ouvrir le dossier de sortie", lambda *_: self._poster("ouvrir_sortie")),
                pystray.Menu.SEPARATOR,
                pystray.MenuItem("Lancer au démarrage de Windows",
                                 lambda *_: self._poster("basculer_demarrage"),
                                 checked=lambda _item: bool(self.cfg["demarrage_auto"]),
                                 visible=sys.platform == "win32"),
                pystray.Menu.SEPARATOR,
                pystray.MenuItem("Quitter", lambda *_: self._poster("quitter")),
            )
            icone = pystray.Icon(ID_APP, dessiner_icone(64), NOM_APP, menu)
            icone.run_detached()
            self.icone = icone
        except Exception:
            log.warning("icône de la zone de notification indisponible", exc_info=True)
            self.icone = None

    # ----- boucle d'événements (thread principal) ------------------------- #
    def _pomper(self) -> None:
        try:
            while True:
                self._evenement(self.evenements.get_nowait())
        except queue.Empty:
            pass
        except Exception:
            log.error("erreur dans la boucle d'événements", exc_info=True)
        finally:
            try:
                self.root.after(100, self._pomper)
            except tk.TclError:
                pass

    def _evenement(self, ev: tuple) -> None:
        nom = ev[0]
        if nom == "afficher":
            self.afficher()
        elif nom == "choisir_sortie":
            self.afficher()
            self.choisir_sortie()
        elif nom == "ouvrir_sortie":
            self.ouvrir_sortie()
        elif nom == "basculer_demarrage":
            self.definir_demarrage(not self.cfg["demarrage_auto"])
        elif nom == "quitter":
            self.quitter()
        elif nom == "fichiers":
            self.ajouter(ev[1])
        elif nom == "debut":
            self.arbre.set(ev[1], "statut", "⚙ En cours…")
        elif nom == "fin":
            self._fin(ev[1], ev[2])

    # ----- actions --------------------------------------------------------- #
    def choisir_sortie(self) -> bool:
        depart = self.cfg["sortie"] if self.cfg["sortie"] and Path(self.cfg["sortie"]).is_dir() else None
        choix = filedialog.askdirectory(title="Dossier de sortie des PDF aplatis",
                                        initialdir=depart, mustexist=False, parent=self.root)
        if not choix:
            return False
        self.cfg["sortie"] = str(Path(choix))
        self.var_sortie.set(self.cfg["sortie"])
        self._maj_texte_zone()
        return True

    def ouvrir_sortie(self) -> None:
        if not self.cfg["sortie"]:
            self.afficher()
            if not self.choisir_sortie():
                return
        p = Path(self.cfg["sortie"])
        try:
            p.mkdir(parents=True, exist_ok=True)
        except OSError as e:
            messagebox.showerror(NOM_APP, f"Dossier de sortie inaccessible :\n{e}")
            return
        ouvrir_dossier(p)

    def ouvrir_journal(self) -> None:
        journal = dossier_config() / "aplatir.log"
        if journal.exists():
            ouvrir_dossier(journal)
        else:
            messagebox.showinfo(NOM_APP, "Pas encore de journal.")

    def choisir_fichiers(self) -> None:
        choix = filedialog.askopenfilenames(title="Choisir des PDF", parent=self.root,
                                            filetypes=[("Fichiers PDF", "*.pdf"), ("Tous les fichiers", "*.*")])
        if choix:
            self.ajouter(list(choix))

    def vider_liste(self) -> None:
        if self.en_cours:
            messagebox.showinfo(NOM_APP, "Un traitement est en cours : patientez.")
            return
        self.arbre.delete(*self.arbre.get_children())
        self.var_statut.set("Prêt.")

    def ajouter(self, chemins: list) -> None:
        """Point d'entrée commun : glisser-déposer, bouton, ligne de commande."""
        if not self.cfg["sortie"] or not str(self.cfg["sortie"]).strip():
            self.afficher()
            if not self.choisir_sortie():
                self.var_statut.set("Aucun dossier de sortie choisi : rien n'a été traité.")
                return
        sortie = Path(self.cfg["sortie"])
        pdfs, ignores = core.collecter_pdf(chemins, exclure=sortie)
        for p, raison in ignores:
            iid = self.arbre.insert("", "end", values=(p.name, LIBELLES["ignore"][0], raison), tags=("ignore",))
            self.arbre.see(iid)
        if not pdfs:
            if not ignores:
                self.var_statut.set("Aucun PDF dans ce qui a été déposé.")
            return
        if self.en_cours == 0:
            self.stats = {"ok": 0, "alerte": 0, "erreur": 0, "ignore": 0}
        for p in pdfs:
            iid = self.arbre.insert("", "end", values=(p.name, "⏳ En attente", str(p.parent)), tags=("attente",))
            self.arbre.see(iid)
            self.en_cours += 1
            self.jobs.put((iid, p, sortie, bool(self.cfg["securite"])))
        self.var_statut.set(f"{self.en_cours} fichier(s) en cours de traitement…")

    def _boucle_travail(self) -> None:
        while True:
            job = self.jobs.get()
            if job is None:
                return
            iid, src, sortie, securite = job
            self._poster("debut", iid)
            try:
                res = core.aplatir_fichier(src, sortie, securite=securite)
            except Exception as e:      # ne devrait pas arriver : aplatir_fichier ne lève pas
                res = core.Resultat(src=src, statut="erreur", message=f"{type(e).__name__} : {e}")
            self._poster("fin", iid, res)

    def _fin(self, iid: str, res: "core.Resultat") -> None:
        self.en_cours = max(0, self.en_cours - 1)
        texte, tag = LIBELLES.get(res.statut, LIBELLES["erreur"])
        detail = res.message
        if res.statut in ("ok", "copie", "securite", "alerte") and res.dst is not None:
            detail = f"{res.message}  →  {res.dst.name}"
        if self.arbre.exists(iid):
            self.arbre.item(iid, values=(res.src.name, texte, detail), tags=(tag,))
        self.stats[tag if tag in self.stats else "ok"] += 1
        log.info("%s | %s | %s | signatures=%s pages=%s image=%s", res.statut, res.src, detail,
                 res.signatures, res.pages_elements, res.pages_image)
        if self.en_cours:
            self.var_statut.set(f"{self.en_cours} fichier(s) en cours de traitement…")
        else:
            self._lot_termine()

    def _lot_termine(self) -> None:
        s = self.stats
        morceaux = [f"{s['ok']} aplati(s)"]
        if s["alerte"]:
            morceaux.append(f"{s['alerte']} à vérifier")
        if s["erreur"]:
            morceaux.append(f"{s['erreur']} erreur(s)")
        if s["ignore"]:
            morceaux.append(f"{s['ignore']} ignoré(s)")
        msg = "Terminé : " + ", ".join(morceaux) + "."
        self.var_statut.set(msg)
        self.notifier(msg)

    def quitter(self) -> None:
        if self.en_cours and not messagebox.askyesno(
                NOM_APP, "Un aplatissement est en cours.\nQuitter quand même ?", parent=self.root):
            return
        try:
            if self.icone is not None:
                self.icone.stop()
        except Exception:
            pass
        self.jobs.put(None)
        self.root.destroy()

    def lancer(self) -> None:
        self.root.mainloop()


# --------------------------------------------------------------------------- #
# Auto-test (utilisé par l'intégration continue sur Windows, sans interface)
# --------------------------------------------------------------------------- #
def autotest(chemin_rapport: str) -> int:
    import tempfile
    import pymupdf

    lignes: list[str] = []
    ok = True

    def etape(nom, fonction):
        nonlocal ok
        try:
            lignes.append(f"OK     {nom} : {fonction()}")
        except BaseException:
            ok = False
            lignes.append(f"ECHEC  {nom}\n{traceback.format_exc()}")

    def t_infos():
        return f"python {sys.version.split()[0]}, frozen={getattr(sys, 'frozen', False)}, {sys.platform}"

    def t_aplatir():
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            src = tmp / "essai signé é.pdf"
            d = pymupdf.open()
            p = d.new_page()
            p.insert_text((72, 72), "Rapport de fin de fabrication")
            p.add_stamp_annot(pymupdf.Rect(72, 150, 250, 210), stamp=0)
            w = pymupdf.Widget()
            w.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT
            w.field_name, w.field_value, w.rect = "champ", "Valeur", pymupdf.Rect(72, 250, 300, 275)
            p.add_widget(w)
            d.save(str(src))
            d.close()
            res = core.aplatir_fichier(src, tmp / "sortie")
            assert res.statut == "ok", f"statut={res.statut} {res.message}"
            assert res.dst.name == "[a]- essai signé é.pdf", res.dst.name
            r = pymupdf.open(str(res.dst))
            assert not core.analyser(r).pages, "des éléments interactifs subsistent"
            r.close()
            return res.message

    def t_tkdnd():
        root = TkinterDnD.Tk()
        try:
            root.withdraw()
            root.update()
            version = root.tk.call("package", "require", "tkdnd")
        finally:
            root.destroy()
        return f"tkdnd {version}"

    def t_pystray():
        if pystray is None:
            raise RuntimeError("pystray n'a pas pu être importé")
        icone = pystray.Icon("selftest", dessiner_icone(64), "selftest")
        return type(icone).__module__

    def t_demarrage():
        if sys.platform != "win32":
            return "ignoré (pas Windows)"
        nom = ID_APP + "-selftest"
        assert regler_demarrage(True, nom), "écriture impossible"
        assert demarrage_actif(nom), "valeur absente après écriture"
        assert regler_demarrage(False, nom), "suppression impossible"
        assert not demarrage_actif(nom), "valeur encore présente après suppression"
        return "registre OK"

    def t_instance():
        s = devenir_instance_principale()
        if s is None:
            return "port déjà pris (ignoré)"
        recus = []
        threading.Thread(target=servir_instance, args=(s, recus.append), daemon=True).start()
        try:
            assert prevenir_instance_existante({"cmd": "show"}), "pas de réponse"
        finally:
            s.close()
        assert recus == [{"cmd": "show"}], recus
        return "socket OK"

    for nom, f in (("infos", t_infos), ("aplatissement", t_aplatir), ("tkinterdnd2", t_tkdnd),
                   ("pystray", t_pystray), ("démarrage Windows", t_demarrage), ("instance unique", t_instance)):
        etape(nom, f)
    lignes.append("RESULTAT : " + ("TOUT EST OK" if ok else "ECHEC"))
    try:
        Path(chemin_rapport).write_text("\n".join(lignes) + "\n", encoding="utf-8")
    except OSError:
        pass
    return 0 if ok else 1


# --------------------------------------------------------------------------- #
def main(argv: list | None = None) -> int:
    ap = argparse.ArgumentParser(description="Aplatir PDF - outil de barre des tâches", add_help=False)
    ap.add_argument("--tray", action="store_true")
    ap.add_argument("--selftest", metavar="FICHIER")
    ap.add_argument("fichiers", nargs="*")
    args, _inconnus = ap.parse_known_args(argv)

    if args.selftest:
        return autotest(args.selftest)

    configurer_journal()
    if TkinterDnD is None:
        log.error("tkinterdnd2 introuvable")
        return 1
    fichiers = [os.path.abspath(f) for f in args.fichiers]

    serveur = devenir_instance_principale()
    if serveur is None:        # une instance tourne déjà : on la réveille et on s'efface
        message = {"cmd": "files", "paths": fichiers} if fichiers else {"cmd": "show"}
        if prevenir_instance_existante(message):
            return 0
        log.warning("port occupé mais pas par Aplatir PDF : on continue sans instance unique")

    activer_dpi_windows()
    app = App(cache=args.tray, fichiers=fichiers, serveur=serveur)
    log.info("démarrage (cache=%s, fichiers=%d)", args.tray, len(fichiers))
    app.lancer()
    return 0


if __name__ == "__main__":
    code = main()
    # « Quitter » doit TOUJOURS arrêter le processus : le thread de l'icône de la zone de
    # notification n'est pas un démon et pourrait sinon laisser un processus invisible.
    logging.shutdown()
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(code)
