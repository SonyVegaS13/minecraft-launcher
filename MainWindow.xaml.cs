using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow : Window
{
    private const string MinecraftVersion = "1.20.1";
    private const string ForgeVersion = "47.4.20";

    private const string GitHubOwner = "SonyVegaS13";
    private const string GitHubRepo = "minecraft-launcher";
    private const string ClientPackAssetName = "SolarisClient.zip";

    // Заполни позже адресом своего сервера.
    private const string ServerHost = "";
    private const int ServerPort = 25565;

    private readonly string _gameDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Solaris", "game");

    private readonly string _stateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Solaris");

    private readonly string _accountFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Solaris", "account.json");

    private readonly HttpClient _http = new();

    private bool _registerMode;

    public MainWindow()
    {
        InitializeComponent();

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SolarisLauncher/3.0");

        Directory.CreateDirectory(_stateDir);

        RamText.Text = $"{(int)RamSlider.Value} MB";
        TryRestoreAccount();
    }

    // -------------------------
    // ЛОКАЛЬНЫЙ АККАУНТ
    // -------------------------

    private async void AuthActionButton_Click(object sender, RoutedEventArgs e)
    {
        string login = LoginBox.Text.Trim();
        string password = PasswordBox.Password;

        AuthStatus.Text = "";

        if (!IsValidLogin(login))
        {
            AuthStatus.Text = "Логин: 3–16 символов, только буквы, цифры и _.";
            return;
        }

        if (password.Length < 8)
        {
            AuthStatus.Text = "Пароль должен содержать минимум 8 символов.";
            return;
        }

        AuthActionButton.IsEnabled = false;
        SwitchAuthButton.IsEnabled = false;

        try
        {
            if (_registerMode)
                await RegisterLocalAsync(login, password);
            else
                await LoginLocalAsync(login, password);
        }
        catch (Exception ex)
        {
            AuthStatus.Text = ex.Message;
        }
        finally
        {
            AuthActionButton.IsEnabled = true;
            SwitchAuthButton.IsEnabled = true;
        }
    }

    private void SwitchAuthButton_Click(object sender, RoutedEventArgs e)
    {
        _registerMode = !_registerMode;

        AuthTitle.Text = _registerMode ? "Создание аккаунта" : "Вход в аккаунт";
        AuthActionButton.Content = _registerMode ? "СОЗДАТЬ АККАУНТ" : "ВОЙТИ";
        SwitchAuthButton.Content = _registerMode ? "У меня уже есть аккаунт" : "Создать аккаунт";
        AuthStatus.Text = "";
        PasswordBox.Clear();
    }

    private async Task RegisterLocalAsync(string login, string password)
    {
        if (File.Exists(_accountFile))
        {
            var existing = await ReadAccountAsync();
            if (existing is not null &&
                string.Equals(existing.Username, login, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Этот аккаунт уже зарегистрирован на этом компьютере.");
            }
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = HashPassword(password, salt);

        var account = new LocalAccount
        {
            Username = login,
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash)
        };

        await SaveAccountAsync(account);

        ShowMainView(login);
    }

    private async Task LoginLocalAsync(string login, string password)
    {
        LocalAccount? account = await ReadAccountAsync();

        if (account is null)
            throw new InvalidOperationException(
                "Аккаунт ещё не создан на этом компьютере.");

        if (!string.Equals(account.Username, login, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Неверный логин или пароль.");

        byte[] salt = Convert.FromBase64String(account.Salt);
        byte[] expected = Convert.FromBase64String(account.PasswordHash);
        byte[] actual = HashPassword(password, salt);

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidOperationException("Неверный логин или пароль.");

        ShowMainView(account.Username);
    }

    private async void TryRestoreAccount()
    {
        try
        {
            LocalAccount? account = await ReadAccountAsync();
            if (account is not null && !string.IsNullOrWhiteSpace(account.Username))
                ShowMainView(account.Username);
        }
        catch
        {
            // Повреждённый локальный файл не должен ломать запуск лаунчера.
        }
    }

    private void ShowMainView(string username)
    {
        AuthView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;

        WelcomeText.Text = username;
        ProfileText.Text = $"Игрок: {username}\nВерсия клиента: Minecraft {MinecraftVersion}";
        StatusText.Text = "Готов к запуску.";
        Progress.Value = 0;
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_accountFile))
                File.Delete(_accountFile);
        }
        catch
        {
            // Если файл нельзя удалить, всё равно возвращаемся на экран входа.
        }

        MainView.Visibility = Visibility.Collapsed;
        AuthView.Visibility = Visibility.Visible;

        LoginBox.Clear();
        PasswordBox.Clear();
        AuthStatus.Text = "";
        _registerMode = false;
        AuthTitle.Text = "Вход в аккаунт";
        AuthActionButton.Content = "ВОЙТИ";
        SwitchAuthButton.Content = "Создать аккаунт";

        await Task.CompletedTask;
    }

    private async Task<LocalAccount?> ReadAccountAsync()
    {
        if (!File.Exists(_accountFile))
            return null;

        await using FileStream stream = File.OpenRead(_accountFile);
        return await JsonSerializer.DeserializeAsync<LocalAccount>(stream);
    }

    private async Task SaveAccountAsync(LocalAccount account)
    {
        Directory.CreateDirectory(_stateDir);

        await using FileStream stream = File.Create(_accountFile);
        await JsonSerializer.SerializeAsync(stream, account,
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static byte[] HashPassword(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            210_000,
            HashAlgorithmName.SHA256,
            32);
    }

    private static bool IsValidLogin(string login)
    {
        if (login.Length < 3 || login.Length > 16)
            return false;

        return login.All(c =>
            char.IsLetterOrDigit(c) || c == '_');
    }

    // -------------------------
    // НАСТРОЙКИ RAM
    // -------------------------

    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RamText is not null)
            RamText.Text = $"{(int)e.NewValue} MB";
    }

    // -------------------------
    // ЗАПУСК MINECRAFT
    // -------------------------

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
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

            await LaunchMinecraftAsync(versionName, WelcomeText.Text.Trim());

            StatusText.Text = "Minecraft запущен.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка";
            MessageBox.Show(
                ex.ToString(),
                "Solaris Launcher — ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        string installedTag = File.Exists(stateFile)
            ? (await File.ReadAllTextAsync(stateFile)).Trim()
            : "";

        string packUrl = release.Assets
            .FirstOrDefault(a => string.Equals(
                a.Name,
                ClientPackAssetName,
                StringComparison.OrdinalIgnoreCase))
            ?.BrowserDownloadUrl
            ?? throw new InvalidOperationException(
                $"В GitHub Release {tag} не найден файл {ClientPackAssetName}.");

        VersionText.Text = $"Сборка: {tag}";

        if (string.Equals(installedTag, tag, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(_gameDir, "mods")))
        {
            StatusText.Text = $"Сборка {tag} уже установлена.";
            Progress.Value = 40;
            return;
        }

        string tempZip = Path.Combine(
            Path.GetTempPath(),
            $"SolarisClient-{Guid.NewGuid():N}.zip");

        string tempExtract = Path.Combine(
            Path.GetTempPath(),
            $"SolarisClient-{Guid.NewGuid():N}");

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
        string url =
            $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

        using HttpResponseMessage response = await _http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub не вернул последнюю версию сборки. " +
                $"HTTP {(int)response.StatusCode} {response.StatusCode}.");
        }

        await using Stream stream =
            await response.Content.ReadAsStreamAsync();

        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        return release
            ?? throw new InvalidOperationException(
                "GitHub вернул пустой ответ о Release.");
    }

    private async Task DownloadFileWithProgressAsync(
        string url,
        string destination,
        double min,
        double max)
    {
        using HttpResponseMessage response =
            await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;

        await using Stream input =
            await response.Content.ReadAsStreamAsync();

        await using FileStream output = new(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 64,
            true);

        byte[] buffer = new byte[1024 * 128];
        long readTotal = 0;

        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;

            if (total is > 0)
            {
                double ratio = Math.Clamp(
                    (double)readTotal / total.Value,
                    0,
                    1);

                Progress.Value = min + (max - min) * ratio;
            }
        }
    }

    private static void ExtractZipSafely(
        string zipFile,
        string destination)
    {
        string fullDestination =
            Path.GetFullPath(destination) +
            Path.DirectorySeparatorChar;

        using ZipArchive archive = ZipFile.OpenRead(zipFile);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target =
                Path.GetFullPath(
                    Path.Combine(destination, entry.FullName));

            if (!target.StartsWith(
                    fullDestination,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Архив содержит небезопасный путь: " +
                    entry.FullName);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(target)!);

            entry.ExtractToFile(target, true);
        }
    }

    private static string FindPackRoot(string extractedDir)
    {
        if (Directory.Exists(Path.Combine(extractedDir, "mods")) ||
            Directory.Exists(Path.Combine(extractedDir, "config")))
            return extractedDir;

        string[] directories =
            Directory.GetDirectories(extractedDir);

        if (directories.Length == 1 &&
            (Directory.Exists(
                Path.Combine(directories[0], "mods")) ||
             Directory.Exists(
                Path.Combine(directories[0], "config"))))
        {
            return directories[0];
        }

        throw new InvalidOperationException(
            "Не удалось найти клиентскую сборку в SolarisClient.zip.");
    }

    private void InstallManagedPack(string sourceRoot)
    {
        string[] managedDirectories =
        {
            "mods",
            "config",
            "defaultconfigs",
            "resourcepacks",
            "shaderpacks",
            "kubejs",
            "journeymap",
            "tacz",
            "xaero",
            "patchouli_books",
            "scripts"
        };

        foreach (string relative in managedDirectories)
        {
            string source =
                Path.Combine(sourceRoot, relative);

            if (!Directory.Exists(source))
                continue;

            string destination =
                Path.Combine(_gameDir, relative);

            TryDeleteDirectory(destination);
            CopyDirectory(source, destination);
        }

        string[] managedFiles =
        {
            "options.txt",
            "optionsof.txt",
            "servers.dat",
            "servers.dat_old"
        };

        foreach (string file in managedFiles)
        {
            string source =
                Path.Combine(sourceRoot, file);

            if (File.Exists(source))
            {
                File.Copy(
                    source,
                    Path.Combine(_gameDir, file),
                    true);
            }
        }
    }

    private async Task<string> EnsureForgeAsync()
    {
        var path = new MinecraftPath(_gameDir);
        var launcher = new MinecraftLauncher(path);

        var forgeInstaller = new ForgeInstaller(launcher);

        string versionName =
            $"1.20.1-forge-{ForgeVersion}";

        var options = new ForgeInstallOptions
        {
            SkipIfAlreadyInstalled = true
        };

        string installed =
            await forgeInstaller.Install(
                MinecraftVersion,
                ForgeVersion,
                options);

        versionName = installed;

        StatusText.Text =
            "Скачиваем библиотеки, ресурсы и Java...";

        Progress.Value = 82;

        await launcher.InstallAsync(versionName);

        Progress.Value = 92;

        return versionName;
    }

    private async Task LaunchMinecraftAsync(
        string versionName,
        string nick)
    {
        var path = new MinecraftPath(_gameDir);
        var launcher = new MinecraftLauncher(path);

        int ram = (int)RamSlider.Value;

        var options = new MLaunchOption
        {
            // Пока используем offline session.
            // Microsoft/Solaris server authentication добавим следующим этапом.
            Session = MSession.CreateOfflineSession(nick),

            MaximumRamMb = ram,
            MinimumRamMb = Math.Min(2048, ram),

            GameLauncherName = "SolarisLauncher",
            GameLauncherVersion = "3.0"
        };

        if (!string.IsNullOrWhiteSpace(ServerHost))
        {
            options.ServerIp = ServerHost;
            options.ServerPort = ServerPort;
        }

        var process =
            await launcher.BuildProcessAsync(
                versionName,
                options);

        process.Start();
    }

    // -------------------------
    // ВСПОМОГАТЕЛЬНОЕ
    // -------------------------

    private static void CopyDirectory(
        string source,
        string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(
                file,
                Path.Combine(
                    destination,
                    Path.GetFileName(file)),
                true);
        }

        foreach (string directory in
                 Directory.GetDirectories(source))
        {
            CopyDirectory(
                directory,
                Path.Combine(
                    destination,
                    Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private sealed class LocalAccount
    {
        public string Username { get; set; } = "";
        public string Salt { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
