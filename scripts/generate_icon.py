"""Build the approved Solaris icon from its committed transparent source.

The source is stored as a base64 PNG because the GitHub text-only editor
cannot upload binary files. This script produces proper PNG and Windows ICO
assets before the WPF project is compiled.
"""
from __future__ import annotations

import base64
import hashlib
import io
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
source = ASSETS / "SolarisOfficialLogo.png.base64"
raw = base64.b64decode(source.read_text(encoding="ascii").strip(), validate=True)
expected_sha256 = "07615d96fd184d6f92c4662ac1056a8168a44401b6a6b439597210b6eaf7dfd0"
assert hashlib.sha256(raw).hexdigest() == expected_sha256, "Emblem source does not match the approved version"

with Image.open(io.BytesIO(raw)) as original:
    original.load()
    assert original.size == (128, 128), f"Unexpected logo size: {original.size}"
    logo = original.convert("RGBA")
    assert logo.getpixel((0, 0))[3] == 0, "Transparent background was lost"

    # Produce a WPF PNG and true multi-resolution PE/Windows icon.
    logo.save(ASSETS / "SolarisOfficialLogo.png", optimize=True)
    icon_image = logo.resize((256, 256), Image.Resampling.LANCZOS)
    icon_image.save(
        ASSETS / "SolarisLauncher.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print("Official Solaris emblem assets generated successfully.")
    print("PNG:", ASSETS / "SolarisOfficialLogo.png")
    print("ICO:", ASSETS / "SolarisLauncher.ico")
