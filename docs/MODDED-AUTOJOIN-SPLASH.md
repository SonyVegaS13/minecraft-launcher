# Solaris Modded autojoin and Minecraft loading screen

## Autojoin: implemented in this branch

Current configured Modded server: `solarisplay.millida.host:25565`. The launcher already performs its status checks against this host. `MainWindow.xaml.cs` now uses the same endpoint for `ModdedServerHost`, passed to `LaunchMinecraftAsync` through CmlLib `MLaunchOption.ServerIp` and `ServerPort`. On the Modded **Play** action, the client is expected to join the server rather than stay on the title screen. The **Update** action is unchanged and does not automatically start Minecraft. Vanilla remains unchanged and launches to its main menu.

This change has not been runtime-tested on a Windows client and the host address must be confirmed against the actual Modded panel before a stable release. Network errors, authentication rules, incompatible mods, or server downtime may still prevent joining.

## Red Mojang / Forge loading background: not implemented yet

If the request refers to the **Minecraft game window** rather than a Solaris Launcher WPF loading panel, the image must be integrated into the actual Minecraft 1.20.1 Forge client. Adding an image to `MainWindow.xaml` does not replace the game's red Mojang screen.

Candidate: a Forge 1.20.1 client loading-screen mod such as **Drippy Loading Screen** (with compatible FancyMenu and other required dependencies) or **Simple Splash Screen**, installed and configured as part of the managed Modded client pack. Choose a user-approved image, test Forge 47.4.20 compatibility and every stage of startup on Windows, and verify the actual covered loading phases. Some Forge pre-loading screens may remain uncustomizable by particular mods. Respect redistribution terms for mod JARs and ensure the pack manifest/update mechanism carries the correct licensed assets. Do not change Vanilla, built-in artwork, updater, or pre-existing files without dedicated testing.

## Verification checklist

1. Verify Modded Play builds Java command line with server IP and port 25565.
2. Confirm direct entry into hosted Modded when reachable; test unavailable-server UX.
3. Confirm Vanilla behavior and Update flow unaffected.
4. After selecting approved image and a compatible splash mod, test launch from clicking Play through world login on Windows; check red background is replaced where technically possible.
5. Run dotnet build and publish and commit verified results before any stable release.
