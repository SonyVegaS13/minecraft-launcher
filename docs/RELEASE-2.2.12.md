# Solaris Neon 2.2.12 — Unified launch artwork

## User request
Use the SOLARIS cosmic artwork when launching both **Vanilla** and **Modded** through the already-installed Solaris launcher. The update must arrive via the existing `neon-v*` channel; users should not have to unpack a ZIP or reinstall.

## Implementation
- A fullscreen, dismissible SOLARIS WPF launch cover appears immediately before starting Java for BOTH game modes. It uses the launcher-bundled `Assets/SolarisCosmicHome.jpg` (already packaged as a WPF resource). It optionally picks up `SolarisDirectories.StateDir/solaris-loading.png` if available.
- The cover has an indeterminate progress indicator; users may press Esc or click "Пропустить" to hide it. It automatically disappears when a fresh `logs/latest.log` reports game initialization or when Java exits, with a 120-second failsafe.
- Modded retains its existing Simple Custom Early Loading Forge integration and the confirmed owner-supplied direct-connect IP `solarismodded.millida.host:25565`. Vanilla stays unmodified and still opens Minecraft's regular main menu.
- **Vanilla limitation:** since this is an unmodded client, an independent launcher overlay can *cover* Mojang's early loading sequence but cannot patch the game's own internal splash pixels. The cover may briefly transition to Mojang's splash depending on startup timing. Do not market this as a true Mojang renderer replacement.
- The artwork that is physically bundled in this revision is the EXISTING approved Solaris cosmic asset. **The newly generated image attached in the conversation has not been transferred into the GitHub repository.** Do not claim that the binary includes its exact new bytes.

## Validation
GitHub Actions WPF CI must succeed. Additional manual Windows tests must confirm fullscreen sizing, Esc dismiss, timeout, real main-menu readiness and no overlay left after Java failure, Vanilla offline, Modded 1.20.1 Forge 47.4.20 with the early-loading addon, and safe automatic update from 2.2.11 to 2.2.12.

## Release
The branch is intentionally a staging branch. Verify the PR checks before merging into `solaris-2.2-dev`, then create a `release:` commit to publish `neon-v2.2.12`. The installed Solaris updater sees this tag and should prompt to download, verify SHA256, replace its own EXE in place, and relaunch.

## Release qualification

Windows CI succeeded on the PR: https://github.com/SonyVegaS13/minecraft-launcher/actions/runs/38748696867 and https://github.com/SonyVegaS13/minecraft-launcher/actions/runs/38748696933 . Both launcher build and portable Windows EXE publish succeeded. This does not prove that Vanilla / Modded launched correctly on a player's Windows machine. Manual in-game verification remains pending. The exact new user-uploaded image cannot be claimed to be embedded until the binary asset is transferred to repository; the bundled existing Solaris cosmic JPEG is used at this stage.
