using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

// Per-server state is deliberately separate from the Solaris EXE updater.
// Checking and installing never starts Minecraft: the player decides when to play.
public partial class MainWindow
{
    private enum ServerClientState { Checking, UpdateRequired, Ready, CheckFailed }
    private ServerClientState _vanillaClientState = ServerClientState.Checking;
    private ServerClientState _moddedClientState = ServerClientState.Checking;
    private string _vanillaTargetVersion = VanillaVersion;
    private bool _serverStateRefreshRunning;
    private DateTimeOffset _moddedLastRemoteCheck = DateTimeOffset.MinValue;

    private static string ForgeClientVersionName => $"{MinecraftVersion}-forge-{ForgeVersion}";

    private bool HasVanillaClientFiles(string version)
    {
        string directory = Path.Combine(_stateDir, "vanilla", "versions", version);
        return File.Exists(Path.Combine(directory, version + ".json")) &&
               File.Exists(Path.Combine(directory, version + ".jar"));
    }

    private bool HasForgeClientFiles()
    {
        string version = ForgeClientVersionName;
        return File.Exists(Path.Combine(_gameDir, "versions", version, version + ".json"));
    }

    private async Task<bool> IsVanillaClientInstalledAsync(string version)
    {
        if (!HasVanillaClientFiles(version)) return false;
        return await Task.Run(() =>
            !string.IsNullOrWhiteSpace(FindJavaForMode(Path.Combine(_stateDir, "vanilla"), 25)));
    }

    private async Task<bool> HasModdedRuntimeAsync()
    {
        if (!HasForgeClientFiles()) return false;
        return await Task.Run(() => !string.IsNullOrWhiteSpace(FindJavaForMode(_gameDir, 17)));
    }

    private void SetVanillaState(ServerClientState state, string description)
    {
        _vanillaClientState = state;
        if (VanillaInstallStateText is not null) VanillaInstallStateText.Text = description;
        RefreshServerActionButtons();
    }

    private void SetModdedState(ServerClientState state, string description)
    {
        _moddedClientState = state;
        if (ModdedInstallStateText is not null) ModdedInstallStateText.Text = description;
        RefreshServerActionButtons();
    }

    private void RefreshServerActionButtons()
    {
        if (VanillaPlayButton is null || PlayButton is null) return;
        UpdateServerActionButton(VanillaPlayButton, _vanillaClientState);
        UpdateServerActionButton(PlayButton, _moddedClientState);
    }

    private void UpdateServerActionButton(Button button, ServerClientState state)
    {
        if (_launchCancellation is not null && ReferenceEquals(button, _activeLaunchButton))
            return; // Preserve the working CANCEL button until the operation finishes.

        if (_installingLauncherUpdate || (_launchCancellation is not null &&
            !ReferenceEquals(button, _activeLaunchButton)))
        {
            button.IsEnabled = false;
            return;
        }

        button.IsEnabled = state != ServerClientState.Checking;
        button.Content = state switch
        {
            ServerClientState.Ready => "▶  ИГРАТЬ",
            ServerClientState.UpdateRequired => "↓  ОБНОВИТЬ",
            ServerClientState.CheckFailed => "↻  ПРОВЕРИТЬ",
            _ => "ПРОВЕРКА..."
        };
    }

    // The vanilla server advertises a Minecraft version in its status response.
    // Only accept an exact numeric release name. Unknown/custom descriptions
    // leave the tested launcher default intact rather than guessing a version.
    private void ApplyVanillaServerVersion(string? advertisedVersion)
    {
        if (string.IsNullOrWhiteSpace(advertisedVersion) ||
            !System.Text.RegularExpressions.Regex.IsMatch(advertisedVersion,
                @"^\d{1,4}\.\d{1,4}(?:\.\d{1,4})?$") ||
            string.Equals(_vanillaTargetVersion, advertisedVersion, StringComparison.OrdinalIgnoreCase))
            return;

        _vanillaTargetVersion = advertisedVersion;
        if (VanillaVersionText is not null)
            VanillaVersionText.Text = $"Minecraft {advertisedVersion} • Survival";
        // Update the Vanilla action even if Modded is still checking its pack.
        // Separate server checks must not block one another.
        if (_launchCancellation is null)
            _ = RefreshVanillaClientStateAsync();
    }

    private async Task RefreshVanillaClientStateAsync()
    {
        string version = _vanillaTargetVersion;
        SetVanillaState(ServerClientState.Checking, "Проверяем установленную версию...");
        bool ready;
        try { ready = await IsVanillaClientInstalledAsync(version); }
        catch { ready = false; }
        if (!string.Equals(version, _vanillaTargetVersion, StringComparison.Ordinal))
        {
            await RefreshVanillaClientStateAsync();
            return;
        }
        SetVanillaState(ready ? ServerClientState.Ready : ServerClientState.UpdateRequired,
            ready ? $"Версия {version} готова" : $"Требуется установка {version}");
    }

    private async Task RefreshModdedClientStateAsync(CancellationToken token,
        bool showChecking = true)
    {
        if (showChecking)
            SetModdedState(ServerClientState.Checking, "Проверяем релиз и моды...");
        try
        {
            var release = await GetLatestClientReleaseAsync(token);
            string tag = release.TagName;
            string stateFile = Path.Combine(_stateDir, "client-release.txt");
            string localTag = File.Exists(stateFile)
                ? (await File.ReadAllTextAsync(stateFile, token)).Trim() : "";
            VersionText.Text = $"Сборка: {tag}";

            bool packReady = string.Equals(localTag, tag, StringComparison.OrdinalIgnoreCase) &&
                await ValidateManagedPackManifestAsync(tag, token);
            bool runtimeReady = packReady && await HasModdedRuntimeAsync();
            if (runtimeReady)
                SetModdedState(ServerClientState.Ready, $"Сборка {tag} готова");
            else
                SetModdedState(ServerClientState.UpdateRequired,
                    packReady ? "Нужна установка Forge / Java" : $"Доступна сборка {tag}");
            _moddedLastRemoteCheck = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // A release lookup can fail when offline or GitHub is throttling.
            // Never silently install an unknown pack. Preserve offline play
            // only for an existing client whose local hashes are valid.
            try
            {
                string stateFile = Path.Combine(_stateDir, "client-release.txt");
                string localTag = File.Exists(stateFile)
                    ? (await File.ReadAllTextAsync(stateFile, token)).Trim() : "";
                bool localReady = !string.IsNullOrWhiteSpace(localTag) &&
                    await ValidateManagedPackManifestAsync(localTag, token) &&
                    await HasModdedRuntimeAsync();
                SetModdedState(localReady ? ServerClientState.Ready : ServerClientState.CheckFailed,
                    localReady ? "GitHub недоступен — локальная сборка готова"
                        : "Не удалось проверить сборку — повторите");
            }
            catch (OperationCanceledException) { throw; }
            catch { SetModdedState(ServerClientState.CheckFailed, "Ошибка проверки — повторите"); }
        }
    }

    private async Task RefreshServerStatesAsync()
    {
        if (_serverStateRefreshRunning || _launchCancellation is not null || _installingLauncherUpdate)
            return;
        _serverStateRefreshRunning = true;
        try
        {
            await RefreshVanillaClientStateAsync();
            await RefreshModdedClientStateAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            SetModdedState(ServerClientState.CheckFailed, "Ошибка проверки — повторите");
        }
        finally
        {
            _serverStateRefreshRunning = false;
            RefreshServerActionButtons();
        }
    }

    private async Task InstallVanillaClientAsync(string version, CancellationToken token)
    {
        string vanillaDir = Path.Combine(_stateDir, "vanilla");
        Directory.CreateDirectory(vanillaDir);
        StatusText.Text = $"Устанавливаем Solaris Vanilla {version}...";
        Progress.Value = 5;
        var launcher = new CmlLib.Core.MinecraftLauncher(new CmlLib.Core.MinecraftPath(vanillaDir));
        WireMinecraftProgress(launcher, "Vanilla", 10, 84);
        await launcher.InstallAsync(version, token);
        token.ThrowIfCancellationRequested();
        await EnsureJavaForModeAsync(vanillaDir, 25, 85, 97, token);
        token.ThrowIfCancellationRequested();
        if (!await IsVanillaClientInstalledAsync(version))
            throw new InvalidDataException("Minecraft Vanilla установлен не полностью.");
        Progress.Value = 100;
        StatusText.Text = $"Solaris Vanilla {version} обновлён. Теперь нажмите «Играть».";
        SetVanillaState(ServerClientState.Ready, $"Версия {version} готова");
    }

    private async Task InstallModdedClientAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_gameDir);
        StatusText.Text = "Обновляем сборку Solaris Modded...";
        Progress.Value = 5;
        await UpdateClientPackAsync(token);
        token.ThrowIfCancellationRequested();
        StatusText.Text = "Подготавливаем Minecraft, Java 17 и Forge...";
        Progress.Value = 41;
        string javaPath = await EnsureJavaForModeAsync(_gameDir, 17, 41, 47, token);
        await EnsureForgeAsync(javaPath, token);
        token.ThrowIfCancellationRequested();
        StatusText.Text = "Проверяем установленную сборку...";
        await RefreshModdedClientStateAsync(token, showChecking: false);
        if (_moddedClientState != ServerClientState.Ready)
            throw new InvalidDataException("Сборка ещё не прошла проверку, запуск заблокирован.");
        Progress.Value = 100;
        StatusText.Text = "Solaris Modded обновлён. Теперь нажмите «Играть».";
    }
}
