# SolarisLauncher 2.0

SolarisLauncher no longer starts the official Minecraft Launcher.

It uses:
- Minecraft 1.20.1
- Forge 47.4.20
- CmlLib.Core 4.0.6
- CmlLib.Core.Installer.Forge 1.1.1
- GitHub Releases as the source of the Solaris client pack

## 1. Create the client pack

Do NOT upload your whole `.minecraft` and do NOT upload the server folder.

The release archive should contain only client files, for example:

```
SolarisClient.zip
├── mods/
├── config/
├── resourcepacks/
├── shaderpacks/
├── defaultconfigs/
├── kubejs/
├── journeymap/
├── tacz/
├── xaero/
└── options.txt
```

Do not put these in the pack:
- saves/
- logs/
- crash-reports/
- screenshots/
- runtime/
- libraries/
- assets/
- versions/
- launcher_profiles.json
- usercache.json
- usernamecache.json

Minecraft/Forge/libraries/assets/Java are installed by CmlLib from the official distribution sources.

## 2. Publish on GitHub

Repository:
`https://github.com/SonyVegaS13/minecraft-launcher`

Create a GitHub Release, for example:

Tag: `v1.0.0`

Attach an asset named exactly:

`SolarisClient.zip`

The launcher always checks the latest published Release.

When you publish `v1.0.1` with a new `SolarisClient.zip`, users receive the new pack automatically.

## 3. Build

Install the .NET 8 SDK on the developer PC, then:

```
dotnet restore
dotnet build -c Release
```

For a standalone Windows build (player does not need .NET installed):

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 4. Server auto-connect

In `MainWindow.xaml.cs`, set:

```
private const string ServerHost = "YOUR_SERVER_IP";
private const int ServerPort = 25565;
```

Then Minecraft will be launched with the server connection parameters.

## Important

The current launcher uses an offline Minecraft session with the nickname typed by the user. It does NOT verify a custom nickname/password against a server. A real login system needs a separate authentication backend; this is intentionally not faked in the launcher.
