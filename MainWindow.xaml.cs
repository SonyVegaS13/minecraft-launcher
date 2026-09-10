using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.ProcessBuilder;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow : Window
{
    private const string MinecraftVersion = "1.20.1";
    private const string ForgeVersion = "47.4.20";

    // Your public GitHub repository. The launcher reads the latest published Release.
    private const string GitHubOwner = "SonyVegaS13";
    private const string GitHubRepo = "minecraft-launcher";
    private const string ClientPackAssetName = "SolarisClient.zip";

    // Optional: put your Minecraft server here later, e.g. "123.123.123.123".
    // Leave empty to open Minecraft normally.
    private const string ServerHost = "";
    private const int ServerPort = 25565;

    private readonly string _gameDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Solaris", "game");

    private readonly string _stateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Solaris");

    private readonly HttpClient _http = new();

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SolarisLauncher/2.0");
        NickBox.Text = Environment.UserName;
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        string nick = NickBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(nick))
        {
            MessageBox.Show("Введите ник.", "SolarisLauncher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PlayButton.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(_stateDir);
            Directory.CreateDirectory(_gameDir);

            StatusText.Text = "Проверяем обновление сборки на GitHub...";
            Progress.Value = 5;

            await UpdateClientPackAsync();

            StatusText.Text = "Устанавливаем Minecraft 1.20.1 и Forge...";
            Progress.Value = 45;

            string versionName = await EnsureForgeAsync();

            StatusText.Text = "Запускаем Minecraft...";
            Progress.Value = 100;

            await LaunchMinecraftAsync(versionName, nick);
            StatusText.Text = "Minecraft запущен.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка";
            MessageBox.Show(ex.ToString(), "SolarisLauncher — ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PlayButton.IsEnabled = true;
        }
    }

    private async Task UpdateClientPackAsync()
    {
        var release = await GetLatestReleaseAsync();
        string tag = release.TagName;
        string stateFile = Path.Combine(_stateDir, "client-release.txt");
        string installedTag = File.Exists(stateFile) ? (await File.ReadAllTextAsync(stateFile)).Trim() : "";

        string packUrl = release.Assets
            .FirstOrDefault(a => string.Equals(a.Name, ClientPackAssetName, StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl
            ?? throw new InvalidOperationException(
                $"В GitHub Release {tag} не найден файл {ClientPackAssetName}.\n\n" +
                "Создай Release и добавь в него клиентскую сборку с таким именем.");

        VersionText.Text = tag;

        if (string.Equals(installedTag, tag, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(_gameDir, "mods")))
        {
            StatusText.Text = $"Сборка {tag} уже установлена.";
            Progress.Value = 40;
            return;
        }

        string tempZip = Path.Combine(Path.GetTempPath(), $"SolarisClient-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(), $"SolarisClient-{Guid.NewGuid():N}");

        try
        {
            StatusText.Text = $"Скачиваем сборку {tag} с GitHub...";
            await DownloadFileWithProgressAsync(packUrl, tempZip, 5, 35);

            StatusText.Text = "Распаковываем сборку...";
            Progress.Value = 36;
            Directory.CreateDirectory(tempExtract);
            ExtractZipSafely(tempZip, tempExtract);

            string sourceRoot = FindPackRoot(tempExtract);
            InstallManagedPack(sourceRoot);

            await File.WriteAllTextAsync(stateFile, tag);
            Progress.Value = 40;
        }
        finally
        {
            TryDeleteFile(tempZip);
            TryDeleteDirectory(tempExtract);
        }
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync()
    {
        string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
        using HttpResponseMessage response = await _http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub не вернул последнюю версию сборки. HTTP {(int)response.StatusCode} {response.StatusCode}.\n\n" +
                "Проверь, что в репозитории опубликован хотя бы один Release.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return release ?? throw new InvalidOperationException("GitHub вернул пустой ответ о Release.");
    }

    private async Task DownloadFileWithProgressAsync(string url, string destination, double min, double max)
    {
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync();
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, true);

        byte[] buffer = new byte[1024 * 128];
        long readTotal = 0;
        int read;

        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;

            if (total is > 0)
            {
                double ratio = Math.Clamp((double)readTotal / total.Value, 0, 1);
                Progress.Value = min + (max - min) * ratio;
            }
        }
    }

    private static void ExtractZipSafely(string zipFile, string destination)
    {
        string fullDestination = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        using ZipArchive archive = ZipFile.OpenRead(zipFile);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Архив сборки содержит небезопасный путь: " + entry.FullName);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static string FindPackRoot(string extractedDir)
    {
        // Preferred format: mods/, config/, resourcepacks/ at the archive root.
        if (Directory.Exists(Path.Combine(extractedDir, "mods")) ||
            Directory.Exists(Path.Combine(extractedDir, "config")))
            return extractedDir;

        string[] directories = Directory.GetDirectories(extractedDir);
        if (directories.Length == 1 &&
            (Directory.Exists(Path.Combine(directories[0], "mods")) ||
             Directory.Exists(Path.Combine(directories[0], "config"))))
            return directories[0];

        throw new InvalidOperationException(
            "Не удалось найти клиентскую сборку в SolarisClient.zip. Внутри архива должны находиться папки mods и/или config.");
    }

    private void InstallManagedPack(string sourceRoot)
    {
        // These are launcher-managed folders. Old files are removed so deleted mods/configs
        // from a newer release do not remain on the player's PC.
        string[] managedDirectories =
        {
            "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks",
            "kubejs", "journeymap", "tacz", "xaero", "patchouli_books", "scripts"
        };

        foreach (string relative in managedDirectories)
        {
            string source = Path.Combine(sourceRoot, relative);
            if (!Directory.Exists(source))
                continue;

            string destination = Path.Combine(_gameDir, relative);
            TryDeleteDirectory(destination);
            CopyDirectory(source, destination);
        }

        // Copy selected root-level client files, if they exist in the pack.
        string[] managedFiles =
        {
            "options.txt", "optionsof.txt", "servers.dat", "servers.dat_old"
        };

        foreach (string file in managedFiles)
        {
            string source = Path.Combine(sourceRoot, file);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(_gameDir, file), true);
        }
    }

    private async Task<string> EnsureForgeAsync()
    {
        var path = new MinecraftPath(_gameDir);
        var launcher = new MinecraftLauncher(path);

        var forgeInstaller = new ForgeInstaller(launcher);
        string versionName = $"1.20.1-forge-{ForgeVersion}";

        var options = new ForgeInstallOptions
{
    SkipIfAlreadyInstalled = true
};

        string installed = await forgeInstaller.Install(MinecraftVersion, ForgeVersion, options);
        versionName = installed;

        // Forge installation itself is not enough: CmlLib also installs vanilla assets,
        // libraries and the Java runtime needed to run the selected version.
        StatusText.Text = "Скачиваем библиотеки, ресурсы и Java...";
        Progress.Value = 82;
        await launcher.InstallAsync(versionName);

        Progress.Value = 92;
        return versionName;
    }

    private async Task LaunchMinecraftAsync(string versionName, string nick)
    {
        var path = new MinecraftPath(_gameDir);
        var launcher = new MinecraftLauncher(path);

        var options = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(nick),
            MaximumRamMb = 4096,
            MinimumRamMb = 1024,
            GameLauncherName = "SolarisLauncher",
            GameLauncherVersion = "2.0"
        };

        if (!string.IsNullOrWhiteSpace(ServerHost))
        {
            options.ServerIp = ServerHost;
            options.ServerPort = ServerPort;
        }

        var process = await launcher.BuildProcessAsync(versionName, options);
        process.Start();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);

        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private sealed class GitHubRelease
    {
        public string TagName { get; set; } = "";
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = "";
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
