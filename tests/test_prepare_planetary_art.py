"""Artwork pipeline checks: malformed replacements fail, original art survives."""
from __future__ import annotations

import contextlib
import io
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from PIL import Image, features

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
from prepare_planetary_art import ArtError, prepare  # noqa: E402


class PreparePlanetaryArtTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.sources = Path(self.temporary.name) / "sources"
        self.outputs = self.sources / "Prepared"
        self.sources.mkdir()
        self.addCleanup(self.temporary.cleanup)

    def prepare_quietly(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            prepare(self.sources, self.outputs)

    def create_head(self, extension: str = ".png") -> Path:
        source = self.sources / ("operator-head" + extension)
        with Image.new("RGBA", (54, 53), (0, 0, 0, 0)) as image:
            # Include opaque and partial alpha so the conversion cannot pass by
            # merely producing an all-transparent or flattened substitute.
            image.paste((246, 92, 18, 255), (8, 4, 46, 45))
            image.putpixel((27, 45), (101, 52, 191, 83))
            image.save(source, lossless=True)
        return source

    def test_missing_optional_layers_use_fallback_without_outputs(self) -> None:
        self.prepare_quietly()
        self.assertFalse(self.outputs.exists())

    def test_png_pixels_and_original_file_are_preserved(self) -> None:
        source = self.create_head()
        original_bytes = source.read_bytes()
        self.prepare_quietly()
        self.assertEqual(source.read_bytes(), original_bytes)
        with Image.open(source) as original, Image.open(self.outputs / source.name) as prepared:
            self.assertEqual(prepared.mode, "RGBA")
            self.assertEqual(prepared.size, original.size)
            self.assertEqual(prepared.tobytes(), original.convert("RGBA").tobytes())
        output_bytes = (self.outputs / source.name).read_bytes()
        self.prepare_quietly()
        self.assertEqual((self.outputs / source.name).read_bytes(), output_bytes)

    @unittest.skipUnless(features.check("webp"), "Pillow was built without WebP support")
    def test_webp_decoded_pixels_and_original_are_preserved(self) -> None:
        source = self.create_head(".webp")
        original_bytes = source.read_bytes()
        self.prepare_quietly()
        self.assertEqual(source.read_bytes(), original_bytes)
        with Image.open(source) as original, Image.open(self.outputs / "operator-head.png") as prepared:
            self.assertEqual(prepared.tobytes(), original.convert("RGBA").tobytes())

    def test_opaque_surface_is_valid_inside_wpf_circle_mask(self) -> None:
        with Image.new("RGB", (246, 246), (239, 113, 23)) as image:
            image.save(self.sources / "vanilla-surface.png")
        self.prepare_quietly()
        self.assertTrue((self.outputs / "vanilla-surface.png").is_file())

    def test_opaque_atmosphere_is_rejected_before_replacing_output(self) -> None:
        source = self.create_head()
        self.prepare_quietly()
        previous = (self.outputs / source.name).read_bytes()
        with Image.new("RGB", (336, 336), (25, 1, 45)) as image:
            image.save(self.sources / "modded-atmosphere.png")
        with self.assertRaisesRegex(ArtError, "requires transparent pixels"):
            self.prepare_quietly()
        self.assertEqual((self.outputs / source.name).read_bytes(), previous)

    def test_ambiguous_sources_are_rejected(self) -> None:
        self.create_head()
        (self.sources / "operator-head.webp").write_bytes(b"not selected silently")
        with self.assertRaisesRegex(ArtError, "both PNG and WebP"):
            self.prepare_quietly()
        self.assertFalse(self.outputs.exists())

    def test_malformed_source_has_actionable_cli_failure(self) -> None:
        (self.sources / "vanilla-surface.png").write_bytes(b"invalid PNG")
        result = subprocess.run(
            [sys.executable, str(ROOT / "scripts" / "prepare_planetary_art.py"),
             "--source-dir", str(self.sources)], capture_output=True, text=True, check=False,
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("vanilla-surface.png", result.stderr)
        self.assertIn("cannot decode/validate", result.stderr)
        self.assertNotIn("Traceback", result.stderr)
        self.assertFalse(self.outputs.exists())

    def test_stale_generated_layer_removed_without_touching_unrelated_files(self) -> None:
        source = self.create_head()
        self.prepare_quietly()
        unrelated = self.outputs / "artist-notes.txt"
        unrelated.write_text("keep this", encoding="utf-8")
        source.unlink()
        self.prepare_quietly()
        self.assertFalse((self.outputs / "operator-head.png").exists())
        self.assertEqual(unrelated.read_text(encoding="utf-8"), "keep this")

    def test_source_directory_cannot_be_used_as_output(self) -> None:
        with self.assertRaisesRegex(ArtError, "originals are preserved"):
            prepare(self.sources, self.sources)


if __name__ == "__main__":
    unittest.main()
