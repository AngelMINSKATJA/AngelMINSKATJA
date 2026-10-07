#!/usr/bin/env python3
"""Génère src/BarcodeTray/Assets/app.ico (bibliothèque standard Python uniquement).

Usage :
    python3 tools/make_icon.py                 # écrit src/BarcodeTray/Assets/app.ico
    python3 tools/make_icon.py chemin/sortie.ico
    python3 tools/make_icon.py --preview dossier   # écrit aussi un PNG par taille (pour contrôle visuel)

Dessin : un carré arrondi sombre avec des barres blanches de code-barres (et une fine ligne de
"texte" dessous aux grandes tailles).  Les barres sont alignées sur des pixels entiers pour rester
nettes en 16/24/32 px ; seuls les coins arrondis sont lissés.

Format de sortie : fichier ICO dont chaque image est un PNG RGBA 32 bits (pris en charge depuis
Windows Vista) aux tailles 16, 24, 32, 48, 64 et 256 pixels.
"""

import os
import struct
import sys
import zlib

SIZES = (16, 24, 32, 48, 64, 256)

BACKGROUND = (0x1B, 0x26, 0x3B)      # bleu nuit
BAR = (0xFF, 0xFF, 0xFF)             # barres blanches
ACCENT = (0x5B, 0xC0, 0xEB)          # ligne de "texte" (bleu clair)

# Motifs de modules ("1" = barre, "0" = espace), commençant et finissant par une barre.
PATTERN_10 = "1101001011"                     # 16 px
PATTERN_14 = "11010010011001"                 # 24 px
PATTERN_20 = "11010010001100101101"           # 32 px
PATTERN_30 = "110100100001101001000110100111"  # 48 px (modules de 1 px)
PATTERN_15 = "110100100110101"                # 64 px (modules de 2 px)
PATTERN_32 = "11010111010011010001011010010111"  # 256 px (modules de 6 px)

# taille -> (motif, px par module, haut des barres, bas des barres, ligne de texte (haut, bas) ou None)
LAYOUT = {
    16: (PATTERN_10, 1, 3, 12, None),
    24: (PATTERN_14, 1, 5, 18, None),
    32: (PATTERN_20, 1, 6, 22, (25, 26)),
    48: (PATTERN_30, 1, 9, 32, (37, 39)),
    64: (PATTERN_15, 2, 12, 43, (50, 52)),
    256: (PATTERN_32, 6, 48, 172, (200, 208)),
}


def rounded_square_coverage(size, x, y, radius, samples):
    """Part (0..1) du pixel (x, y) couverte par le carré arrondi qui remplit toute l'image."""
    inside = 0
    step = 1.0 / samples
    for sy in range(samples):
        py = y + (sy + 0.5) * step
        for sx in range(samples):
            px = x + (sx + 0.5) * step
            # distance au coin d'arrondi le plus proche
            cx = min(max(px, radius), size - radius)
            cy = min(max(py, radius), size - radius)
            dx = px - cx
            dy = py - cy
            if dx * dx + dy * dy <= radius * radius:
                inside += 1
    return inside / float(samples * samples)


def render(size):
    """Renvoie une liste de lignes ; chaque ligne est une liste de tuples (r, g, b, a)."""
    pattern, module_px, top, bottom, text_line = LAYOUT[size]
    radius = size * 0.22
    samples = 8 if size <= 64 else 4

    bars_width = len(pattern) * module_px
    assert pattern[0] == "1" and pattern[-1] == "1" and set(pattern) <= {"0", "1"}
    assert (size - bars_width) % 2 == 0, "motif non centrable sur un nombre entier de pixels"
    left = (size - bars_width) // 2

    pixels = []
    for y in range(size):
        row = []
        for x in range(size):
            coverage = rounded_square_coverage(size, x, y, radius, samples)
            if coverage <= 0.0:
                row.append((0, 0, 0, 0))
                continue

            colour = BACKGROUND
            if top <= y < bottom and left <= x < left + bars_width:
                module = (x - left) // module_px
                if pattern[module] == "1":
                    colour = BAR
            elif text_line is not None and text_line[0] <= y < text_line[1]:
                text_left = left + module_px * 2
                text_right = left + bars_width - module_px * 2
                if text_left <= x < text_right:
                    colour = ACCENT

            row.append((colour[0], colour[1], colour[2], int(round(coverage * 255))))
        pixels.append(row)
    return pixels


def png_bytes(pixels):
    height = len(pixels)
    width = len(pixels[0])

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = bytearray()
    for row in pixels:
        raw.append(0)  # filtre "None"
        for r, g, b, a in row:
            raw.extend((r, g, b, a))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)  # 8 bits, RGBA, sans entrelacement
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def ico_bytes(images):
    """images : liste de (taille, octets PNG)."""
    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = bytearray()
    payload = bytearray()
    for size, data in images:
        dimension = 0 if size >= 256 else size  # 0 signifie 256 dans l'en-tête ICO
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset)
        payload += data
        offset += len(data)
    return header + bytes(entries) + bytes(payload)


def main(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    default_out = os.path.normpath(os.path.join(here, "..", "src", "BarcodeTray", "Assets", "app.ico"))

    preview_dir = None
    args = list(argv)
    if "--preview" in args:
        i = args.index("--preview")
        preview_dir = args[i + 1]
        del args[i:i + 2]
    out = args[0] if args else default_out

    images = []
    for size in SIZES:
        data = png_bytes(render(size))
        images.append((size, data))
        if preview_dir:
            os.makedirs(preview_dir, exist_ok=True)
            with open(os.path.join(preview_dir, "icon_%d.png" % size), "wb") as f:
                f.write(data)

    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "wb") as f:
        f.write(ico_bytes(images))
    print("écrit %s (%d octets, tailles %s)" % (out, os.path.getsize(out), ", ".join(str(s) for s in SIZES)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
