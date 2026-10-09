# Solaris Neon 2.2.8 — approved Vanilla and Modded cards

The server cards use the **two exact PNG banners approved by the project owner** and uploaded to `Assets/` on the `solaris-2.2-dev` branch.

## Cards

- **Modded:** `Assets/Воксельная битва под пурпурным небом.png` → `Assets/SolarisModdedCard.png`. The illustration features an ominous Wither Storm, distant Wither, Create machinery, a railway, and modded-world scenery.
- **Vanilla:** `Assets/Изображение ChatGPT 9 окт. 2026 г., 17_24_22.png` → `Assets/SolarisVanillaCard.png`. The illustration depicts peaceful Minecraft-style survival scenery at sunset.

The build script `scripts/prepare_card_art.py` verifies PNG geometry and **copies the original bytes unchanged** into the bundled WPF resources. No image regeneration, cropping, or online downloads are required at runtime.

## Integration

- `NeonArtwork.cs` applies approved cards **after** imported theme images, so an older cached `Solaris-Neon-UI.zip` cannot accidentally replace the new Vanilla/Modded illustrations.
- Existing crossfade-on-hover and cached soft-thumbnail behavior remain intact.
- Login/home retain the Neon 2.2.7 background and the official Solaris logo.
- Version updated in project, visible UI, and launcher update comparison to **2.2.8**.
- Release tag: `neon-v2.2.8`. The in-app updater discovers it from GitHub Releases, verifies the downloaded EXE against `SHA256SUMS.txt`, then replaces the installed executable while retaining user account, themes, and Minecraft installation.

## Verification

The `solaris-2.2-dev` pre-release branch workflow completed all image preparation, .NET build, publish, and artifact-upload steps successfully before the release commit.

To test from an existing 2.2.7 installation, run Solaris using the desktop shortcut, approve the proposed 2.2.8 update, wait for the restart, and inspect both cards and the version label.
