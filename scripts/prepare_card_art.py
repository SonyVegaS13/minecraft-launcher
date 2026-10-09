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
    "SolarisModdedCard": "Воксельная битва под пурпурным небом(3).png",
    "SolarisVanillaCard": "Изображение ChatGPT 9 окт. 2026 г., 17_24_22(1).png",
}
APPROVED_SIZE = (2048, 682)


def main() -> None:
    for dest_stem, original_name in CARDS.items():
        original = ASSETS / original_name
        canonical = ASSETS / (dest_stem + ".png")
        # Accept files already uploaded using canonical names, too.
        src = original if original.is_file() else canonical
        if not src.is_file():
            raise SystemExit(
                f"Missing approved {dest_stem} card. Upload {original_name} "
                f"to the GitHub Assets folder before publishing Solaris 2.2.8."
            )
        with Image.open(src) as artwork:
            if artwork.format != "PNG" or artwork.size != APPROVED_SIZE:
                raise SystemExit(
                    f"Unexpected {src.name}: {artwork.format}, {artwork.size}; "
                    f"expected user-approved PNG {APPROVED_SIZE}"
                )
            artwork.verify()
        if src != canonical:
            shutil.copyfile(src, canonical)
        print(f"Approved card embedded: {canonical.name} from {src.name}")

    print("Both approved Solaris Neon 2.2.8 server cards are ready.")


if __name__ == "__main__":
    main()
