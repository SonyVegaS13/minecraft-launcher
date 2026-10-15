# Solaris Neon 2.2.13 — Modded update loop hotfix

## Root cause
Previous launcher versions 2.2.11–2.2.12 download `SimpleCustomEarlyLoading-2.2-forge.jar` to the Modded game `mods` directory whenever the player clicks Play. The strict server-pack manifest validation rejected **every unlisted JAR** as pack corruption, so on subsequent launcher starts it always displayed `ОБНОВИТЬ`. Re-installing the pack quarantined the splash JAR; the next launch added it again, causing an endless cycle.

## Fix
1. Keep checking **all server-managed JARs** against the original SHA-256 manifest, without relaxing their integrity checks.
2. Recognize exactly one launcher-installed optional client-only splash JAR by absolute path and the expected publisher SHA-512 digest.
3. Store the verified SHA-512 in the user's Solaris state directory when the launcher downloads the signed publisher JAR from pinned Modrinth release `Un816pXM` (Forge 1.20.1, v2.2-forge).
4. Migrate existing 2.2.11/2.2.12 installations: when the known optional JAR is present but there is no marker, fetch the official version metadata once, compare local SHA-512 and cache the verified digest.
5. Preserve only that verified optional JAR when repairing/updating the server-controlled modpack. Unexpected JARs are still rejected/quarantined as before.
6. Preserve Modded autojoin (`solarismodded.millida.host:25565`), the branded splash for Modded/Vanilla, user account and saves, updater, and Forge configuration.

## Expected acceptance test (on Windows)
- With installed Modded server pack plus correct SimpleCustomEarlyLoading add-on, launch Solaris twice: **ИГРАТЬ** must remain the action, not **ОБНОВИТЬ**.
- Clicking ИГРАТЬ still opens Minecraft and autojoins Modded.
- After actual server modpack release update, `ОБНОВИТЬ` appears exactly when needed; installed server modpack integrity remains strict.
- Introducing an unexpected `rogue.jar` makes pack verification fail as before.
- If the optional splash JAR bytes are changed, hash verification fails; do not silently allow modified executable content.
- Verify the standalone Vanilla branch remains unchanged.
- Test all existing launcher updater/recovery paths.

The tests in GitHub Actions validate .NET 8 Windows build/publish, not an actual gameplay session; runtime verification still required. Publish as `neon-v2.2.13` and continuous `neon-dev-v2.2.13.<revision>` through the usual updater channels, not as a mandatory manual ZIP.
