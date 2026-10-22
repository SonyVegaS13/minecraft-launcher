# Observed verification status — 2026-10-22

- **Real Forge build passed.** Official MinecraftForge **1.20.1-47.4.20**, official Minecraft1.20.1 mappings, Temurin **17.0.20.1+1**, Gradle **8.1.1**, ForgeGradle **6.0.54**, MixinGradle **0.7.38**, Mixin processor **0.8.5**. No API stubs or substitute Minecraft classes.
- Final command completed **12 executed tasks** in **12 seconds**, including `compileJava`, `jar` and **`reobfJar`**, with strict SHA-256 dependency verification and compiler warnings treated as errors. Log: `/tmp/solaris-id-forge-build-final.log`. Forge has no standalone adapter tests (`test NO-SOURCE`); this is a build result, not an adapter test-suite pass. Shared-core lifecycle tests are reported separately.
- Produced `/tmp/solaris-id-forge-build/libs/solaris-id-forge-1.20.1-0.1.0.jar` (35793 bytes), SHA-256 `16ca0dd5f2ecd7cc0b35ef418ee5ccc55229950cf815271867d3c2fee903d0a6`. This review artifact is not installed into a server/client pack.
- Inspected the final packaged `solaris_id_bridge.refmap.json`: **39/39** protected packet handlers and **2/2** advancement handlers have SRG target descriptors; manifest registers the required Mixin config; the shared core is bundled. Reobfuscated Shadow fields also match the real targets. A first full build exposed four incorrect query/recipe handler names, which were corrected against the actual mapped Forge class before this final clean build.
- Pinned released Gradle plugins and saved `gradle/verification-metadata.xml`. The exact locally generated `net.minecraftforge:forge:1.20.1-47.4.20_mapped_official_1.20.1` output is narrowly exempted from module hashes because it contains current ZIP timestamps. Its official upstream Forge/Mojang inputs still use ForgeGradle verification; downloaded Gradle modules retain SHA-256 checks. Both tool distributions were verified against their official SHA-256 values during setup. Forge/Mojang/Maven downloads used HTTPS; the authoritative environment proxy CA was imported into the local JDK trust store, without disabling certificate verification. Initial network/tool availability limitations are resolved.
- **Real Forge server/client E2E: not run.** No claim that Mixins have been exercised against actual malicious game packets or that the installed server modpack has been validated. A dedicated test Forge47.4.20 server, normal Minecraft1.20.1 game client, HTTPS Solaris ID API, PostgreSQL and separate server credentials are still required for the README acceptance cases. Production Modded configuration, world, client pack and launch paths were not changed.

## Reproduce in this cloud instance

```sh
cd /workspace/minecraft-launcher/bridges/forge
JAVA_HOME=/workspace/.solaris-environment/tools/jdk-17 \
GRADLE_USER_HOME=/tmp/solaris-id-forge-gradle \
/workspace/.solaris-environment/tools/gradle-8.1.1/bin/gradle \
  --no-daemon \
  -Dhttps.proxyHost=proxy -Dhttps.proxyPort=8080 \
  -Dhttp.proxyHost=proxy -Dhttp.proxyPort=8080 \
  -PsolarisBuildDir=/tmp/solaris-id-forge-build \
  clean build --dependency-verification strict
```

The `/tmp` paths keep caches and large generated files off the limited workspace filesystem. Future environments must restore/setup the verified JDK/Gradle and trusted platform proxy configuration first. Normal non-proxy developer machines omit these environment-specific proxy flags.
