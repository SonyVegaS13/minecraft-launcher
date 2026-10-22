# Solaris ID Minecraft bridges

The Paper plugin and Forge server mod implement the HTTPS protocol in [the contract](../docs/solaris-id/CONTRACT.md). They target **Minecraft 1.20.1 and Java 17**. Paper is for vanilla clients; Forge targets **47.4.20** with the server's existing compatible client modpack. Neither requires a Solaris client mod.

A join creates a new random backend connection ID. The player stays quarantined and receives a random code. In the authenticated Solaris Launcher, the account owner selects the configured server, obtains a single-use ticket over HTTPS, and approves the displayed code. Only backend approval releases that actual connection. Nicknames, addresses and Minecraft offline UUIDs never authenticate a player. A code locates a pending connection; it is not an account credential. No ticket, password or server key is sent through Minecraft chat or process arguments.

## Build and checks

Install a verified JDK 17 and Gradle 8.1.1. With `JAVA_HOME` pointing at that JDK:

```sh
gradle -p bridges --no-daemon clean :core:test :paper:build
gradle -p bridges/forge --no-daemon build
```

The first build resolves the real Paper `1.20.1-R0.1-SNAPSHOT` API from the official Paper repository, Gson 2.10.1, and JUnit 5. Forge uses the real ForgeGradle toolchain and reobfuscates its production server JAR. No Minecraft API stubs are used. JARs are produced in `bridges/paper/build/libs/` and `bridges/forge/build/libs/`. The Paper JAR includes shared protocol classes and Gson. Install only the adapter for the relevant server.

`core:test` checks deterministic connection lifecycle behavior and real Java HTTPS requests to an ephemeral TLS fixture, including trusted certificate handling, strict headers/routes, body limits, malformed/unauthorized responses, and rejection of redirects and unknown certificates. These tests do **not** start a Minecraft client or prove game interaction behavior. The separate `:core:realApiProbe` task can exercise the Java client against the actual ASP.NET API with its private integration configuration and a launcher-approval coordinator; see the test class for the private file schema. The configuration contains secrets and must stay outside the checkout and logs.

## Server configuration

Supply these environment variables to the server process through a secret manager or protected service environment:

| Variable | Purpose |
| --- | --- |
| `SOLARIS_ID_API_ORIGIN` | HTTPS API origin, with no path, query or embedded credentials. |
| `SOLARIS_SERVER_ID` | Exact audience ID configured in API `SOLARIS_GAME_SERVERS`. |
| `SOLARIS_SERVER_KEY` | That audience's unique random server-only key, at least 32 characters. |

The corresponding API server entry uses `mode: "vanilla"` for Paper or `mode: "modded"` for Forge. Do not reuse the same key across audiences. Never put keys in Minecraft commands, launcher arguments, committed configuration or logs. Production uses normal JVM certificate/hostname verification; a private certificate authority must be installed in the server JVM trust store. HTTP, redirects and insecure trust overrides are rejected.

Put the Paper JAR in the Paper 1.20.1 server's `plugins/` directory, or the Forge JAR in the Forge 47.4.20 server's `mods/` directory. This feature does not edit the existing server address, launch version, modpack, updater or player inventories. Test installation on a separate server first.

## Runtime behavior and integration API

The shared core bounds active connections to 256, requests to 512, response bodies to 16 KiB, and each HTTPS request to six seconds. Initial approval expires within 90 seconds. Pending connections poll every two seconds; approved connections poll for revocation every five seconds and heartbeat about every 15 seconds. Heartbeats and close requests contain only a monotonically increasing sequence. The API accounts elapsed time using its own clock; quarantine adds no trusted playtime. Any invalid response, timeout, denial, ban or session revocation withdraws the exposed identity immediately and queues a main-thread kick. The independent verification deadline is 30 seconds, below the API's 45-second stale cutoff.

Disconnect cancels polling/heartbeats, closes the trusted session, and deletes the backend connection. An already sent create request is allowed to drain for its bounded lifetime so a late backend insertion can also be deleted. Late network responses or queued main-thread callbacks cannot release a replacement connection. Shutdown withdraws all identities and drains cleanup asynchronously. Backend expiry bounds cleanup during a network outage.

Both adapters block movement, commands, chat, damage, interaction, inventory/creative changes, projectiles, pickups, vehicles and other gameplay until approved. Paper allows `/quit` as a local disconnect. All world/player changes run on the Minecraft main thread; HTTPS runs outside it. Configure these mandatory authentication adapters before optional gameplay plugins/mods and test the full installed set: another plugin or mod deliberately overriding event cancellation or removing the bridge is outside this boundary.

Integrations must use the immutable Solaris account ID:

* Paper: obtain the `SolarisPaperBridge` plugin instance and call `solarisId(Player): Optional<UUID>`.
* Forge: call `SolarisForgeBridge.solarisId(ServerPlayer): Optional<UUID>`.

The optional is empty outside an approved live connection. Minecraft's transport UUID is only a connection lookup key. Automatic skin rendering and inventory migration from offline UUIDs require separate integrations.

Real Paper/Forge server startup and an actual game client are separate E2E checks: join quarantine, code approval, every protected interaction, disconnect/reconnect, backend outage and ban/revoke kicks must be exercised against the final server plugin/mod set. Compilation, core tests and a real API HTTP probe must not be reported as those game-client checks.
