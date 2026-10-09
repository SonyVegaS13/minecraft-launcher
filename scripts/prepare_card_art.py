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
        destination = ASSETS / f"{name}.png"
        if destination.is_file():
            # The exact user-supplied PNG takes precedence if available.
            with Image.open(destination) as original:
                if original.format != "PNG" or original.size != (2048, 682):
                    raise RuntimeError(f"Unexpected approved PNG: {destination} {original.size}")
                original.verify()
            print(f"Approved original card PNG ready: {destination.name}")
            continue
        if not source.is_file():
            # Development builds may run without cards. The release gate
            # refuses to publish 2.2.8 until both images are present.
            print(f"Awaiting approved source: {name}.png or {name}.webp")
            continue
        with Image.open(source) as opened:
            if opened.format != "WEBP" or opened.width < 1500 or opened.height < 500:
                raise RuntimeError(f"Invalid user-approved server banner: {source}")
            opened.convert("RGB").save(destination, format="PNG", optimize=True)
            print(f"Prepared official card art {destination.name}: {opened.width}x{opened.height}")

if __name__ == "__main__":
    main()
