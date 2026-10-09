#!/usr/bin/env python3
"""Convert the approved Solaris Neon artwork to native WPF PNG resources.

The two source images are committed as high-quality compressed WebP assets.
The SHA256 hashes ensure the visuals used in every Windows release are the
approved Solaris World/Modded illustrations and not placeholder art.

The GitHub Actions workflow installs Pillow and runs this before dotnet
restore/build/publish. The PNG files become WPF Resource items in the
self-contained single-file SolarisLauncher.exe.
"""

from __future__ import annotations

import hashlib
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Assets"
APPROVED = {
    "SolarisWorld": {
        "sha256": "377c9f364d90062cbcc6fc26a5f0d1544a7959d61e92461b9f9f91d8e68cd3f3",
        "dimensions": (1672, 941),
    },
    "SolarisModded": {
        "sha256": "93df7ba8374cfeb47b121a85f487a93fb0ec0226f0ba95fcb271860c9dbf998a",
        "dimensions": (2043, 770),
    },
}


def main() -> None:
    for stem, metadata in APPROVED.items():
        source = ASSETS / (stem + ".webp")
        if not source.is_file():
            raise SystemExit(
                f"Missing approved artwork {source.name}: refusing to build "
                "Solaris without the original Neon UI."
            )

        actual = hashlib.sha256(source.read_bytes()).hexdigest()
        if actual != metadata["sha256"]:
            raise SystemExit(
                f"Wrong {source.name} SHA256 {actual}; "
                f"expected {metadata['sha256']}."
            )

        with Image.open(source) as image:
            if image.format != "WEBP" or image.size != metadata["dimensions"]:
                raise SystemExit(
                    f"Unexpected {source.name} type or dimensions: "
                    f"{image.format} {image.size}"
                )
            output = ASSETS / (stem + ".png")
            image.convert("RGB").save(output, format="PNG", optimize=True)
            print(f"Embedded asset ready: {output.name} ({output.stat().st_size:,} bytes)")

    print("Approved Solaris Neon artwork integrity checks passed.")


if __name__ == "__main__":
    main()
