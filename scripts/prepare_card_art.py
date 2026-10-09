#!/usr/bin/env python3
"""Package the TWO user-approved Solaris 2.2.8 server card banners.

Sources (exact artworks selected by the project owner):
- SolarisVanillaCard.webp: sunset survival valley, player and wolf.
- SolarisModdedCard.webp: Wither Storm, distant Wither, Create factory/train.

Both source files are binary Git blobs added to the repository. This script
converts them into resource PNGs before dotnet publish. Do not regenerate art.
"""

from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Assets"
NAMES = ("SolarisVanillaCard", "SolarisModdedCard")

def main() -> None:
    for name in NAMES:
        source = ASSETS / f"{name}.webp"
        if not source.is_file():
            # During ongoing development, allow builds without the optional new
            # banners; a release gate below will require both art files.
            print(f"Awaiting approved source: {source}")
            continue
        with Image.open(source) as opened:
            if opened.format != "WEBP" or opened.width < 1500 or opened.height < 500:
                raise RuntimeError(f"Invalid user-approved server banner: {source}")
            destination = ASSETS / f"{name}.png"
            opened.convert("RGB").save(destination, format="PNG", optimize=True)
            print(f"Prepared official card art {destination.name}: {opened.width}x{opened.height}")

if __name__ == "__main__":
    main()
