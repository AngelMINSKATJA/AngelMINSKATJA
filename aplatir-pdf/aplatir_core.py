# -*- coding: utf-8 -*-
"""Aplatir PDF - coeur de traitement (sans interface).

Grave les signatures électroniques, tampons, annotations et champs de formulaire
dans le contenu des pages, pour qu'ils survivent à une fusion de PDF (Acrobat
« Combiner les fichiers » les perd quand ils restent des objets interactifs).

Principe, pour chaque PDF :
  1. on repère les pages qui portent des signatures / annotations / champs ;
  2. s'il n'y en a aucune, le fichier est simplement copié sous son nouveau nom ;
  3. sinon on « grave » (PyMuPDF ``Document.bake``) puis on COMPARE, page par page,
     le rendu avant/après. Si une page a changé d'aspect (signature perdue...),
     elle est remplacée par une image de la page d'origine (mode sécurité) ;
  4. le résultat est écrit de façon atomique sous ``[a]- <nom d'origine>``.
"""
from __future__ import annotations

import os
import re
import shutil
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

import pymupdf
from PIL import Image, ImageChops

PREFIXE = "[a]- "

DPI_RASTER = 200        # résolution des pages converties en image (mode sécurité)
DPI_CONTROLE = 100      # résolution du contrôle visuel avant/après
COTE_MAX_CONTROLE = 3000   # px, plafond du côté le plus long pour le contrôle visuel
COTE_MAX_RASTER = 6000     # px, plafond du côté le plus long pour une page convertie
SEUIL_CANAL = 48        # écart (0-255) à partir duquel un pixel compte comme différent
SEUIL_PIXELS = 12       # nb de pixels différents tolérés avant de juger la page altérée

# Annotations qui ne se voient pas / ne nécessitent pas d'être gravées
TYPES_NEUTRES = {"Link", "Popup", "Widget"}


class ErreurPDF(Exception):
    """Erreur « attendue » : le message est affichable tel quel à l'utilisateur."""


@dataclass
class Analyse:
    pages: dict = field(default_factory=dict)   # n° de page (1 = première) -> [descriptions]
    signatures: int = 0
    # n° de page -> rectangles des champs de signature NON signés. MuPDF y dessine une pastille
    # « SIGN » d'interface qui n'appartient pas au document : on les exclut du contrôle visuel.
    zones_vides: dict = field(default_factory=dict)


@dataclass
class Resultat:
    src: Path
    dst: Path | None = None
    # "ok" aplati | "copie" rien à aplatir | "securite" pages converties en image
    # "alerte" à vérifier | "ignore" | "erreur"
    statut: str = "erreur"
    signatures: int = 0
    pages_elements: list = field(default_factory=list)
    pages_image: list = field(default_factory=list)
    message: str = ""


# --------------------------------------------------------------------------- #
# Fichiers
# --------------------------------------------------------------------------- #
def chemin_sortie(src: Path, dossier_sortie: Path) -> Path:
    return Path(dossier_sortie) / (PREFIXE + Path(src).name)


def _cle_naturelle(p: Path):
    return [int(t) if t.isdigit() else t.casefold() for t in re.split(r"(\d+)", p.name)]


def _est_dans(p: Path, dossier: Path | None) -> bool:
    if dossier is None:
        return False
    try:
        p.resolve().relative_to(Path(dossier).resolve())
        return True
    except (ValueError, OSError):
        return False


def collecter_pdf(chemins: Iterable, exclure: Path | None = None):
    """Développe fichiers et dossiers déposés.

    Retourne ``(pdfs, ignores)`` : ``pdfs`` dans l'ordre donné (le contenu d'un dossier
    est trié par ordre « naturel », sous-dossiers compris), sans doublon ;
    ``ignores`` liste de ``(chemin, raison)``.
    Les fichiers déjà préfixés ``[a]- `` et ceux du dossier de sortie sont écartés.
    """
    pdfs: list[Path] = []
    ignores: list[tuple[Path, str]] = []
    vus: set[str] = set()

    def ajouter(p: Path):
        cle = os.path.normcase(str(p.resolve()))
        if cle not in vus:
            vus.add(cle)
            pdfs.append(p)

    for e in chemins:
        p = Path(e)
        if p.is_dir():
            trouves = sorted(
                (x for x in p.rglob("*") if x.is_file() and x.suffix.lower() == ".pdf"),
                key=lambda x: [_cle_naturelle(Path(part)) for part in x.relative_to(p).parts],
            )
            for x in trouves:
                if x.name.startswith(PREFIXE) or _est_dans(x, exclure):
                    continue
                ajouter(x)
        elif p.is_file() and p.suffix.lower() == ".pdf":
            ajouter(p)
        elif p.exists():
            ignores.append((p, "n'est pas un fichier PDF"))
        else:
            ignores.append((p, "fichier introuvable"))
    return pdfs, ignores


# --------------------------------------------------------------------------- #
# Analyse
# --------------------------------------------------------------------------- #
def analyser(doc) -> Analyse:
    """Pages portant une signature, un champ de formulaire ou une annotation visible."""
    res = Analyse()
    for page in doc:
        elems: list[str] = []
        try:
            for w in page.widgets() or []:
                if w.field_type == pymupdf.PDF_WIDGET_TYPE_SIGNATURE:
                    res.signatures += 1
                    elems.append(f"signature '{w.field_name}'")
                    if not w.is_signed:
                        res.zones_vides.setdefault(page.number + 1, []).append(pymupdf.Rect(w.rect))
                else:
                    elems.append(f"champ {w.field_type_string} '{w.field_name}'")
            for a in page.annots() or []:
                t = a.type[1]
                if t not in TYPES_NEUTRES:
                    elems.append(f"annotation {t}")
        except Exception:
            # page illisible : par prudence on la considère comme « à graver »
            elems.append("éléments illisibles")
        if elems:
            res.pages[page.number + 1] = elems
    return res


# --------------------------------------------------------------------------- #
# Contrôle visuel + conversion en image
# --------------------------------------------------------------------------- #
def _matrice(page, dpi: int, cote_max: int):
    z = dpi / 72.0
    plus_long = max(page.rect.width, page.rect.height) * z
    if plus_long > cote_max:
        z *= cote_max / plus_long
    return pymupdf.Matrix(z, z)


def _pixmap(page, dpi: int, cote_max: int, annots: bool = True):
    return page.get_pixmap(matrix=_matrice(page, dpi, cote_max), annots=annots,
                           alpha=False, colorspace=pymupdf.csRGB)


def _image(pix) -> Image.Image:
    return Image.frombytes("RGB", (pix.width, pix.height), pix.samples)


def _ecart_max(a: Image.Image, b: Image.Image) -> Image.Image:
    """Image en niveaux de gris : pour chaque pixel, le plus grand écart de canal entre a et b."""
    r, g, bl = ImageChops.difference(a, b).split()
    return ImageChops.lighter(ImageChops.lighter(r, g), bl)


def _marque_perdue(orig: Image.Image, apres: Image.Image, fond: Image.Image) -> bool:
    """Vrai si l'aplatissement a PERDU ou déformé quelque chose de visible.

    ``fond`` est la page d'origine rendue SANS annotations. Seuls comptent les pixels où
    l'original portait une marque (``orig`` != ``fond``) que le résultat ne reproduit pas
    (``orig`` != ``apres``). Ce que l'aplatissement AJOUTE (ex. l'apparence d'un champ de
    signature non signé, que MuPDF n'affiche pas mais que l'on veut conserver) n'est pas
    une perte.
    """
    if not (orig.size == apres.size == fond.size):
        return True
    pire = ImageChops.darker(_ecart_max(orig, apres), _ecart_max(orig, fond))
    return sum(pire.histogram()[SEUIL_CANAL + 1:]) >= SEUIL_PIXELS


def _masquer_zones(avant: Image.Image, fond: Image.Image, page, zones) -> None:
    """Dans ``avant``, remplace les zones données (coordonnées page) par le contenu de ``fond``."""
    m = _matrice(page, DPI_CONTROLE, COTE_MAX_CONTROLE)
    for z in zones:
        r = pymupdf.Rect(z) * page.rotation_matrix * m
        boite = (max(0, int(r.x0) - 3), max(0, int(r.y0) - 3),
                 min(avant.width, int(r.x1) + 4), min(avant.height, int(r.y1) + 4))
        if boite[2] > boite[0] and boite[3] > boite[1]:
            avant.paste(fond.crop(boite), boite)


def _pages_alterees(orig, doc, numeros, zones_vides=None) -> list:
    zones_vides = zones_vides or {}
    alterees = []
    for n in numeros:
        try:
            avant = _image(_pixmap(orig[n - 1], DPI_CONTROLE, COTE_MAX_CONTROLE))
            fond = _image(_pixmap(orig[n - 1], DPI_CONTROLE, COTE_MAX_CONTROLE, annots=False))
            apres = _image(_pixmap(doc[n - 1], DPI_CONTROLE, COTE_MAX_CONTROLE))
            if n in zones_vides:
                _masquer_zones(avant, fond, orig[n - 1], zones_vides[n])
            if _marque_perdue(avant, apres, fond):
                alterees.append(n)
        except Exception:
            alterees.append(n)   # impossible de contrôler : par prudence
    return alterees


def _convertir_en_images(doc, orig, numeros, dpi: int) -> None:
    """Remplace les pages listées (1-based) de ``doc`` par le rendu image de ``orig``."""
    toc = None
    try:
        toc = doc.get_toc(simple=False)
    except Exception:
        pass
    for n in numeros:
        pix = _pixmap(orig[n - 1], dpi, COTE_MAX_RASTER)
        ancienne = doc[n - 1]
        rect = ancienne.rect
        liens = []
        try:
            liens = ancienne.get_links()
        except Exception:
            pass
        nouvelle = doc.new_page(pno=n, width=rect.width, height=rect.height)
        nouvelle.insert_image(nouvelle.rect, pixmap=pix)
        for lien in liens:
            try:
                nouvelle.insert_link(lien)
            except Exception:
                pass
        doc.delete_page(n - 1)
    if toc:
        try:
            doc.set_toc(toc)
        except Exception:
            pass


# --------------------------------------------------------------------------- #
# Ouverture / écriture
# --------------------------------------------------------------------------- #
def _ouvrir(src: Path):
    try:
        doc = pymupdf.open(str(src))
    except Exception as e:
        raise ErreurPDF(_message_erreur(e)) from e
    try:
        if not doc.is_pdf:
            raise ErreurPDF("ce fichier n'est pas un vrai PDF")
        if doc.needs_pass and not doc.authenticate(""):
            raise ErreurPDF("PDF protégé par un mot de passe")
        if doc.page_count == 0:
            raise ErreurPDF("PDF vide (aucune page)")
    except Exception:
        doc.close()
        raise
    return doc


def _message_erreur(e: Exception) -> str:
    if isinstance(e, ErreurPDF):
        return str(e)
    nom = type(e).__name__
    if nom in ("EmptyFileError",):
        return "fichier vide"
    if nom in ("FileDataError",):
        return "PDF illisible ou corrompu"
    if nom in ("FileNotFoundError",) or isinstance(e, FileNotFoundError):
        return "fichier introuvable"
    if isinstance(e, PermissionError):
        return ("accès refusé : le fichier de sortie est peut-être ouvert dans un autre "
                "programme (Acrobat ?) ou le dossier est protégé")
    if isinstance(e, OSError):
        return f"erreur disque : {e.strerror or e}"
    return f"{nom} : {e}"


def _ecrire_atomique(dst: Path, ecrire) -> None:
    """Écrit via un fichier temporaire du dossier de sortie, puis remplace ``dst``."""
    dst.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".aplatir-", suffix=".tmp", dir=str(dst.parent))
    os.close(fd)
    try:
        ecrire(tmp)
        os.replace(tmp, dst)
    except BaseException:
        try:
            os.remove(tmp)
        except OSError:
            pass
        raise


def _enregistrer(doc, dst: Path) -> None:
    _ecrire_atomique(dst, lambda tmp: doc.save(tmp, garbage=3, deflate=True))


# --------------------------------------------------------------------------- #
# Traitement d'un fichier
# --------------------------------------------------------------------------- #
def aplatir_fichier(src, dossier_sortie, *, securite: bool = True,
                    dpi_raster: int = DPI_RASTER) -> Resultat:
    """Aplatit ``src`` vers ``dossier_sortie/[a]- <nom>``. Ne lève jamais d'exception."""
    src = Path(src)
    res = Resultat(src=src)
    try:
        _aplatir(src, Path(dossier_sortie), securite, dpi_raster, res)
    except Exception as e:
        res.statut = "erreur"
        res.message = _message_erreur(e)
    return res


def _aplatir(src: Path, dossier: Path, securite: bool, dpi_raster: int, res: Resultat) -> None:
    if src.name.startswith(PREFIXE):
        res.statut = "ignore"
        res.message = "déjà aplati (le nom commence par « [a]- »)"
        return
    if not src.is_file():
        raise ErreurPDF("fichier introuvable")
    dst = chemin_sortie(src, dossier)
    res.dst = dst

    doc = _ouvrir(src)
    orig = None
    try:
        analyse = analyser(doc)
        res.signatures = analyse.signatures
        res.pages_elements = sorted(analyse.pages)
        nb_pages = doc.page_count

        if not analyse.pages:
            doc.close()
            doc = None
            _ecrire_atomique(dst, lambda tmp: shutil.copyfile(src, tmp))
            res.statut = "copie"
            res.message = "rien à aplatir (copie telle quelle)"
            return

        orig = _ouvrir(src)    # exemplaire intact : contrôle visuel + conversion en image
        pages_image: list[int] = []
        alerte = ""
        try:
            doc.bake(annots=True, widgets=True)
        except Exception as e:
            if not securite:
                raise ErreurPDF(f"aplatissement impossible ({e})") from e
            doc.close()
            doc = _ouvrir(src)           # repart d'un document intact
            pages_image = sorted(analyse.pages)
        else:
            alterees = _pages_alterees(orig, doc, sorted(analyse.pages), analyse.zones_vides)
            if alterees and securite:
                pages_image = alterees
            elif alterees:
                alerte = ("rendu différent après aplatissement, page(s) "
                          + ", ".join(map(str, alterees)) + " : à vérifier")

        if pages_image:
            _convertir_en_images(doc, orig, pages_image, dpi_raster)
        res.pages_image = pages_image
        _enregistrer(doc, dst)
    finally:
        if orig is not None:
            orig.close()
        if doc is not None:
            doc.close()

    # contrôle final du fichier écrit
    chk = pymupdf.open(str(dst))
    try:
        restes = analyser(chk).pages
        pages_ok = chk.page_count == nb_pages
    finally:
        chk.close()

    sig = f"{res.signatures} signature(s), " if res.signatures else ""
    base = f"{sig}{len(res.pages_elements)} page(s) traitée(s)"
    if not pages_ok:
        res.statut = "alerte"
        res.message = "le nombre de pages a changé : à vérifier !"
    elif restes:
        res.statut = "alerte"
        res.message = (f"{base} ; éléments encore interactifs page(s) "
                       + ", ".join(map(str, sorted(restes))))
    elif alerte:
        res.statut = "alerte"
        res.message = alerte
    elif res.pages_image:
        res.statut = "securite"
        res.message = (f"{base} ; page(s) {', '.join(map(str, res.pages_image))} "
                       "convertie(s) en image par sécurité")
    else:
        res.statut = "ok"
        res.message = base
