# -*- coding: utf-8 -*-
"""Icône de l'application, dessinée en code (aucun fichier image à embarquer).

    python aplatir_icon.py            -> écrit aplatir.ico (utilisé pour l'exe)
"""
from __future__ import annotations

import base64
import io
import sys

from PIL import Image, ImageDraw


def dessiner_icone(taille: int = 64) -> Image.Image:
    """Page blanche à coin plié sur fond bleu, avec une pastille verte « validé »."""
    s = 512
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=s // 5, fill=(31, 78, 140, 255))

    x0, y0, x1, y1 = int(s * .22), int(s * .12), int(s * .74), int(s * .86)
    pli = int(s * .17)
    d.polygon([(x0, y0), (x1 - pli, y0), (x1, y0 + pli), (x1, y1), (x0, y1)], fill=(255, 255, 255, 255))
    d.polygon([(x1 - pli, y0), (x1 - pli, y0 + pli), (x1, y0 + pli)], fill=(190, 208, 232, 255))
    for i in range(3):                                   # lignes de texte
        y = int(s * (.32 + i * .09))
        d.rounded_rectangle((x0 + 34, y, x1 - 40, y + 14), radius=7, fill=(160, 175, 195, 255))
    d.line([(x0 + 34, int(s * .66)), (x0 + 90, int(s * .56)), (x0 + 130, int(s * .70)),
            (x0 + 190, int(s * .58))], fill=(31, 78, 140, 255), width=12, joint="curve")  # signature

    cx, cy, r = int(s * .72), int(s * .74), int(s * .21)  # pastille verte + coche
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(46, 160, 67, 255), outline=(255, 255, 255, 255), width=10)
    d.line([(cx - r * .5, cy + r * .02), (cx - r * .1, cy + r * .42), (cx + r * .52, cy - r * .4)],
           fill=(255, 255, 255, 255), width=22, joint="curve")
    return im.resize((taille, taille), Image.LANCZOS)


def icone_png_base64(taille: int = 64) -> str:
    """PNG encodé en base64, utilisable par ``tkinter.PhotoImage(data=...)``."""
    buf = io.BytesIO()
    dessiner_icone(taille).save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def ecrire_ico(chemin: str = "aplatir.ico") -> None:
    tailles = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    dessiner_icone(256).save(chemin, format="ICO", sizes=tailles)


if __name__ == "__main__":
    ecrire_ico(sys.argv[1] if len(sys.argv) > 1 else "aplatir.ico")
    print("icône écrite")
