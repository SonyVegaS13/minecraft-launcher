# Solaris Neon 2.2.9 — stronger Vanilla/Modded card blur

## User-requested visual adjustment

Following successful in-app auto-update to Solaris 2.2.8, increase the **idle background blur** of both Minecraft server cards.

- Vanilla and Modded preserve the **exact original two user-approved PNG illustrations** in `Assets` (no generation, editing, resizing, or cropping of source images).
- Idle state: downsample a cached preview to **360 pixels wide** and apply **two precomputed box-blur passes** (horizontal and vertical) with **radius 7**.
- Hover state: preserve the original sharp illustration with the existing opacity crossfade (**185ms in / 235ms out**) and cached `BitmapCache` rendering.
- No live WPF blur effects, shaders, or additional GPU render passes while moving the mouse.
- All other UI artwork, installed Minecraft files, local account, settings and auto-updater are unchanged.

## Build / publish

The branch build passed image preparation, .NET restore/build, single-file Windows EXE publishing, ZIP packaging, SHA256 generation, and artifact upload prior to this release commit.

GitHub prerelease `neon-v2.2.9` publishes `SolarisLauncher.exe`, ZIP, and `SHA256SUMS.txt`. Existing Solaris Neon 2.2.8 checks for the new version on startup, asks permission, verifies SHA256, and installs in-place.

To validate visually, hover and leave both Vanilla and Modded cards after updating, comparing the blur against version 2.2.8.
