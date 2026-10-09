#!/usr/bin/env python3
"""Prepare the approved Solaris Neon artworks for WPF resource embedding.

The source ZIP must be the original Solaris-Neon-UI.zip from the
approved launcher art pack. We verify its SHA256 and the extracted PNGs
before compiling them into SolarisLauncher.exe.

Run:
    python scripts/prepare_neon_art.py

Do not include the source archive in the app at runtime: both PNGs are
compiled as WPF Resource items and load from pack://application URIs.
"""

from __future__ import annotations

import hashlib
from pathlib import Path
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parent.parent
SOURCE_ZIP = ROOT / "Assets" / "Solaris-Neon-UI.zip"
EXPECTED_ZIP_SHA256 = "16eba2ca9e49a4e8aa3892589040bf881e5a82c334e7d4a3f9a3d25efa8143f6"
ASSETS = {
    "SolarisWorld.png": "842a512279cfc7aaa35dbe7457d0955c5e789c3415ae61e911e4aed70507babd",
    "SolarisModded.png": "a4e47f548a9fd41c4530cd8fb544e43cb1d3ce79a2db7ede5138a5fcccab7ffa",
}
ARCHIVE_PREFIX = "Solaris-Neon-UI/Patch/Assets/"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> None:
    if not SOURCE_ZIP.is_file():
        raise SystemExit(
            "Approved artwork archive is not committed: Assets/Solaris-Neon-UI.zip. "
            "Do not publish Solaris 2.2.7 without the actual artwork."
        )

    archive = SOURCE_ZIP.read_bytes()
    actual = sha256(archive)
    if actual != EXPECTED_ZIP_SHA256:
        raise SystemExit(
            f"Wrong Solaris art ZIP SHA256: {actual}. "
            f"Expected {EXPECTED_ZIP_SHA256}."
        )

    with ZipFile(SOURCE_ZIP) as source:
        for filename, expected_hash in ASSETS.items():
            data = source.read(ARCHIVE_PREFIX + filename)
            actual_hash = sha256(data)
            if actual_hash != expected_hash:
                raise SystemExit(f"Unexpected contents of {filename}: {actual_hash}")
            target = ROOT / "Assets" / filename
            target.write_bytes(data)
            print(f"Embedded asset ready: {filename} ({len(data):,} bytes)")

    print("Approved Solaris Neon artwork integrity checks passed.")


if __name__ == "__main__":
    main()
