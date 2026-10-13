#!/usr/bin/env python3
"""Validate optional approved planetary layers and prepare native WPF PNGs.

No images are downloaded or invented. Original PNG/WebP files remain untouched;
prepared PNGs retain their decoded pixels, alpha, and embedded color profile.
Missing optional layers use the scene's vector fallback. A malformed supplied
layer stops preparation so CI cannot silently accept a broken art replacement.
"""
from __future__ import annotations

import argparse
import os
import sys
import warnings
from dataclasses import dataclass
from pathlib import Path

try:
    from PIL import Image, UnidentifiedImageError
except ImportError:
    raise SystemExit(
        "Pillow is required to prepare planetary art. Install the Pillow version "
        "pinned in .github/workflows/animated-planets-home-ci.yml."
    )


ROOT = Path(__file__).resolve().parent.parent
MAX_SOURCE_BYTES = 32 * 1024 * 1024
MAX_PREPARED_TOTAL_BYTES = 64 * 1024 * 1024


@dataclass(frozen=True)
class Layer:
    name: str
    size: tuple[int, int]
    maximum_size: tuple[int, int]
    transparent: bool = True


# Sizes express the layout's logical export ratio, not a forced resampling size.
LAYERS = (
    Layer("space-background", (1280, 600), (3840, 1800), transparent=False),
    Layer("bridge-foreground", (1280, 600), (3840, 1800)),
    Layer("operator-body", (150, 151), (1024, 1024)),
    Layer("operator-head", (54, 53), (512, 512)),
    Layer("vanilla-surface", (246, 246), (1024, 1024), transparent=False),
    Layer("vanilla-atmosphere", (336, 336), (1024, 1024)),
    Layer("modded-surface", (246, 246), (1024, 1024), transparent=False),
    Layer("modded-atmosphere", (336, 336), (1024, 1024)),
    Layer("modded-rings-back", (350, 300), (1536, 1317)),
    Layer("modded-rings-front", (350, 300), (1536, 1317)),
)


class ArtError(Exception):
    """Actionable invalid user-supplied artwork."""


def read_layer(source: Path, layer: Layer) -> tuple[Image.Image, bytes | None]:
    if source.stat().st_size > MAX_SOURCE_BYTES:
        raise ArtError(f"{source.name}: source exceeds the 32 MiB per-file limit.")
    expected_format = "PNG" if source.suffix == ".png" else "WEBP"
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(source) as image:
                if image.format != expected_format:
                    raise ArtError(
                        f"{source.name}: expected {expected_format} content, got {image.format}."
                    )
                if getattr(image, "n_frames", 1) != 1:
                    raise ArtError(f"{source.name}: animated images are not supported; export one frame.")
                width, height = image.size
                minimum_width, minimum_height = layer.size
                maximum_width, maximum_height = layer.maximum_size
                if not (minimum_width <= width <= maximum_width and
                        minimum_height <= height <= maximum_height):
                    raise ArtError(
                        f"{source.name}: {width}x{height} is outside the supported "
                        f"{minimum_width}x{minimum_height}–{maximum_width}x{maximum_height} range."
                    )
                expected_ratio = minimum_width / minimum_height
                if abs(width / height / expected_ratio - 1) > 0.02:
                    raise ArtError(
                        f"{source.name}: preserve the {minimum_width}:{minimum_height} "
                        "canvas ratio (maximum 2% rounding tolerance)."
                    )
                image.verify()
            # verify() validates the container but invalidates its decoder.
            with Image.open(source) as image:
                image.load()
                rgba = image.convert("RGBA")
                profile = image.info.get("icc_profile")
        alpha = rgba.getchannel("A")
        minimum_alpha, maximum_alpha = alpha.getextrema()
        bounds = alpha.getbbox()
        if bounds is None or maximum_alpha == 0:
            raise ArtError(f"{source.name}: the entire image is transparent; export the visible layer.")
        if layer.transparent and minimum_alpha != 0:
            raise ArtError(
                f"{source.name}: requires transparent pixels (alpha 0) around the isolated layer; "
                "export RGBA rather than an opaque background."
            )
        print(f"Validated {source.name}: {rgba.width}x{rgba.height}, visible alpha bounds {bounds}")
        return rgba, profile
    except (OSError, ValueError, SyntaxError, EOFError, UnidentifiedImageError,
            Image.DecompressionBombError, Image.DecompressionBombWarning) as error:
        raise ArtError(f"{source.name}: cannot decode/validate the image: {error}") from error


def prepare(source_dir: Path, output_dir: Path) -> None:
    if source_dir.resolve() == output_dir.resolve():
        raise ArtError("The output directory must differ from the source directory; originals are preserved.")

    # Validate all supplied layers before replacing any existing generated files.
    prepared: list[tuple[Layer, Image.Image, bytes | None]] = []
    missing: list[Layer] = []
    for layer in LAYERS:
        candidates = [source_dir / (layer.name + extension) for extension in (".png", ".webp")]
        existing = [source for source in candidates if source.is_file()]
        if len(existing) > 1:
            raise ArtError(
                f"{layer.name}: both PNG and WebP sources exist. Keep one approved source "
                "format per layer to avoid choosing artwork silently."
            )
        if not existing:
            missing.append(layer)
            continue
        image, profile = read_layer(existing[0], layer)
        prepared.append((layer, image, profile))

    if prepared or output_dir.exists():
        output_dir.mkdir(parents=True, exist_ok=True)
        # Stage derived files before publishing them, so failed validation or a
        # size-limit failure leaves previous generated assets intact.
        staged: list[tuple[Path, Path]] = []
        try:
            total_size = 0
            for layer, image, profile in prepared:
                destination = output_dir / (layer.name + ".png")
                temporary = destination.with_suffix(".png.tmp")
                staged.append((temporary, destination))
                options = {"icc_profile": profile} if profile else {}
                image.save(temporary, format="PNG", compress_level=9, **options)
                total_size += temporary.stat().st_size
                if total_size > MAX_PREPARED_TOTAL_BYTES:
                    raise ArtError(
                        "Prepared PNG layers exceed 64 MiB in total. Ask the artist for "
                        "smaller exports within the documented layout; do not overwrite the originals."
                    )
            for temporary, destination in staged:
                os.replace(temporary, destination)
                print(f"Prepared {destination.name}: {destination.stat().st_size:,} bytes")
            # This is the generated-output directory, never the source directory.
            # Remove only this script's canonical outputs if their source vanished,
            # so a missing optional layer cannot embed a stale approved replacement.
            for layer in missing:
                (output_dir / (layer.name + ".png")).unlink(missing_ok=True)
        finally:
            for temporary, _ in staged:
                temporary.unlink(missing_ok=True)
            for _, image, _ in prepared:
                image.close()
    if missing:
        print("Vector fallback: " + ", ".join(layer.name for layer in missing))
    print(f"Planetary artwork ready: {len(prepared)} optional layer(s); originals preserved.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path, default=ROOT / "Assets" / "Planetary",
                        help="Approved PNG/WebP sources (default: Assets/Planetary).")
    parser.add_argument("--output-dir", type=Path,
                        help="Generated PNG directory (default: SOURCE_DIR/Prepared).")
    args = parser.parse_args()
    try:
        prepare(args.source_dir, args.output_dir or args.source_dir / "Prepared")
    except (ArtError, OSError) as error:
        print(f"Planetary artwork preparation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
