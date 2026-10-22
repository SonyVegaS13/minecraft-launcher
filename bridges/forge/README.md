# Solaris ID — Forge SERVER Auth Bridge

Target: Minecraft **1.20.1**, Forge **47.4.20**, Java **17**. This is a separate server mod; it is not copied into, or used to rebuild, the Solaris Modded client pack. No existing server addresses/configurations are edited by this module.

## Authentication

Each actual join creates a new connection through the shared `ConnectionCoordinator`. Until the independent Solaris ID API approves that connection, the player is quarantined. The server displays only a random **locator code**; the player approves that code using their logged-in Solaris Launcher. Passwords, refresh tokens and one-use game tickets never enter Minecraft chat, game arguments or bridge logs. Nickname, IP address and offline Minecraft UUID never count as authentication.

The shared HTTPS client uses server-only credentials and audience restriction. Async network completions are dispatched onto the main server thread. Deadlines, API denial/unavailability, revocation/ban and invalid responses fail closed; disconnect/shutdown cancels polling and closes the trusted gameplay session. Heartbeats send monotonic sequence numbers, never client duration claims.

`SolarisForgeBridge.solarisId(ServerPlayer)` exposes the approved immutable Solaris UUID. It returns empty while quarantined. Existing Minecraft transport/offline UUIDs are not rewritten; this module does **not** claim inventory UUID migration or automatic in-world skin rendering. Those need dedicated integrations and backup planning.

## Quarantine

Forge events block commands/chat, attacks and damage, interactions, block changes, containers, pickups/drops, XP changes, mounting, dimension travel and projectiles. Tick handling keeps the player at their original location and zeroes velocity/fall distance. Approval is tied to the exact live `ServerPlayer` object as well as the fresh backend connection; an older player object with the same transport UUID cannot inherit it. Authenticated respawns explicitly replace that object binding.

A **required server-side Mixin** also blocks protected packet handlers before processing, including inventory/creative slots, world-edit/command-block packets, movement/vehicle controls, recipes and arbitrary play-state mod payloads. Events alone cannot enforce these restrictions against crafted inventory packets. An additional required advancement gate prevents pre-auth award/revoke/reward functions because Forge advancement notifications are not cancellable. Keepalive, login negotiation, teleport acknowledgement and disconnect remain available. A missing injection is a startup error, never an auth bypass.

No game mode, inventory, invulnerability or no-gravity flag is changed: an autosave/crash must not permanently persist temporary privileges. API/adapter failure denies access rather than treating the nickname as an authenticated identity.

## Configuration

Use the server process environment; never commit real values:

- `SOLARIS_ID_API_ORIGIN`: independent HTTPS origin, e.g. `https://id.example.invalid`.
- `SOLARIS_SERVER_ID`: the configured server audience in `SOLARIS_GAME_SERVERS`.
- `SOLARIS_SERVER_KEY`: high-entropy server-only key for that audience.

The bridge never prints values or exception text containing input. Missing/invalid settings deny joins. Do not expose backend credentials to clients. Provision a separate key for each game server. Deploy to a test server first; installing into working servers is outside this change's authorization.

## Build

From `bridges/forge`, using Java17 and Gradle8.1.1:

```sh
gradle --no-daemon clean build
```

ForgeGradle **6.0.54**, MixinGradle **0.7.38** and Mixin annotation processor **0.8.5** are pinned. Their official repositories and Maven Central supply dependencies. `gradle/verification-metadata.xml` pins SHA-256 checksums from the successfully resolved build; normal builds verify them. The one exact locally generated `_mapped_official_` Forge output is exempted because ForgeGradle writes current ZIP timestamps; its official upstream inputs remain verified by the Forge/Mojang pipeline. ForgeGradle also obtains version-pinned Minecraft artifacts through the official Mojang metadata and mapping pipeline. `../core/src/main/java` is compiled into this JAR; Forge already supplies Gson at runtime, so no separate core/Gson JAR is installed. Missing Mixin target warnings fail compilation (`-Werror`), and each runtime injector is independently required.

The cloud build was completed with verified Temurin17 and Gradle8.1.1, producing the real `reobfJar` and complete refmap. See [BUILD_STATUS.md](BUILD_STATUS.md) for evidence and the exact local command. Use `-PsolarisBuildDir=/tmp/solaris-id-forge-build` and a `/tmp` `GRADLE_USER_HOME` when workspace disk is limited. In proxy environments Java requires the platform proxy flags and trusted platform CA; TLS verification remains enabled.

## Acceptance tests still required in a real Forge server

- Unapproved users cannot move, use either inventory, change creative slots, execute commands, chat, alter blocks, pick/drop items or use a mod payload to reach protected gameplay.
- Approve the displayed code in the launcher; confirm the immutable Solaris UUID and trusted heartbeat time.
- Wrong nickname/audience, expired/replayed pass, API outage, logout/revocation, ban and rapid reconnect must deny/kick without reusing an old approval.
- Verify disconnect and shutdown close exactly once, and another login does not inherit a stale callback.
- Verify all installed modpack modules tolerate pre-auth play-state payload blocking. Mods that require gameplay payloads before approval may need explicit integration; the bridge does not permit a blanket bypass. Server-side mod ticks, player-presence automation and arbitrary third-party listeners need their own review: a packet/event action gate is not a complete sandbox for every server mod.
- Offline-mode player data and world packets may already be loaded/sent before the join callback; this action quarantine does not promise inventory confidentiality before login or prevent duplicate-nickname login denial-of-service. Minecraft offline-mode transport itself is unencrypted. HTTPS approval does not add Minecraft packet confidentiality or claim protection from an active in-path game-protocol attacker. Use trusted/private game transport or a separately designed encrypted gateway before claiming that property.

Full automatic launch-to-login handshake, skin rendering and inventory migration are not implemented by this explicit-approval bridge.
