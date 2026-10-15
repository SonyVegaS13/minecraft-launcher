# Solaris Neon 2.2.12 — DEV in-app update

This commit triggers an independent developer-channel release so installed DEV users can receive the same 2.2.12 code without manually installing or downloading a ZIP. The regular stable release is already published as `neon-v2.2.12`.

- Stable updates: `neon-v2.2.12`; installer prompts the user to confirm the update.
- DEV updates: `neon-dev-v2.2.12.<revision>`; DEV auto-updater automatically accepts the newest release when idle.
- Both channels preserve existing Modded direct connect and offer branded Solaris artwork over Vanilla/Modded start.
- The included default artwork is `Assets/SolarisCosmicHome.jpg` already bundled with Solaris. The newly generated user-specific ChatGPT PNG is not present in GitHub and must not be advertised as bundled.
- The Vanilla version uses an independent temporary WPF launch cover because Vanilla itself has no Forge splash addon.
- No Minecraft runtime verification was performed on the user's Windows PC, though CI build and publish succeeded.
