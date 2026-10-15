# Solaris Neon 2.2.13 — release qualification

## Verified before publication
- Windows WPF build succeeded (PR #13).
- Windows portable executable publish succeeded (PR #13).
- The exact SHA-256 server mod manifest remains authoritative for all server-managed game jars.
- Only the pinned publisher-verified Forge splash jar is separated from server modpack validation.
- Existing installations with an untracked, valid splash JAR migrate its SHA-512 trust record on first check.
- Unexpected / modified JARs are not globally allowed.
- Solaris Modded server host remains `solarismodded.millida.host:25565`.
- Both regular and DEV channel upgrades are in-place, with automatic SHA256 verification.

Windows Minecraft runtime test has not been done; the user should reopen Solaris and confirm that the Modded button remains `ИГРАТЬ` across two launches.
