#!/usr/bin/env python3
"""Bundle EXACTLY the two user-approved Solaris Neon 2.2.8 card images.

The card images are uploaded once to the GitHub Assets folder under their
original Russian filenames (no manual rename required). The CI build copies
them to predictable WPF Resource filenames. No generated substitutes.
"""
from __future__ import annotations

import shutil
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Assets"
CARDS = {
    "SolarisModdedCard": "Воксельная битва под пурпурным небом.png",
    "SolarisVanillaCard": "Изображение ChatGPT 9 окт. 2026 г., 17_24_22.png",
}
MIN_WIDTH = 1600
MIN_HEIGHT = 500
TARGET_ASPECT_RATIO = 3.0
ASPECT_TOLERANCE = 0.15


def main() -> None:
    for dest_stem, original_name in CARDS.items():
        original = ASSETS / original_name
        canonical = ASSETS / (dest_stem + ".png")
        # Accept files already uploaded using canonical names, too.
        src = original if original.is_file() else canonical
        if not src.is_file():
            raise SystemExit(
                f"Missing approved {dest_stem} card. Upload its original PNG "
                f"to the GitHub Assets folder before publishing Solaris 2.2.8."
            )
        with Image.open(src) as artwork:
            if (artwork.format != "PNG" or artwork.width < MIN_WIDTH or
                artwork.height < MIN_HEIGHT or
                abs(artwork.width / artwork.height - TARGET_ASPECT_RATIO) > ASPECT_TOLERANCE):
                raise SystemExit(
                    f"Unexpected approved card {dest_stem}: {artwork.format}, {artwork.size}; "
                    f"expected a user-approved wide 3:1 PNG"
                )
            artwork.verify()
        if src != canonical:
            shutil.copyfile(src, canonical)
        print(f"Approved card embedded: {canonical.name} ({src.stat().st_size:,} bytes)")

    print("Both approved Solaris Neon 2.2.8 server cards are ready.")


if __name__ == "__main__":
    main()
