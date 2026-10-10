using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    // Neon releases are independent of the legacy 2.1.x production channel.
    // Only deliberately published `neon-vX.Y.Z` releases are offered as updates.
    private const string NeonReleasesUrl =
        "https://api.github.com/repos/SonyVegaS13/minecraft-launcher/releases?per_page=60";
    // Separate, opt-in channel. A DEV release is published on successful
    // Windows CI builds and never offered to installed production players.
    private const string DevTagPrefix = "neon-dev-v";

    private sealed record NeonUpdate(Version Version, string DownloadUrl, string ChecksumUrl);
    private bool _updateOperationRunning;

    private async Task CheckForUpdatesAsync()
    {
        if (_updateOperationRunning || App.LaunchedAfterRecovery || App.PendingUpdateAcknowledgement is not null)
            return;
        _updateOperationRunning = true;
        try
        {
            NeonUpdate? update = App.IsDevChannel
                ? await FindLatestDevNeonUpdateAsync()
                : await FindLatestNeonUpdateAsync();
            if (update is null || !SolarisSafeUpdate.CanOffer(update.Version.ToString()))
                return;

            MessageBoxResult answer = MessageBox.Show(
                $"Доступна новая версия Solaris Neon {(App.IsDevChannel ? "DEV " : "")}{update.Version}.\n\n" +
                $"Установлена версия {(App.IsDevChannel ? InstalledFileVersion() : LauncherVersion)}.\n\n" +
                "Скачать проверенное обновление и перезапустить Solaris?\n" +
                "Аккаунт, Minecraft и настройки сохранятся.",
                "Solaris — обновление",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    await StartUpdateAsync(update);
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Не удалось обновить Solaris — можно продолжать играть.";
                    MessageBox.Show(
                        "Обновление не установлено. Текущая версия сохранена.\n\n" + ex.Message,
                        "Solaris — ошибка обновления",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            // Offline mode / API rate limits must never prevent using Solaris.
            try
            {
                Directory.CreateDirectory(Path.Combine(_stateDir, "logs"));
                File.AppendAllText(Path.Combine(_stateDir, "logs", "update.log"),
                    $"[{DateTimeOffset.Now:O}] {ex}\n");
            }
            catch { /* Optional diagnostics. */ }
        }
        finally
        {
            _updateOperationRunning = false;
        }
    }

    private async Task<NeonUpdate?> FindLatestNeonUpdateAsync()
    {
        using var response = await _http.GetAsync(NeonReleasesUrl);
        response.EnsureSuccessStatusCode();
        await using Stream data = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(data);

        if (!Version.TryParse(LauncherVersion, out Version? current))
            throw new InvalidOperationException("Неверная версия Solaris.");

        NeonUpdate? best = null;
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;

            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("neon-v", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Version.TryParse(tag["neon-v".Length..], out Version? version) ||
                version is null || version.CompareTo(current) <= 0 ||
                (best is not null && best.Version.CompareTo(version) >= 0))
                continue;

            string? exe = null;
            string? sha = null;
            foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) ||
                    address.Scheme != Uri.UriSchemeHttps ||
                    !address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name == "SolarisLauncher.exe") exe = url;
                if (name == "SHA256SUMS.txt") sha = url;
            }

            // Never offer unverified builds or legacy releases.
            if (exe is not null && sha is not null)
                best = new NeonUpdate(version, exe, sha);
        }
        return best;
    }

    private static Version InstalledFileVersion()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить версию Solaris.");
        var file = FileVersionInfo.GetVersionInfo(exe);
        return new Version(file.FileMajorPart, file.FileMinorPart,
            file.FileBuildPart, file.FilePrivatePart);
    }

    private async Task<NeonUpdate?> FindLatestDevNeonUpdateAsync()
    {
        using var response = await _http.GetAsync(NeonReleasesUrl);
        response.EnsureSuccessStatusCode();
        await using Stream data = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(data);

        Version installed = InstalledFileVersion();
        NeonUpdate? newest = null;
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean())
                continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith(DevTagPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Version.TryParse(tag[DevTagPrefix.Length..], out Version? version) ||
                version is null || version.Revision < 0 ||
                version.CompareTo(installed) <= 0 ||
                (newest is not null && newest.Version.CompareTo(version) >= 0))
                continue;

            string? executable = null, checksum = null;
            foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
            {
                string assetName = asset.GetProperty("name").GetString() ?? "";
                string assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out Uri? uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (assetName == "SolarisLauncher.exe") executable = assetUrl;
                if (assetName == "SHA256SUMS.txt") checksum = assetUrl;
            }
            if (executable is not null && checksum is not null)
                newest = new NeonUpdate(version, executable, checksum);
        }
        return newest;
    }

    private async Task StartUpdateAsync(NeonUpdate update)
    {
        string currentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не найден файл запущенного лаунчера.");
        string updatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            App.IsDevChannel ? "Solaris-Neon-Dev" : "Solaris", "updates");
        Directory.CreateDirectory(updatesDir);
        string incoming = Path.Combine(updatesDir, $"Solaris-{Guid.NewGuid():N}.exe");
        string attemptId = Guid.NewGuid().ToString("N");
        SolarisSafeUpdate.Begin(update.Version.ToString(), attemptId);

        try
        {
            StatusText.Text = $"Скачиваем Solaris Neon {update.Version}...";
            Progress.Value = 0;
            await DownloadFileWithProgressAsync(
                update.DownloadUrl, incoming, 0, 100, CancellationToken.None);

            if (!File.Exists(incoming) || new FileInfo(incoming).Length < 1_000_000)
                throw new InvalidDataException("Загруженный EXE повреждён или пуст.");

            // Verify bytes against the hash published together with this exact release.
            string checksums = await _http.GetStringAsync(update.ChecksumUrl);
            var match = Regex.Match(checksums,
                @"(?im)^([0-9a-f]{64})\s+\*?SolarisLauncher\.exe\s*$");
            if (!match.Success)
                throw new InvalidDataException("Нет контрольной суммы SolarisLauncher.exe в релизе.");

            await using (FileStream stream = File.OpenRead(incoming))
            {
                byte[] hash = await SHA256.HashDataAsync(stream);
                string actual = Convert.ToHexString(hash);
                if (!actual.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA256 не совпадает — обновление отменено.");
            }

            StatusText.Text = "Файл проверен. Перезапускаем Solaris...";
            var startInfo = new ProcessStartInfo
            {
                FileName = currentPath,
                WorkingDirectory = Path.GetDirectoryName(currentPath)!,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("--self-update");
            startInfo.ArgumentList.Add(currentPath);
            startInfo.ArgumentList.Add(incoming);
            startInfo.ArgumentList.Add(update.Version.ToString());
            startInfo.ArgumentList.Add(attemptId);
            if (App.IsDevChannel) startInfo.ArgumentList.Add("--dev-channel");

            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Не удалось запустить помощник обновления.");

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SolarisSafeUpdate.MarkFailed(update.Version.ToString(), attemptId, ex);
            try { File.Delete(incoming); } catch { }
            throw;
        }
    }
}
