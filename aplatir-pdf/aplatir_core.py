# -*- coding: utf-8 -*-
"""Aplatir PDF - coeur de traitement (sans interface).

Grave les signatures électroniques, tampons, annotations et champs de formulaire
dans le contenu des pages, pour qu'ils survivent à une fusion de PDF (Acrobat
« Combiner les fichiers » les perd quand ils restent des objets interactifs).

Principe, pour chaque PDF :
  1. on repère les pages qui portent des signatures / annotations / champs ;
  2. s'il n'y en a aucune, le fichier est simplement copié sous son nouveau nom ;
  3. sinon on « grave » (PyMuPDF ``Document.bake``) puis on COMPARE, page par page,
     le rendu avant/après. Si une page a changé d'aspect (signature perdue, objet masqué
     devenu visible...), elle est remplacée par une image de la page d'origine ;
  4. le résultat est écrit de façon atomique sous ``[a]- <nom d'origine>``, puis le
     fichier ÉCRIT est relu et recontrôlé (page par page) avant de le déclarer « ok ».
"""
from __future__ import annotations

import os
import re
import shutil
import sys
import tempfile
import time
import zlib
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
ESSAIS_REMPLACEMENT = 6    # antivirus / OneDrive peuvent verrouiller le fichier quelques instants
MAX_PAGES_AFFICHEES = 8

# Annotations qui ne se voient pas / ne nécessitent pas d'être gravées
TYPES_NEUTRES = {"Link", "Popup", "Widget"}


class ErreurPDF(Exception):
    """Erreur « attendue » : le message est affichable tel quel à l'utilisateur."""


@dataclass
class Analyse:
    pages: dict = field(default_factory=dict)   # n° de page (1 = première) -> [descriptions]
    signatures: int = 0                         # champs de signature SIGNÉS
    vides: int = 0                              # champs de signature non signés
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


def _cle(p: Path) -> str:
    try:
        return os.path.normcase(str(p.resolve()))
    except OSError:
        return os.path.normcase(str(p.absolute()))


def chemin_sortie_unique(src: Path, dossier_sortie: Path, reserves: dict) -> Path:
    """Nom de sortie qui ne sert qu'à CETTE source.

    Deux sources de même nom (``OF-1\\Rapport.pdf`` et ``OF-2\\Rapport.pdf``) ne doivent pas
    s'écraser : la 2e devient ``[a]- Rapport (2).pdf``. ``reserves`` est un dict partagé
    (chemin de sortie -> source) que l'appelant garde le temps de la session ; redéposer
    la MÊME source retrouve le même nom (le fichier est alors simplement remplacé).
    """
    cle_src = _cle(Path(src))
    base = chemin_sortie(src, dossier_sortie)
    cand, n = base, 1
    while True:
        k = os.path.normcase(str(cand))
        proprietaire = reserves.get(k)
        if proprietaire is None or proprietaire == cle_src:
            reserves[k] = cle_src
            return cand
        n += 1
        cand = base.with_name(f"{base.stem} ({n}){base.suffix}")


def _cle_naturelle(texte: str):
    return [int(t) if t.isdecimal() else t.casefold() for t in re.split(r"(\d+)", texte)]


def _est_pdf(p: Path) -> bool:
    try:
        return p.is_file() and p.suffix.lower() == ".pdf"
    except OSError:
        return False


def collecter_pdf(chemins: Iterable):
    """Développe fichiers et dossiers déposés.

    Retourne ``(pdfs, ignores)`` : ``pdfs`` dans l'ordre donné (le contenu d'un dossier
    est trié par ordre « naturel », sous-dossiers compris), sans doublon ;
    ``ignores`` liste de ``(chemin, raison)``. Les fichiers dont le nom commence déjà par
    ``[a]- `` (sorties de cet outil) sont écartés. Ne lève jamais d'exception.
    """
    pdfs: list[Path] = []
    ignores: list[tuple[Path, str]] = []
    vus: set[str] = set()

    def ajouter(p: Path):
        cle = _cle(p)
        if cle not in vus:
            vus.add(cle)
            pdfs.append(p)

    for e in chemins:
        p = Path(e)
        try:
            est_dossier = p.is_dir()
        except OSError as err:
            ignores.append((p, f"illisible : {err.strerror or err}"))
            continue
        if est_dossier:
            erreurs: list = []
            trouves: list[Path] = []
            for racine, _dossiers, noms in os.walk(p, onerror=erreurs.append):
                for nom in noms:
                    if Path(nom).suffix.lower() == ".pdf":
                        trouves.append(Path(racine) / nom)
            for err in erreurs:
                ignores.append((Path(getattr(err, "filename", None) or p),
                                f"dossier illisible : {getattr(err, 'strerror', None) or err}"))
            trouves.sort(key=lambda x: [_cle_naturelle(part) for part in x.relative_to(p).parts])
            deja = 0
            for x in trouves:
                if x.name.startswith(PREFIXE):
                    deja += 1
                else:
                    ajouter(x)
            if deja:
                ignores.append((p, f"{deja} fichier(s) déjà aplati(s) (« {PREFIXE.strip()} ») ignoré(s)"))
        elif _est_pdf(p):
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
        n = page.number + 1
        elems: list[str] = []
        try:
            for w in page.widgets() or []:
                if w.field_type == pymupdf.PDF_WIDGET_TYPE_SIGNATURE:
                    if w.is_signed:
                        res.signatures += 1
                        elems.append(f"signature '{w.field_name}'")
                    else:
                        res.vides += 1
                        elems.append(f"champ de signature vide '{w.field_name}'")
                        res.zones_vides.setdefault(n, []).append(pymupdf.Rect(w.rect))
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
            res.pages[n] = elems
    return res


def _a_des_restrictions(doc) -> bool:
    """PDF chiffré (mot de passe « propriétaire » seul : il s'ouvre sans mot de passe mais peut
    interdire l'assemblage). ``Document.is_encrypted`` vaut alors False : on lit /Encrypt."""
    try:
        return doc.xref_get_key(-1, "Encrypt")[0] != "null"
    except Exception:
        return False


def _xfa_present(doc) -> bool:
    try:
        return doc.xref_get_key(doc.pdf_catalog(), "AcroForm/XFA")[0] != "null"
    except Exception:
        return False


def _normaliser_pour_rendu(doc) -> None:
    """Retouches EN MÉMOIRE, appliquées à l'exemplaire à aplatir ET à l'exemplaire de référence.

    Elles contournent des défauts d'affichage de MuPDF qui feraient graver une valeur fausse
    ou une marque parasite (les champs sont de toute façon supprimés après gravure) :
      - champ de signature vide sans apparence : MuPDF dessine une pastille « SIGN » d'interface
        et ``bake`` la grave dans la page -> on le masque ;
      - champ avec /RV (texte riche) sans apparence : MuPDF n'affiche rien -> on retire /RV ;
      - liste / menu déroulant à options [export, libellé] : MuPDF affiche la valeur d'export
        au lieu du libellé -> on met le libellé comme valeur.
    """
    for page in doc:
        try:
            widgets = list(page.widgets() or [])
        except Exception:
            continue
        for w in widgets:
            try:
                _normaliser_champ(doc, w)
            except Exception:
                pass


def _normaliser_champ(doc, w) -> None:
    sans_ap = doc.xref_get_key(w.xref, "AP/N")[0] == "null"
    if w.field_type == pymupdf.PDF_WIDGET_TYPE_SIGNATURE:
        if sans_ap and not w.is_signed:
            f = doc.xref_get_key(w.xref, "F")
            actuel = int(f[1]) if f[0] == "int" else 0
            doc.xref_set_key(w.xref, "F", str(actuel | 2))       # bit « Hidden » : bake l'ignore
        return
    if sans_ap:
        x = w.xref
        for _ in range(32):
            if doc.xref_get_key(x, "RV")[0] != "null":
                doc.xref_set_key(x, "RV", "null")
            parent = doc.xref_get_key(x, "Parent")
            if parent[0] != "xref":
                break
            x = int(parent[1].split()[0])
    if w.field_type in (pymupdf.PDF_WIDGET_TYPE_COMBOBOX, pymupdf.PDF_WIDGET_TYPE_LISTBOX):
        paires = {c[0]: c[1] for c in (w.choice_values or [])
                  if isinstance(c, (tuple, list)) and len(c) == 2}
        valeur = w.field_value
        if isinstance(valeur, str) and valeur in paires:
            hexa = ("﻿" + paires[valeur]).encode("utf-16-be").hex()   # chaîne PDF UTF-16BE
            doc.xref_set_key(w.xref, "V", f"<{hexa}>")


def _purger_certification(doc) -> None:
    """Après gravure, /AcroForm et /Perms (certification DocMDP) ne renvoient plus à rien."""
    cat = doc.pdf_catalog()
    for cle in ("Perms", "AcroForm"):
        if doc.xref_get_key(cat, cle)[0] != "null":
            doc.xref_set_key(cat, cle, "null")


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
    (``orig`` != ``apres``).
    """
    if not (orig.size == apres.size == fond.size):
        return True
    pire = ImageChops.darker(_ecart_max(orig, apres), _ecart_max(orig, fond))
    return sum(pire.histogram()[SEUIL_CANAL + 1:]) >= SEUIL_PIXELS


def _marque_ajoutee(orig: Image.Image, apres: Image.Image, fond: Image.Image) -> bool:
    """Vrai si le résultat montre une marque que l'original ne montrait PAS à l'écran
    (annotation « NoView », calque masqué... que ``bake`` rend visible)."""
    ajout = ImageChops.subtract(_ecart_max(apres, fond), _ecart_max(orig, fond))
    return sum(ajout.histogram()[SEUIL_CANAL + 1:]) >= SEUIL_PIXELS


def _masquer_zones(image: Image.Image, fond: Image.Image, page, zones) -> None:
    """Dans ``image``, remplace les zones données (coordonnées page) par le contenu de ``fond``."""
    m = _matrice(page, DPI_CONTROLE, COTE_MAX_CONTROLE)
    for z in zones:
        r = pymupdf.Rect(z) * page.rotation_matrix * m
        boite = (max(0, int(r.x0) - 3), max(0, int(r.y0) - 3),
                 min(image.width, int(r.x1) + 4), min(image.height, int(r.y1) + 4))
        if boite[2] > boite[0] and boite[3] > boite[1]:
            image.paste(fond.crop(boite), boite)


def _pages_alterees(orig, doc, numeros, zones_vides=None) -> list:
    """Contrôle AVANT enregistrement : pages (1-based) dont l'aspect dans ``doc`` diffère de
    celui de ``orig``."""
    return _comparer_pages(orig, doc, numeros, zones_vides)


def _comparer_pages(orig, doc, numeros, zones_vides=None) -> list:
    """Pages (1-based) dont l'aspect dans ``doc`` diffère de celui de ``orig`` : marque perdue
    OU marque ajoutée."""
    zones_vides = zones_vides or {}
    alterees = []
    for n in numeros:
        try:
            avant = _image(_pixmap(orig[n - 1], DPI_CONTROLE, COTE_MAX_CONTROLE))
            fond = _image(_pixmap(orig[n - 1], DPI_CONTROLE, COTE_MAX_CONTROLE, annots=False))
            apres = _image(_pixmap(doc[n - 1], DPI_CONTROLE, COTE_MAX_CONTROLE))
            if n in zones_vides:
                _masquer_zones(avant, fond, orig[n - 1], zones_vides[n])
                _masquer_zones(apres, fond, orig[n - 1], zones_vides[n])
            if _marque_perdue(avant, apres, fond) or _marque_ajoutee(avant, apres, fond):
                alterees.append(n)
        except Exception:
            alterees.append(n)   # impossible de contrôler : par prudence
        finally:
            pymupdf.TOOLS.store_shrink(100)    # ne garde pas les pages en cache
    return alterees


def _xref_image(doc, pix) -> int:
    """Ajoute ``pix`` au document comme image Flate DÉJÀ compressée (pas de copie brute en mémoire)."""
    xref = doc.get_new_xref()
    doc.update_object(xref, f"<</Type/XObject/Subtype/Image/Width {pix.width}/Height {pix.height}"
                            "/ColorSpace/DeviceRGB/BitsPerComponent 8>>")
    doc.update_stream(xref, zlib.compress(pix.samples, 6), new=True, compress=False)
    doc.xref_set_key(xref, "Filter", "/FlateDecode")   # update_stream(new=True) efface /Filter
    return xref


def _convertir_en_images(doc, orig, numeros, dpi: int) -> None:
    """Remplace les pages listées (1-based) de ``doc`` par le rendu image de ``orig``.

    La page d'origine garde son numéro d'objet (xref) : liens, destinations nommées et
    signets qui la visent restent valides. La page image est créée EN FIN de document pour
    ne pas décaler les numéros de page pendant la recréation des liens, puis son contenu
    est recopié dans l'objet de la page d'origine.
    """
    for n in numeros:
        pix = _pixmap(orig[n - 1], dpi, COTE_MAX_RASTER)
        ancienne = doc[n - 1]
        rect = ancienne.rect
        old_xref = ancienne.xref
        parent = doc.xref_get_key(old_xref, "Parent")
        liens = []
        try:
            liens = ancienne.get_links()
        except Exception:
            pass
        nouvelle = doc.new_page(-1, width=rect.width, height=rect.height)
        nouvelle.set_cropbox(nouvelle.mediabox)   # sinon /CropBox hérité du nœud /Pages rogne l'image
        nouvelle.insert_image(nouvelle.rect, xref=_xref_image(doc, pix))
        for lien in liens:
            try:
                nouvelle.insert_link(lien)
            except Exception:
                pass
        doc.update_object(old_xref, doc.xref_object(nouvelle.xref, compressed=False))
        if parent[0] == "xref":
            doc.xref_set_key(old_xref, "Parent", parent[1])
        doc.delete_page(-1)


# --------------------------------------------------------------------------- #
# Ouverture / écriture
# --------------------------------------------------------------------------- #
def _ouvrir(src: Path):
    try:
        with open(src, "rb"):
            pass
    except OSError as e:
        raise ErreurPDF(_message_erreur(e)) from e
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
    texte = str(e)
    if nom == "EmptyFileError":
        return "fichier vide"
    if nom == "FileDataError":
        return "PDF illisible ou corrompu"
    if isinstance(e, PermissionError) or "Permission denied" in texte:
        return ("accès refusé : le fichier ou le dossier est ouvert dans un autre programme "
                "(Acrobat ?), verrouillé ou protégé")
    if isinstance(e, FileNotFoundError):
        return "fichier introuvable"
    if "No space left" in texte or "cannot fwrite" in texte:
        return "disque plein : impossible d'écrire le fichier de sortie"
    if isinstance(e, OSError):
        return f"erreur disque : {e.strerror or e}"
    texte = re.sub(r"^(Fz|mupdf)\w*:?\s*(code=\d+:\s*)?", "", texte)
    return f"{nom} : {texte}"


def _remplacer(tmp: str, dst: Path) -> None:
    """``os.replace`` avec quelques nouvelles tentatives : un antivirus, OneDrive ou l'indexeur
    Windows peuvent tenir le fichier quelques centaines de ms (erreurs 5 / 32)."""
    for i in range(ESSAIS_REMPLACEMENT):
        try:
            os.replace(tmp, dst)
            return
        except PermissionError:
            if i == ESSAIS_REMPLACEMENT - 1:
                raise
            time.sleep(0.05 * 2 ** i)


def _chemins_longs_actifs() -> bool:
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                            r"SYSTEM\CurrentControlSet\Control\FileSystem") as cle:
            return winreg.QueryValueEx(cle, "LongPathsEnabled")[0] == 1
    except (OSError, ImportError):
        return False


def _verifier_longueur_chemin(dst: Path) -> None:
    """Windows (réglage par défaut) refuse les chemins de 260 caractères ou plus."""
    if sys.platform != "win32":
        return
    plus_long = max(len(str(dst)), len(str(dst.parent)) + 1 + 22)   # 22 = fichier temporaire
    if plus_long >= 260 and not _chemins_longs_actifs():
        raise ErreurPDF(f"chemin de sortie trop long ({plus_long} caractères, limite Windows : 259) : "
                        "choisissez un dossier de sortie moins profond")


def _ecrire_atomique(dst: Path, ecrire) -> None:
    """Écrit via un fichier temporaire du dossier de sortie, puis remplace ``dst``."""
    try:
        dst.parent.mkdir(parents=True, exist_ok=True)
    except OSError as e:
        raise ErreurPDF(f"dossier de sortie inaccessible ({dst.parent}) : {e.strerror or e}") from e
    fd, tmp = tempfile.mkstemp(prefix=".aplatir-", suffix=".tmp", dir=str(dst.parent))
    os.close(fd)
    try:
        ecrire(tmp)
        _remplacer(tmp, dst)
    except BaseException:
        try:
            os.remove(tmp)
        except OSError:
            pass
        raise


def _enregistrer(doc, dst: Path) -> None:
    # garbage=1 et NON 2/3 : après ``bake``, ``garbage>=2`` renumérote deux fois un /Resources
    # hérité du nœud /Pages (références nulles : tampons, signatures et polices perdus) et
    # est quadratique sur les gros formulaires. Sortie toujours non chiffrée : un PDF à mot de
    # passe « propriétaire » seul s'ouvre sans mot de passe mais refuse l'assemblage dans Acrobat.
    _ecrire_atomique(dst, lambda tmp: doc.save(tmp, garbage=1, deflate=True,
                                                      encryption=pymupdf.PDF_ENCRYPT_NONE))


# --------------------------------------------------------------------------- #
# Traitement d'un fichier
# --------------------------------------------------------------------------- #
def aplatir_fichier(src, dossier_sortie, *, securite: bool = True,
                    dpi_raster: int = DPI_RASTER, dst=None) -> Resultat:
    """Aplatit ``src`` vers ``dst`` (par défaut ``dossier_sortie/[a]- <nom>``).

    Ne lève jamais d'exception : toute erreur est rendue dans ``Resultat``.
    """
    src = Path(src)
    res = Resultat(src=src)
    try:
        _aplatir(src, Path(dossier_sortie), securite, dpi_raster, res,
                 Path(dst) if dst is not None else None)
    except Exception as e:
        res.statut = "erreur"
        res.message = _message_erreur(e)
    return res


def _liste_pages(pages) -> str:
    pages = list(pages)
    if len(pages) <= MAX_PAGES_AFFICHEES:
        return ", ".join(map(str, pages))
    return ", ".join(map(str, pages[:MAX_PAGES_AFFICHEES])) + f"… (+{len(pages) - MAX_PAGES_AFFICHEES})"


def _aplatir(src: Path, dossier: Path, securite: bool, dpi_raster: int, res: Resultat,
             dst: Path | None) -> None:
    if src.name.startswith(PREFIXE):
        res.statut = "ignore"
        res.message = "déjà aplati (le nom commence par « [a]- »)"
        return
    if not src.is_file():
        raise ErreurPDF("fichier introuvable")
    dst = dst or chemin_sortie(src, dossier)
    res.dst = dst
    _verifier_longueur_chemin(dst)
    existait = dst.exists()

    doc = _ouvrir(src)
    orig = None
    try:
        reparee = bool(getattr(doc, "is_repaired", False))
        analyse = analyser(doc)
        res.signatures = analyse.signatures
        res.pages_elements = sorted(analyse.pages)
        nb_pages = doc.page_count
        suffixe_reparee = (" ; fichier source endommagé (réparé automatiquement) : signature(s) ou "
                           "page(s) peut-être manquantes dès l'origine, à vérifier")

        if not analyse.pages:
            if _xfa_present(doc):
                raise ErreurPDF(
                    "formulaire dynamique XFA : son contenu n'est pas dans les pages (il n'y a qu'une "
                    "page « Please wait… »). Ouvrez-le dans Adobe Reader, imprimez-le en PDF "
                    "(Microsoft Print to PDF), puis déposez ce PDF")
            if _a_des_restrictions(doc):    # on écrit une copie sans restrictions d'assemblage
                _enregistrer(doc, dst)
            else:
                doc.close()
                doc = None
                _ecrire_atomique(dst, lambda tmp: shutil.copyfile(src, tmp))
            res.statut = "alerte" if reparee else "copie"
            res.message = "rien à aplatir (copie telle quelle)" + (suffixe_reparee if reparee else "")
            if existait:
                res.message += " (remplace le fichier existant)"
            return

        orig = _ouvrir(src)    # exemplaire intact : contrôle visuel + conversion en image
        _normaliser_pour_rendu(doc)
        _normaliser_pour_rendu(orig)
        pages_image: list[int] = []
        alerte_visuelle = ""
        try:
            doc.bake(annots=True, widgets=True)
        except Exception as e:
            if not securite:
                raise ErreurPDF(f"aplatissement impossible ({e})") from e
            doc.close()
            doc = _ouvrir(src)           # repart d'un document intact
            _normaliser_pour_rendu(doc)
            pages_image = sorted(analyse.pages)
        else:
            alterees = _pages_alterees(orig, doc, sorted(analyse.pages), analyse.zones_vides)
            if alterees and securite:
                pages_image = alterees
            elif alterees:
                alerte_visuelle = ("rendu différent après aplatissement, page(s) "
                                   + ", ".join(map(str, alterees)) + " : à vérifier")

        if pages_image:
            _convertir_en_images(doc, orig, pages_image, dpi_raster)
        res.pages_image = pages_image
        _purger_certification(doc)
        _enregistrer(doc, dst)

        # contrôle final sur le fichier ÉCRIT (et non sur la copie en mémoire)
        chk = pymupdf.open(str(dst))
        try:
            pages_ok = chk.page_count == nb_pages
            restes = analyser(chk).pages if pages_ok else {}
            differentes = _controler_ecrit(orig, chk, analyse, pages_image) if pages_ok else []
        finally:
            chk.close()
    finally:
        if orig is not None:
            orig.close()
        if doc is not None:
            doc.close()

    morceaux = []
    if res.signatures:
        morceaux.append(f"{res.signatures} signature(s)")
    if analyse.vides:
        morceaux.append(f"{analyse.vides} champ(s) de signature vide(s)")
    morceaux.append("page(s) " + _liste_pages(res.pages_elements))
    base = " ; ".join(morceaux)

    problemes = []
    if not pages_ok:
        problemes.append("le nombre de pages a changé : à vérifier !")
    if restes:
        problemes.append("éléments encore interactifs page(s) " + ", ".join(map(str, sorted(restes))))
    if differentes:
        problemes.append("page(s) " + ", ".join(map(str, differentes))
                         + " différente(s) de l'original dans le fichier écrit : à vérifier")
    if alerte_visuelle:
        problemes.append(alerte_visuelle)
    if reparee:
        problemes.append(suffixe_reparee.lstrip(" ; "))

    if problemes:
        res.statut = "alerte"
        res.message = base + " ; " + " ; ".join(problemes)
    elif res.pages_image:
        res.statut = "securite"
        res.message = (f"{base} ; page(s) {_liste_pages(res.pages_image)} "
                       "convertie(s) en image par sécurité")
    else:
        res.statut = "ok"
        res.message = base
    if existait:
        res.message += " (remplace le fichier existant)"


def _texte(page) -> str:
    return re.sub(r"\s+", " ", page.get_text("text") or "").strip()


def _controler_ecrit(orig, chk, analyse: Analyse, pages_image: list) -> list:
    """Compare le fichier ÉCRIT à l'original. Retourne les pages (1-based) qui diffèrent.

    - pages portant des éléments (hors pages converties en image, identiques par construction) :
      comparaison visuelle, comme avant l'enregistrement ;
    - toutes les autres pages : comparaison du texte extrait (détecte une police ou une
      ressource perdue à l'enregistrement, même sur une page sans annotation).
    """
    differentes: list[int] = []
    a_controler = [n for n in sorted(analyse.pages) if n not in pages_image]
    differentes.extend(_comparer_pages(orig, chk, a_controler, analyse.zones_vides))
    for i in range(chk.page_count):
        n = i + 1
        if n in analyse.pages:
            continue
        try:
            if _texte(orig[i]) != _texte(chk[i]):
                differentes.append(n)
        except Exception:
            differentes.append(n)
    return sorted(set(differentes))
