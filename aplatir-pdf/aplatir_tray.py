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
    fichier.pdf ...   PDF à traiter tout de suite (glisser des PDF sur l'icône de l'exe)
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
import hmac
import json
import logging
import logging.handlers
import queue
import re
import secrets
import socket
import subprocess
import tempfile
import threading
import time
import traceback
from pathlib import Path

import tkinter as tk
import tkinter.font as tkfont
from tkinter import filedialog, messagebox, ttk

import aplatir_core as core
from aplatir_icon import dessiner_icone, icone_png_base64

try:
    from _version import VERSION      # écrit par build.py (numéro de fabrication)
except Exception:
    VERSION = "dev"

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
PORT_ANCIEN = 47653      # port fixe réservé par les toutes premières versions
FICHIER_VERROU = "instance.lock"
FICHIER_INSTANCE = "instance.json"
FICHIER_SORTIES = "sorties.json"
CLE_RUN = r"Software\Microsoft\Windows\CurrentVersion\Run"
CLE_APPROUVE = r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
DEFAUTS = {"sortie": "", "demarrage_auto": True, "premier_plan": True, "securite": True,
           "astuce_vue": False}

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


def charger_reserves() -> dict:
    """Noms de sortie déjà attribués (chemin de sortie -> source), mémorisés d'un lancement à
    l'autre : sans cela, après un redémarrage, un autre « Rapport.pdf » écraserait la sortie
    du précédent. Aucun test de disque ici (dossier réseau pas encore reconnecté au démarrage de
    Windows, lecteur amovible...) : l'élagage se fait plus tard, à la lecture du dossier."""
    try:
        data = json.loads((dossier_config() / FICHIER_SORTIES).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    if not isinstance(data, dict):
        return {}
    return {k: v for k, v in data.items() if isinstance(k, str) and isinstance(v, str)}


def sauver_reserves(reserves: dict) -> None:
    try:
        d = dossier_config()
        d.mkdir(parents=True, exist_ok=True)
        recentes = dict(list(reserves.items())[-5000:])
        tmp = d / (FICHIER_SORTIES + ".tmp")
        tmp.write_text(json.dumps(recentes, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, d / FICHIER_SORTIES)
    except OSError:
        log.warning("impossible d'enregistrer les noms de sortie", exc_info=True)


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
def exe_dans_un_dossier_temporaire() -> bool:
    """Vrai si l'exe tourne depuis un dossier temporaire, par exemple depuis l'aperçu d'un .zip
    (``%TEMP%\\Temp1_xxx.zip\\``) : y inscrire le démarrage automatique le perdrait au nettoyage."""
    if not getattr(sys, "frozen", False):
        return False
    exe = os.path.normcase(os.path.abspath(sys.executable))
    tmp = os.path.normcase(os.path.abspath(tempfile.gettempdir()))
    return exe.startswith(tmp + os.sep) or bool(re.search(r"[\\/]temp\d+_[^\\/]*\.zip[\\/]", exe, re.IGNORECASE))


def commande_demarrage() -> str:
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}" --tray'
    pythonw = Path(sys.executable).with_name("pythonw.exe")
    interpreteur = pythonw if pythonw.exists() else Path(sys.executable)
    return f'"{interpreteur}" "{Path(__file__).resolve()}" --tray'


def _veto_windows(nom: str = ID_APP) -> bool:
    """Windows (Gestionnaire des tâches > Démarrage, Paramètres > Applications > Démarrage)
    désactive une entrée SANS toucher à la clé « Run » : il l'écrit dans StartupApproved
    (premier octet impair = désactivée)."""
    if sys.platform != "win32":
        return False
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, CLE_APPROUVE) as cle:
            valeur, _ = winreg.QueryValueEx(cle, nom)
    except OSError:
        return False
    return isinstance(valeur, (bytes, bytearray)) and len(valeur) > 0 and (valeur[0] & 1) == 1


def demarrage_actif(nom: str = ID_APP) -> bool:
    """Vrai si l'outil démarrera réellement avec Windows (entrée présente ET non désactivée)."""
    if sys.platform != "win32":
        return False
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, CLE_RUN) as cle:
            winreg.QueryValueEx(cle, nom)
    except OSError:
        return False
    return not _veto_windows(nom)


def regler_demarrage(actif: bool, nom: str = ID_APP, lever_veto: bool = True) -> bool:
    """Ajoute/retire l'entrée de démarrage. Retourne True si l'opération a réussi.

    ``lever_veto`` : en activant, supprime aussi la désactivation faite côté Windows (sinon
    recocher la case ne ferait rien). Le rafraîchissement automatique du chemin de l'exe, au
    lancement, passe ``lever_veto=False`` pour respecter un choix délibéré de l'utilisateur.
    """
    if sys.platform != "win32":
        return False
    if actif and exe_dans_un_dossier_temporaire():
        log.warning("démarrage automatique non modifié : l'exe tourne depuis un dossier temporaire")
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
    except OSError:
        log.warning("démarrage automatique : accès au registre refusé", exc_info=True)
        return False
    if actif and lever_veto:
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, CLE_APPROUVE, 0, winreg.KEY_SET_VALUE) as cle:
                winreg.DeleteValue(cle, nom)
        except OSError:
            pass            # pas de veto : rien à lever
    return True


# --------------------------------------------------------------------------- #
# Instance unique, PAR PROFIL Windows : verrou de fichier (exclusif, relâché automatiquement
# à la mort du processus) + petit serveur local authentifié par un jeton, pour réveiller
# l'instance déjà lancée, lui passer des fichiers, ou lui demander de se fermer quand on
# lance une autre version de l'exe (mise à jour).
# --------------------------------------------------------------------------- #
def prendre_verrou(dossier: Path | None = None):
    """Retourne le fichier verrouillé (à garder ouvert), ou None si une autre instance du même
    profil le détient. Lève OSError si le verrou ne peut pas être créé (dossier inaccessible)."""
    d = Path(dossier) if dossier else dossier_config()
    d.mkdir(parents=True, exist_ok=True)
    f = open(d / FICHIER_VERROU, "a+b")
    try:
        if sys.platform == "win32":
            import msvcrt
            f.seek(0)
            msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(f.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        f.close()
        return None
    return f


def ouvrir_serveur(dossier: Path | None = None):
    """Ouvre le serveur local (port choisi par le système, 127.0.0.1 seulement) et publie
    ``instance.json`` (port + jeton) dans le dossier du profil. Retourne ``(socket, jeton)``."""
    d = Path(dossier) if dossier else dossier_config()
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    s.listen(5)
    jeton = secrets.token_hex(16)
    info = {"port": s.getsockname()[1], "jeton": jeton, "version": VERSION, "pid": os.getpid()}
    try:
        d.mkdir(parents=True, exist_ok=True)
        tmp = d / (FICHIER_INSTANCE + ".tmp")
        tmp.write_text(json.dumps(info), encoding="utf-8")
        for essai in range(6):          # un antivirus peut tenir le fichier un instant
            try:
                os.replace(tmp, d / FICHIER_INSTANCE)
                break
            except PermissionError:
                if essai == 5:
                    raise
                time.sleep(0.1 * (essai + 1))
    except OSError:
        s.close()
        raise
    return s, jeton


def oublier_serveur(jeton: str | None, dossier: Path | None = None) -> None:
    """Retire ``instance.json`` s'il décrit CETTE instance."""
    d = Path(dossier) if dossier else dossier_config()
    try:
        info = json.loads((d / FICHIER_INSTANCE).read_text(encoding="utf-8"))
        if jeton and info.get("jeton") == jeton:
            (d / FICHIER_INSTANCE).unlink()
    except (OSError, ValueError):
        pass


def contacter_instance(message: dict, dossier: Path | None = None, attente: float = 0.0):
    """Envoie ``message`` à l'instance du profil. Retourne sa réponse (« OK » traité, « AUTRE »
    autre version, « OCCUPE » traitement en cours, « NON » jeton refusé) ou None si personne
    n'a répondu dans le délai ``attente`` (secondes)."""
    d = Path(dossier) if dossier else dossier_config()
    fin = time.monotonic() + attente
    while True:
        try:
            info = json.loads((d / FICHIER_INSTANCE).read_text(encoding="utf-8"))
            msg = dict(message, jeton=info["jeton"], version=VERSION)
            with socket.create_connection(("127.0.0.1", int(info["port"])), timeout=3) as c:
                c.sendall((json.dumps(msg) + "\n").encode("utf-8"))
                c.shutdown(socket.SHUT_WR)
                rep = c.recv(16).decode("ascii", "replace").strip()
            if rep in ("OK", "AUTRE", "OCCUPE", "NON"):
                return rep
        except (OSError, ValueError, KeyError, TypeError):
            pass
        if time.monotonic() >= fin:
            return None
        time.sleep(0.2)


def servir_instance(serveur: socket.socket, jeton: str, poster, occupe=lambda: False,
                    version: str | None = None) -> None:
    ma_version = VERSION if version is None else version
    serveur.settimeout(1.0)
    while True:
        try:
            c, _ = serveur.accept()
        except socket.timeout:
            continue
        except OSError:
            if serveur.fileno() == -1:      # fermé volontairement
                return
            time.sleep(0.1)                 # erreur passagère (ex. connexion annulée) : on continue
            continue
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
                if not isinstance(msg, dict):
                    continue
                if not hmac.compare_digest(str(msg.get("jeton", "")), jeton):
                    c.sendall(b"NON")
                elif msg.get("cmd") == "quit":
                    if occupe():
                        c.sendall(b"OCCUPE")
                    else:
                        poster({"cmd": "quit"})
                        c.sendall(b"OK")
                elif msg.get("version") != ma_version:
                    c.sendall(b"AUTRE")
                else:
                    poster({k: v for k, v in msg.items() if k not in ("jeton", "version")})
                    c.sendall(b"OK")
            except Exception:
                log.warning("message d'instance invalide", exc_info=True)


def version_publiee(dossier: Path | None = None) -> str:
    """Version de l'instance qui tourne, telle qu'elle l'a publiée dans ``instance.json``."""
    d = Path(dossier) if dossier else dossier_config()
    try:
        return str(json.loads((d / FICHIER_INSTANCE).read_text(encoding="utf-8")).get("version", ""))
    except (OSError, ValueError, AttributeError):
        return ""


def instance_plus_recente(ma_version: str, autre: str) -> bool:
    """Vrai si l'instance qui tourne (``autre``) est plus récente que moi. Les versions de la CI sont
    « numéro de fabrication-commit » ; sans numéro (« dev », « local-… ») on ne présume de rien."""
    m1, m2 = re.match(r"^(\d+)-", ma_version or ""), re.match(r"^(\d+)-", autre or "")
    return bool(m1 and m2 and int(m2.group(1)) > int(m1.group(1)))


def ancienne_version_active() -> bool:
    """Les toutes premières versions réservaient le port fixe 47653 et répondaient OK à tout :
    on le détecte pour demander de les fermer plutôt que d'ouvrir une 2e icône."""
    try:
        with socket.create_connection(("127.0.0.1", PORT_ANCIEN), timeout=1) as c:
            c.sendall(b'{"cmd": "show"}\n')
            c.shutdown(socket.SHUT_WR)
            return c.recv(16).startswith(b"OK")
    except OSError:
        return False


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
    "securite": ("✔ Aplati (image)", "image"),
    "alerte": ("⚠ À vérifier", "alerte"),
    "ignore": ("— Ignoré", "ignore"),
    "erreur": ("✖ Erreur", "erreur"),
}


# --------------------------------------------------------------------------- #
# Application
# --------------------------------------------------------------------------- #
class App:
    def __init__(self, cache: bool, fichiers: list, serveur: socket.socket | None, jeton: str | None = None):
        self.cfg = Config()
        premiere_fois = not self.cfg.charger()
        if getattr(sys, "frozen", False) and not exe_dans_un_dossier_temporaire():   # chemin de l'exe à jour
            regler_demarrage(self.cfg["demarrage_auto"], lever_veto=False)
            if sys.platform == "win32":            # reflète un éventuel choix fait côté Windows
                self.cfg["demarrage_auto"] = demarrage_actif()
        if premiere_fois:
            self.cfg.sauver()
        self.jeton = jeton
        self.reserves: dict = charger_reserves()    # noms de sortie déjà attribués (mémorisés)
        self.sources: dict = {}                     # ligne de la liste -> chemin complet de la source
        self._ferme = False

        self.evenements: queue.Queue = queue.Queue()   # tout ce qui doit toucher à l'interface
        self.jobs: queue.Queue = queue.Queue()
        self.en_cours = 0
        self.stats = self._stats_vides()
        self.visible = False
        self.icone = None

        self.root = TkinterDnD.Tk()
        self.k = self.root.winfo_fpixels("1i") / 96.0
        self.root.report_callback_exception = self._erreur_tk
        self._construire()
        self.root.protocol("WM_DELETE_WINDOW", self.fermer_fenetre)

        threading.Thread(target=self._boucle_travail, daemon=True).start()
        if serveur is not None:
            threading.Thread(target=servir_instance,
                             args=(serveur, jeton or "", self._message_instance, lambda: self.en_cours > 0),
                             daemon=True).start()
        self._demarrer_icone()
        self.root.after(100, self._pomper)

        if cache and not premiere_fois and self.icone is not None:
            self.root.withdraw()
        else:
            self.afficher()
        if fichiers:
            self.root.after(400, lambda: self.ajouter(fichiers))

    @staticmethod
    def _stats_vides() -> dict:
        return {"ok": 0, "copie": 0, "image": 0, "alerte": 0, "erreur": 0, "ignore": 0}

    # ----- interface ------------------------------------------------------- #
    def _px(self, v: int) -> int:
        return int(v * self.k)

    def _construire(self) -> None:
        r = self.root
        r.title(NOM_APP if VERSION == "dev" else f"{NOM_APP} — version {VERSION}")
        # fenêtre jamais plus grande que l'écran (écran très zoomé : boutons sinon hors de l'écran)
        larg = min(self._px(900), r.winfo_screenwidth() - self._px(40))
        haut = min(self._px(580), r.winfo_screenheight() - self._px(120))
        r.geometry(f"{larg}x{haut}")
        r.minsize(min(self._px(560), larg), min(self._px(440), haut))
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
        # lignes assez hautes pour la police à l'échelle d'affichage choisie (Tk fixe 20 px sinon)
        hauteur = tkfont.nametofont("TkDefaultFont", r).metrics("linespace") + self._px(6)
        ttk.Style(r).configure("Treeview", rowheight=max(self._px(20), hauteur))
        self.arbre = ttk.Treeview(cadre, columns=("fichier", "statut", "detail"),
                                  show="headings", selectmode="extended")
        for col, texte, largeur, etire in (("fichier", "Fichier", 420, True),
                                           ("statut", "Résultat", 130, False),
                                           ("detail", "Détail", 300, True)):
            self.arbre.heading(col, text=texte)
            self.arbre.column(col, width=self._px(largeur), stretch=etire)
        self.arbre.tag_configure("ok", foreground="#1a7f37")
        self.arbre.tag_configure("alerte", foreground="#b35900")
        self.arbre.tag_configure("image", foreground="#1f4e8c")
        self.arbre.tag_configure("erreur", foreground="#c62828")
        self.arbre.tag_configure("ignore", foreground="#6b7280")
        self.arbre.tag_configure("attente", foreground="#6b7280")
        self.arbre.grid(row=0, column=0, sticky="nsew")
        asc = ttk.Scrollbar(cadre, orient="vertical", command=self.arbre.yview)
        asc.grid(row=0, column=1, sticky="ns")
        self.arbre.configure(yscrollcommand=asc.set)
        self.arbre.bind("<<TreeviewSelect>>", self._montrer_detail)
        self.arbre.bind("<ButtonRelease-1>", self._montrer_detail)     # un 2e clic réaffiche le texte
        self.arbre.bind("<Return>", self._montrer_detail)

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
        fond = ttk.Style(r).lookup("TFrame", "background") or r.cget("bg")
        lab = tk.Label(main, textvariable=self.var_statut, anchor="nw", justify="left", height=3,
                       fg="#444", bg=fond)
        lab.grid(row=5, column=0, sticky="ew", pady=(self._px(8), 0))
        lab.bind("<Configure>", lambda e: lab.configure(wraplength=max(100, e.width - 8)))   # retour à la ligne

        # glisser-déposer : on enregistre la fenêtre et les zones principales
        for w in (self.root, self.zone, self.arbre):
            w.drop_target_register(DND_FILES)
            w.dnd_bind("<<Drop>>", self._on_drop)
        self.zone.dnd_bind("<<DropEnter>>", self._on_drop_enter)
        self.zone.dnd_bind("<<DropLeave>>", self._on_drop_leave)
        self._maj_premier_plan()

    def _montrer_detail(self, _evenement=None) -> None:
        """Une ligne sélectionnée : son texte complet (la colonne « Détail » coupe les longs messages)."""
        sel = self.arbre.selection()
        if len(sel) != 1 or not self.arbre.exists(sel[0]):
            return
        fichier, statut, detail = self.arbre.item(sel[0], "values")[:3]
        source = self.sources.get(sel[0])
        self.var_statut.set(f"{statut} : {detail}\n{source or fichier}")      # l'important d'abord, le chemin ensuite

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
        """Ne fait que relever les chemins : le traitement est différé (``after``) car l'appel
        OLE de dépôt est synchrone et bloquerait l'Explorateur pendant l'analyse d'un dossier
        ou l'affichage d'une boîte de dialogue."""
        self.zone.configure(bg="#eef3fb")
        try:
            chemins = list(self.root.tk.splitlist(event.data))
        except Exception:
            self._erreur_tk(*sys.exc_info())
            return event.action
        if not chemins:
            self.var_statut.set("Ce glisser-déposer n'est pas pris en charge (pièce jointe d'e-mail ?) : "
                                "enregistrez d'abord le fichier sur le Bureau, puis glissez-le.")
        else:
            self.root.after(20, lambda: self._ajouter_sans_risque(chemins))
        return event.action

    def _ajouter_sans_risque(self, chemins: list) -> None:
        try:
            self.ajouter(chemins)
        except Exception:
            self._erreur_tk(*sys.exc_info())

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
            self.masquer()          # la croix réduit dans la zone de notification
            if not self.cfg["astuce_vue"]:
                self.cfg["astuce_vue"] = True
                self.notifier("Aplatir PDF reste actif près de l'horloge (flèche ^ sous Windows 11). "
                              "Cliquez sur son icône pour rouvrir la fenêtre.")
        else:
            self.quitter()

    def _maj_premier_plan(self) -> None:
        self.cfg["premier_plan"] = bool(self.var_premier.get())
        self.root.attributes("-topmost", bool(self.cfg["premier_plan"]))

    def _maj_securite(self) -> None:
        self.cfg["securite"] = bool(self.var_securite.get())

    def definir_demarrage(self, actif: bool) -> None:
        if actif and exe_dans_un_dossier_temporaire():
            messagebox.showinfo(NOM_APP, "L'exe tourne depuis un dossier temporaire (aperçu d'un .zip ?).\n\n"
                                "Copiez d'abord AplatirPDF.exe dans un dossier de votre choix, lancez-le "
                                "de là, puis cochez cette case.")
            self.var_demarrage.set(bool(self.cfg["demarrage_auto"]))
            return
        ok = regler_demarrage(actif)
        if not ok and sys.platform == "win32":
            messagebox.showwarning(NOM_APP, "Impossible de modifier le démarrage automatique.")
        if sys.platform == "win32":
            actif = demarrage_actif()       # état réel (tient compte d'un blocage côté Windows)
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
            self._poster("afficher")        # sinon un dépôt sur l'exe se fait sans aucun retour visible
            self._poster("fichiers", [str(p) for p in msg.get("paths", [])])
        elif msg.get("cmd") == "quit":      # une autre version de l'exe demande la place
            self._poster("quitter_force")
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
            while not self._ferme:
                self._evenement(self.evenements.get_nowait())
        except queue.Empty:
            pass
        except Exception:
            log.error("erreur dans la boucle d'événements", exc_info=True)
        finally:
            try:
                if not self._ferme:
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
        elif nom == "quitter_force":
            self.quitter(confirmer=False)
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
        pdfs, ignores = core.collecter_pdf(chemins)
        if self.en_cours == 0:
            self.stats = self._stats_vides()
            self._elaguer_reserves(sortie)
        for p, raison in ignores:
            iid = self.arbre.insert("", "end", values=(p.name, LIBELLES["ignore"][0], raison), tags=("ignore",))
            self.arbre.see(iid)
            self.stats["ignore"] += 1
        if not pdfs:
            if ignores:
                self.var_statut.set(f"Aucun PDF à traiter : {len(ignores)} élément(s) ignoré(s).")
            else:
                self.var_statut.set("Aucun PDF dans ce qui a été déposé.")
            return
        for p in pdfs:
            dst = core.chemin_sortie_unique(p, sortie, self.reserves)   # 2 sources de même nom : « (2) »
            iid = self.arbre.insert("", "end", values=(p.name, "⏳ En attente", str(p.parent)), tags=("attente",))
            self.sources[iid] = p
            self.arbre.see(iid)
            self.en_cours += 1
            self.jobs.put((iid, p, sortie, bool(self.cfg["securite"]), dst))
        sauver_reserves(self.reserves)
        self.var_statut.set(f"{self.en_cours} fichier(s) en cours de traitement…")

    def _elaguer_reserves(self, sortie: Path) -> None:
        """Oublie les noms réservés dont le fichier a disparu du dossier de sortie (une seule lecture
        du dossier, rien en attente). Dossier injoignable : on ne touche à rien."""
        try:
            presents = {os.path.normcase(str(sortie / n)) for n in os.listdir(sortie)}
        except OSError:
            return
        racine = os.path.normcase(str(sortie)) + os.sep
        for k in [k for k in self.reserves if k.startswith(racine) and k not in presents]:
            del self.reserves[k]

    def _boucle_travail(self) -> None:
        while True:
            job = self.jobs.get()
            if job is None:
                return
            iid, src, sortie, securite, *reste = job
            self._poster("debut", iid)
            try:
                res = core.aplatir_fichier(src, sortie, securite=securite, dst=reste[0] if reste else None)
            except Exception as e:      # ne devrait pas arriver : aplatir_fichier ne lève pas
                res = core.Resultat(src=src, statut="erreur", message=f"{type(e).__name__} : {e}")
            self._poster("fin", iid, res)

    def _fin(self, iid: str, res: "core.Resultat") -> None:
        self.en_cours = max(0, self.en_cours - 1)
        texte, tag = LIBELLES.get(res.statut, LIBELLES["erreur"])
        detail = res.message
        if res.dst is not None and res.statut in ("ok", "copie", "securite", "alerte") \
                and res.dst.name != core.PREFIXE + res.src.name:
            detail = f"→ {res.dst.name} ; {res.message}"      # le nom habituel va de soi : on ne signale que « (2) »
        if self.arbre.exists(iid):
            self.arbre.item(iid, values=(res.src.name, texte, detail), tags=(tag,))
        cle = {"ok": "ok", "copie": "copie", "securite": "image", "alerte": "alerte",
               "ignore": "ignore"}.get(res.statut, "erreur")
        self.stats[cle] += 1
        log.info("%s | %s | %s | signatures=%s pages=%s image=%s", res.statut, res.src, detail,
                 res.signatures, res.pages_elements, res.pages_image)
        if self.en_cours:
            self.var_statut.set(f"{self.en_cours} fichier(s) en cours de traitement…")
        else:
            self._lot_termine()

    def _lot_termine(self) -> None:
        s = self.stats
        morceaux = [f"{s['ok']} aplati(s)"]
        if s["copie"]:
            morceaux.append(f"{s['copie']} copié(s) (rien à aplatir)")
        if s["image"]:
            morceaux.append(f"{s['image']} avec page(s) convertie(s) en image")
        if s["alerte"]:
            morceaux.append(f"{s['alerte']} à vérifier")
        if s["erreur"]:
            morceaux.append(f"{s['erreur']} erreur(s)")
        if s["ignore"]:
            morceaux.append(f"{s['ignore']} ignoré(s)")
        msg = "Terminé : " + ", ".join(morceaux) + "."
        self.var_statut.set(msg)
        problemes = [i for i in self.arbre.get_children() if {"alerte", "erreur"} & set(self.arbre.item(i, "tags"))]
        if problemes:
            self.arbre.see(problemes[0])        # une longue liste défile jusqu'à la 1re ligne à regarder
        self.notifier(msg)

    def quitter(self, confirmer: bool = True) -> None:
        if self._ferme:                     # 2e demande (ex. « Quitter » + demande d'une autre version)
            return
        if confirmer and self.en_cours and not messagebox.askyesno(
                NOM_APP, "Un aplatissement est en cours.\nQuitter quand même ?", parent=self.root):
            return
        self._ferme = True
        oublier_serveur(self.jeton)
        try:
            if self.icone is not None:
                self.icone.visible = False      # retire l'icône tout de suite (sinon fantôme jusqu'au survol)
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
        with tempfile.TemporaryDirectory() as d:
            d = Path(d)
            verrou = prendre_verrou(d)
            assert verrou is not None, "verrou refusé"
            assert prendre_verrou(d) is None, "2e verrou accepté"
            serveur, jeton = ouvrir_serveur(d)
            recus: list = []
            threading.Thread(target=servir_instance, args=(serveur, jeton, recus.append),
                             daemon=True).start()
            try:
                assert contacter_instance({"cmd": "show"}, d, attente=3) == "OK", "pas de réponse"
            finally:
                serveur.close()
                verrou.close()
            assert recus == [{"cmd": "show"}], recus
        return "verrou + socket OK"

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
def message_info(texte: str) -> None:
    """Petite boîte de dialogue sans fenêtre principale (l'application n'est pas démarrée).
    ``APLATIR_SANS_DIALOGUE=1`` la remplace par une ligne de journal (tests, intégration continue)."""
    if os.environ.get("APLATIR_SANS_DIALOGUE"):
        log.info("message : %s", texte)
        return
    try:
        racine = tk.Tk()
        racine.withdraw()
        messagebox.showinfo(NOM_APP, texte, parent=racine)
        racine.destroy()
    except Exception:
        log.warning("boîte de dialogue impossible", exc_info=True)


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

    if ancienne_version_active():       # la 1re version (port fixe) tourne encore : elle montre sa fenêtre
        message_info("Une ancienne version d'Aplatir PDF est déjà lancée (icône près de l'horloge).\n\n"
                     "Fermez-la d'abord (clic droit sur son icône, puis Quitter), puis relancez "
                     "cette nouvelle version.")
        return 0

    verrou = None
    try:
        verrou = prendre_verrou()
    except OSError:
        log.warning("verrou d'instance impossible : on continue sans instance unique", exc_info=True)
        verrou = False                  # distinct de None : pas de négociation possible
    if verrou is None:                  # une instance de ce profil tourne déjà : on négocie, de façon bornée
        message = {"cmd": "files", "paths": fichiers} if fichiers else {"cmd": "show"}
        fin = time.monotonic() + 15
        quit_demande = False
        premier_tour = True
        while True:
            rep = contacter_instance(message, attente=4.0 if premier_tour else 0.5)
            premier_tour = False
            if rep == "OK":
                return 0
            if rep == "AUTRE" and not quit_demande:
                autre = version_publiee()
                if instance_plus_recente(VERSION, autre):
                    message_info(f"Une version plus récente d'Aplatir PDF (version {autre}) est déjà lancée "
                                 "(icône près de l'horloge).\nVous venez d'ouvrir un ancien exe : "
                                 "supprimez-le et utilisez le plus récent.")
                    return 0
                demande = contacter_instance({"cmd": "quit"}, attente=2.0)
                if demande == "OCCUPE":
                    message_info("Une autre version d'Aplatir PDF est en train de traiter des fichiers.\n"
                                 "Réessayez dans un instant.")
                    return 0
                quit_demande = demande == "OK"
            try:
                verrou = prendre_verrou()   # l'ancienne instance a-t-elle libéré la place ?
            except OSError:
                verrou = False
                break
            if verrou is not None:
                break
            if time.monotonic() > fin:
                log.warning("une instance détient le verrou mais ne répond pas (réponse : %s) : on continue", rep)
                break
            time.sleep(0.3)

    serveur = jeton = None
    try:
        serveur, jeton = ouvrir_serveur()
    except OSError:
        log.warning("serveur d'instance indisponible", exc_info=True)

    activer_dpi_windows()
    app = App(cache=args.tray, fichiers=fichiers, serveur=serveur, jeton=jeton)
    log.info("démarrage version %s (cache=%s, fichiers=%d)", VERSION, args.tray, len(fichiers))
    app.lancer()
    del verrou                          # gardé ouvert jusqu'ici : il relâche le verrou en se fermant
    return 0


if __name__ == "__main__":
    code = 1
    try:
        code = main()
    except BaseException:
        log.error("erreur fatale\n%s", traceback.format_exc())
        message_info("Aplatir PDF a rencontré une erreur et doit fermer.\n"
                     f"Détails dans le journal : {dossier_config() / 'aplatir.log'}")
    finally:
        # « Quitter » doit TOUJOURS arrêter le processus : le thread de l'icône de la zone de
        # notification n'est pas un démon et pourrait sinon laisser un processus invisible.
        logging.shutdown()
        for flux in (sys.stdout, sys.stderr):
            try:
                flux.flush()
            except Exception:
                pass
        os._exit(code)
