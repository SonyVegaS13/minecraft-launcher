using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SolarisLauncher;

public partial class MainWindow : Window
{
    private const string MinecraftVersion = "1.20.1";
    private const string ForgeVersion = "47.4.20";
    private const string VanillaVersion = "26.2";

    private const string GitHubOwner = "SonyVegaS13";
    private const string GitHubRepo = "minecraft-launcher";
    private const string ClientPackAssetName = "SolarisClient.zip";

    private const string ServerHost = "solarisplay.millida.host";
    private const int ServerPort = 25565;
    private const string ModdedServerHost = "";

    private readonly string _gameDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solaris", "game");
    private readonly string _stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solaris");
    private readonly string _accountFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solaris", "account.json");
    private readonly HttpClient _http = new();
    private bool _registerMode;
    private CancellationTokenSource? _launchCancellation;
    private Button? _activeLaunchButton;
    private bool _updateCheckStarted;

    private const string LauncherVersion = "2.1.1";
    private const string UpdateManifestUrl = "https://raw.githubusercontent.com/SonyVegaS13/minecraft-launcher/solaris-2.1-polish/update.json";

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SolarisLauncher/3.0");
        Directory.CreateDirectory(_stateDir);
        RamText.Text = $"{(int)RamSlider.Value} MB";
        TryRestoreAccount();
        SetupServerButtons();
        _ = UpdateServerStatusAsync();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_updateCheckStarted)
            return;

        _updateCheckStarted = true;
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            string json = await _http.GetStringAsync(UpdateManifestUrl);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement launcher = document.RootElement.GetProperty("launcher");

            string latestVersion =
                launcher.GetProperty("version").GetString() ?? LauncherVersion;

            string downloadUrl =
                launcher.GetProperty("url").GetString() ?? string.Empty;

            if (!Version.TryParse(latestVersion, out Version? latest) ||
                !Version.TryParse(LauncherVersion, out Version? current) ||
                latest is null ||
                current is null ||
                latest.CompareTo(current) <= 0 ||
                string.IsNullOrWhiteSpace(downloadUrl))
            {
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                $"Доступна новая версия Solaris Launcher: {latestVersion}\n\n" +
                $"Текущая версия: {LauncherVersion}\n\n" +
                "Обновить лаунчер сейчас?",
                "Обновление Solaris Launcher",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                await StartUpdateAsync(downloadUrl);
        }
        catch
        {
            // Ошибка проверки обновления не мешает запуску лаунчера.
        }
    }

    private async Task StartUpdateAsync(string downloadUrl)
    {
        string currentLauncherPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к лаунчеру.");

        string launcherDirectory = Path.GetDirectoryName(currentLauncherPath)
            ?? throw new InvalidOperationException("Не удалось определить папку лаунчера.");

        string newLauncherPath = Path.Combine(
            Path.GetTempPath(),
            $"SolarisLauncher-{Guid.NewGuid():N}.exe");

        try
        {
            StatusText.Text = "Скачиваем обновление Solaris Launcher...";
            Progress.Value = 0;

            await DownloadFileWithProgressAsync(
                downloadUrl,
                newLauncherPath,
                0,
                100,
                CancellationToken.None);

            if (!File.Exists(newLauncherPath))
                throw new InvalidOperationException("Обновление не было скачано.");

            Process.Start(new ProcessStartInfo
            {
                FileName = currentLauncherPath,
                Arguments =
                    $"--self-update \"{currentLauncherPath}\" \"{newLauncherPath}\"",
                WorkingDirectory = launcherDirectory,
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }
        catch
        {
            TryDeleteFile(newLauncherPath);
            throw;
        }
    }

    private async void AuthActionButton_Click(object sender, RoutedEventArgs e)
    {
        string login = LoginBox.Text.Trim();
        string password = PasswordBox.Password;
        AuthStatus.Text = "";
        if (!IsValidLogin(login)) { AuthStatus.Text = "Логин: 3–16 символов, только буквы, цифры и _."; return; }
        if (password.Length < 8) { AuthStatus.Text = "Пароль должен содержать минимум 8 символов."; return; }
        AuthActionButton.IsEnabled = false; SwitchAuthButton.IsEnabled = false;
        try { if (_registerMode) await RegisterLocalAsync(login, password); else await LoginLocalAsync(login, password); }
        catch (Exception ex) { AuthStatus.Text = ex.Message; }
        finally { AuthActionButton.IsEnabled = true; SwitchAuthButton.IsEnabled = true; }
    }

    private void SwitchAuthButton_Click(object sender, RoutedEventArgs e)
    {
        _registerMode = !_registerMode;
        AuthTitle.Text = _registerMode ? "Создание аккаунта" : "Вход в аккаунт";
        AuthActionButton.Content = _registerMode ? "СОЗДАТЬ АККАУНТ" : "ВОЙТИ";
        SwitchAuthButton.Content = _registerMode ? "У меня уже есть аккаунт" : "Создать аккаунт";
        AuthStatus.Text = ""; PasswordBox.Clear();
    }

    private async Task RegisterLocalAsync(string login, string password)
    {
        if (File.Exists(_accountFile))
        {
            var existing = await ReadAccountAsync();
            if (existing is not null && string.Equals(existing.Username, login, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Этот аккаунт уже зарегистрирован на этом компьютере.");
        }
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = HashPassword(password, salt);
        await SaveAccountAsync(new LocalAccount { Username = login, Salt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(hash), RememberMe = true });
        ShowMainView(login);
    }

    private async Task LoginLocalAsync(string login, string password)
    {
        LocalAccount? account = await ReadAccountAsync();
        if (account is null) throw new InvalidOperationException("Аккаунт ещё не создан на этом компьютере.");
        if (!string.Equals(account.Username, login, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Неверный логин или пароль.");
        byte[] salt = Convert.FromBase64String(account.Salt);
        byte[] expected = Convert.FromBase64String(account.PasswordHash);
        byte[] actual = HashPassword(password, salt);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new InvalidOperationException("Неверный логин или пароль.");
        ShowMainView(account.Username);
    }

    private async void TryRestoreAccount()
    {
        try { LocalAccount? account = await ReadAccountAsync(); if (account is not null && account.RememberMe && !string.IsNullOrWhiteSpace(account.Username)) ShowMainView(account.Username); }
        catch { }
    }

    private void ShowMainView(string username)
    {
        AuthView.Visibility = Visibility.Collapsed; MainView.Visibility = Visibility.Visible;
        WelcomeText.Text = username;
        ProfileText.Text = $"Игрок: {username}\nВерсия клиента: Minecraft {MinecraftVersion}";
        StatusText.Text = "Готов к запуску."; Progress.Value = 0;
        _ = UpdateServerStatusAsync();
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LocalAccount? account = await ReadAccountAsync();
            if (account is not null) { account.RememberMe = false; await SaveAccountAsync(account); }
        }
        catch { }
        MainView.Visibility = Visibility.Collapsed; AuthView.Visibility = Visibility.Visible;
        LoginBox.Clear(); PasswordBox.Clear(); AuthStatus.Text = ""; _registerMode = false;
        AuthTitle.Text = "Вход в аккаунт"; AuthActionButton.Content = "ВОЙТИ"; SwitchAuthButton.Content = "Создать аккаунт";
    }

    private async Task<LocalAccount?> ReadAccountAsync()
    {
        if (!File.Exists(_accountFile)) return null;
        await using FileStream stream = File.OpenRead(_accountFile);
        return await JsonSerializer.DeserializeAsync<LocalAccount>(stream);
    }

    private async Task SaveAccountAsync(LocalAccount account)
    {
        Directory.CreateDirectory(_stateDir);
        await using FileStream stream = File.Create(_accountFile);
        await JsonSerializer.SerializeAsync(stream, account, new JsonSerializerOptions { WriteIndented = true });
    }

    private static byte[] HashPassword(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);

    private static bool IsValidLogin(string login) => login.Length >= 3 && login.Length <= 16 && login.All(c => char.IsLetterOrDigit(c) || c == '_');

    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (RamText is not null) RamText.Text = $"{(int)e.NewValue} MB"; }

    private async Task UpdateServerStatusAsync()
    {
        if (MainView is null) return;
        TextBlock? status = FindTextBlock(MainView, " Сервер готов");
        Ellipse? dot = FindElement<Ellipse>(MainView);
        if (status is null) return;

        status.Text = " Проверяем сервер...";
        if (dot is not null) dot.Fill = new SolidColorBrush(Color.FromRgb(250, 204, 21));

        try
        {
            bool online = await PingMinecraftServerAsync(ServerHost, ServerPort, CancellationToken.None);
            status.Text = online ? " Сервер онлайн" : " Сервер офлайн";
            if (dot is not null) dot.Fill = new SolidColorBrush(online ? Color.FromRgb(74, 222, 128) : Color.FromRgb(248, 113, 113));
        }
        catch
        {
            status.Text = " Сервер офлайн";
            if (dot is not null) dot.Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
    }

    private static async Task<bool> PingMinecraftServerAsync(string host, int port, CancellationToken token)
    {
        using TcpClient client = new();
        await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(4), token);
        using NetworkStream stream = client.GetStream();

        using MemoryStream handshake = new();
        WriteVarInt(handshake, 0);
        WriteVarInt(handshake, -1);
        WriteString(handshake, host);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)(port & 0xFF));
        WriteVarInt(handshake, 1);
        await WritePacketAsync(stream, handshake.ToArray(), token);

        await WritePacketAsync(stream, new byte[] { 0 }, token);

        int packetLength = await ReadVarIntAsync(stream, token);
        if (packetLength <= 0 || packetLength > 1_000_000) return false;
        byte[] packet = await ReadExactAsync(stream, packetLength, token);
        using MemoryStream response = new(packet, writable: false);
        int packetId = ReadVarInt(response);
        if (packetId != 0) return false;
        int jsonLength = ReadVarInt(response);
        if (jsonLength <= 0 || jsonLength > response.Length - response.Position) return false;
        byte[] json = new byte[jsonLength];
        int read = 0;
        while (read < json.Length)
        {
            int chunk = await response.ReadAsync(json.AsMemory(read, json.Length - read), token);
            if (chunk == 0) return false;
            read += chunk;
        }

        string statusJson = Encoding.UTF8.GetString(json);
        return !string.IsNullOrWhiteSpace(statusJson);
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        using MemoryStream packet = new();
        WriteVarInt(packet, payload.Length);
        byte[] header = packet.ToArray();
        await stream.WriteAsync(header.AsMemory(), token);
        await stream.WriteAsync(payload.AsMemory(), token);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        uint unsigned = unchecked((uint)value);
        while ((unsigned & ~0x7Fu) != 0)
        {
            stream.WriteByte((byte)((unsigned & 0x7F) | 0x80));
            unsigned >>= 7;
        }
        stream.WriteByte((byte)unsigned);
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken token)
    {
        int result = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            int value = await ReadByteAsync(stream, token);
            result |= (value & 0x7F) << shift;
            if ((value & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidOperationException("Некорректный Minecraft VarInt.");
    }

    private static int ReadVarInt(Stream stream)
    {
        int result = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            int value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            result |= (value & 0x7F) << shift;
            if ((value & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidOperationException("Некорректный Minecraft VarInt.");
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[1];
        int read = await stream.ReadAsync(buffer.AsMemory(), token);
        if (read == 0) throw new EndOfStreamException();
        return buffer[0];
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
    {
        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), token);
            if (chunk == 0) throw new EndOfStreamException();
            read += chunk;
        }
        return buffer;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock && string.Equals(textBlock.Text, text, StringComparison.Ordinal)) return textBlock;
            if (child is DependencyObject dependencyChild)
            {
                TextBlock? result = FindTextBlock(dependencyChild, text);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static T? FindElement<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match) return match;
            if (child is DependencyObject dependencyChild)
            {
                T? result = FindElement<T>(dependencyChild);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private void SetupServerButtons()
    {
        if (PlayButton.Parent is not StackPanel panel) return;
        PlayButton.Content = "ИГРАТЬ"; PlayButton.Width = 220;
        if (panel.Children.OfType<Button>().Any(b => b.Name == "VanillaPlayButton")) return;
        var moddedText = new TextBlock { Text = $"Minecraft {MinecraftVersion} • Forge {ForgeVersion}", Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(142, 153, 170)), FontSize = 12 };
        int moddedIndex = panel.Children.IndexOf(PlayButton); panel.Children.Insert(moddedIndex + 1, moddedText);
        var button = new Button { Name = "VanillaPlayButton", Content = "ИГРАТЬ", Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), Style = FindResource("SecondaryButton") as Style };
        button.Click += VanillaPlayButton_Click; panel.Children.Insert(panel.Children.IndexOf(PlayButton) + 2, button);
        var serverText = new TextBlock { Text = $"Minecraft {VanillaVersion} • {ServerHost}:{ServerPort}", Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(142, 153, 170)), FontSize = 12 };
        panel.Children.Insert(panel.Children.IndexOf(button) + 1, serverText);
    }

    private bool TryBeginLaunch(Button button, string playText, out CancellationTokenSource? cancellation, out CancellationToken token)
    {
        if (_launchCancellation is not null)
        {
            if (ReferenceEquals(_activeLaunchButton, button))
            {
                _launchCancellation.Cancel();
                button.Content = "ОТМЕНА...";
                button.IsEnabled = false;
                StatusText.Text = "Отменяем запуск...";
                cancellation = null;
                token = CancellationToken.None;
                return false;
            }

            StatusText.Text = "Сначала отмените текущий запуск.";
            cancellation = null;
            token = CancellationToken.None;
            return false;
        }

        cancellation = new CancellationTokenSource();
        token = cancellation.Token;
        _launchCancellation = cancellation;
        _activeLaunchButton = button;
        button.Content = "ОТМЕНА";
        button.IsEnabled = true;
        return true;
    }

    private void EndLaunch(Button button, string playText, CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_launchCancellation, cancellation))
        {
            _launchCancellation = null;
            _activeLaunchButton = null;
        }

        button.Content = playText;
        button.IsEnabled = true;
        cancellation.Dispose();
    }

    private async void VanillaPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button vanillaButton) return;
        if (!TryBeginLaunch(vanillaButton, "ИГРАТЬ", out CancellationTokenSource? cancellation, out CancellationToken token)) return;

        try
        {
            Directory.CreateDirectory(_stateDir); string vanillaDir = Path.Combine(_stateDir, "vanilla"); Directory.CreateDirectory(vanillaDir);
            StatusText.Text = $"Устанавливаем Minecraft {VanillaVersion}..."; Progress.Value = 10;
            LocalAccount? account = await ReadAccountAsync();
            token.ThrowIfCancellationRequested();
            if (account is null || string.IsNullOrWhiteSpace(account.Username)) throw new InvalidOperationException("Аккаунт не найден.");
            var path = new MinecraftPath(vanillaDir); var launcher = new MinecraftLauncher(path);
            StatusText.Text = $"Скачиваем Minecraft {VanillaVersion} и Java..."; Progress.Value = 25;
            await launcher.InstallAsync(VanillaVersion);
            token.ThrowIfCancellationRequested();
            Progress.Value = 85;
            string javaPath = FindBundledJava(vanillaDir); if (!File.Exists(javaPath)) throw new FileNotFoundException($"Java Runtime не найден: {javaPath}");
            int ram = (int)RamSlider.Value;
            var options = new MLaunchOption { Session = MSession.CreateOfflineSession(account.Username), JavaPath = javaPath, MaximumRamMb = ram, MinimumRamMb = Math.Min(2048, ram), ServerIp = ServerHost, ServerPort = ServerPort, GameLauncherName = "SolarisLauncher", GameLauncherVersion = "3.0" };
            token.ThrowIfCancellationRequested();
            var process = await launcher.BuildProcessAsync(VanillaVersion, options); token.ThrowIfCancellationRequested(); process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.Start(); Progress.Value = 100; StatusText.Text = "Minecraft Vanilla запущен."; Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            StatusText.Text = "Запуск Vanilla отменён.";
        }
        catch (Exception ex) { StatusText.Text = "Ошибка запуска Vanilla"; MessageBox.Show(ex.ToString(), "Solaris Launcher — Vanilla", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            if (cancellation is not null) EndLaunch(vanillaButton, "ИГРАТЬ", cancellation);
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginLaunch(PlayButton, "ИГРАТЬ", out CancellationTokenSource? cancellation, out CancellationToken token)) return;

        try
        {
            Directory.CreateDirectory(_stateDir); Directory.CreateDirectory(_gameDir);
            StatusText.Text = "Проверяем обновление сборки на GitHub..."; Progress.Value = 5;
            await UpdateClientPackAsync(token); token.ThrowIfCancellationRequested();
            StatusText.Text = "Устанавливаем Minecraft 1.20.1 и Forge..."; Progress.Value = 45;
            string versionName = await EnsureForgeAsync(token); token.ThrowIfCancellationRequested();
            LocalAccount? account = await ReadAccountAsync(); token.ThrowIfCancellationRequested();
            if (account is null || string.IsNullOrWhiteSpace(account.Username)) throw new Exception("Аккаунт не найден.");
            StatusText.Text = "Запускаем Minecraft..."; Progress.Value = 100;
            await LaunchMinecraftAsync(versionName, account.Username, ModdedServerHost, token);
            StatusText.Text = "Minecraft запущен.";
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            StatusText.Text = "Запуск Modded отменён.";
        }
        catch (Exception ex) { StatusText.Text = "Ошибка"; MessageBox.Show(ex.ToString(), "Solaris Launcher — ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            if (cancellation is not null) EndLaunch(PlayButton, "ИГРАТЬ", cancellation);
        }
    }

    private async Task<string> EnsureForgeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = new MinecraftPath(_gameDir);
        var launcher = new MinecraftLauncher(path);
        var installer = new ForgeInstaller(launcher);
        string forgeVersion = await installer.Install(MinecraftVersion, ForgeVersion, new ForgeInstallOptions { CancellationToken = token });
        token.ThrowIfCancellationRequested();
        await launcher.InstallAsync(forgeVersion);
        token.ThrowIfCancellationRequested();
        return forgeVersion;
    }

    private async Task LaunchMinecraftAsync(string versionName, string nick, string serverHost, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));
        string javaPath = FindBundledJava(_gameDir);
        if (!File.Exists(javaPath)) throw new FileNotFoundException($"Java Runtime не найден: {javaPath}");
        int ram = (int)RamSlider.Value;
        var options = new MLaunchOption { Session = MSession.CreateOfflineSession(nick), JavaPath = javaPath, MaximumRamMb = ram, MinimumRamMb = Math.Min(2048, ram), GameLauncherName = "SolarisLauncher", GameLauncherVersion = "3.0" };
        if (!string.IsNullOrWhiteSpace(serverHost)) { options.ServerIp = serverHost; options.ServerPort = ServerPort; }
        token.ThrowIfCancellationRequested();
        var process = await launcher.BuildProcessAsync(versionName, options); token.ThrowIfCancellationRequested(); process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.Start();
        Application.Current.Shutdown();
    }

    private async Task UpdateClientPackAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var release = await GetLatestReleaseAsync(token); string tag = release.TagName;
        string stateFile = Path.Combine(_stateDir, "client-release.txt"); string installedTag = File.Exists(stateFile) ? (await File.ReadAllTextAsync(stateFile, token)).Trim() : "";
        string packUrl = release.Assets.FirstOrDefault(a => string.Equals(a.Name, ClientPackAssetName, StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl ?? throw new InvalidOperationException($"В GitHub Release {tag} не найден файл {ClientPackAssetName}.");
        VersionText.Text = $"Сборка: {tag}";
        if (string.Equals(installedTag, tag, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(_gameDir, "mods"))) { StatusText.Text = $"Сборка {tag} уже установлена."; Progress.Value = 40; return; }
        string tempZip = Path.Combine(Path.GetTempPath(), $"SolarisClient-{Guid.NewGuid():N}.zip"); string tempExtract = Path.Combine(Path.GetTempPath(), $"SolarisClient-{Guid.NewGuid():N}");
        try { StatusText.Text = $"Скачиваем сборку {tag} с GitHub..."; await DownloadFileWithProgressAsync(packUrl, tempZip, 5, 35, token); token.ThrowIfCancellationRequested(); StatusText.Text = "Распаковываем сборку..."; Progress.Value = 36; Directory.CreateDirectory(tempExtract); ExtractZipSafely(tempZip, tempExtract, token); string sourceRoot = FindPackRoot(tempExtract); InstallManagedPack(sourceRoot, token); await File.WriteAllTextAsync(stateFile, tag, token); Progress.Value = 40; }
        finally { TryDeleteFile(tempZip); TryDeleteDirectory(tempExtract); }
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken token)
    {
        string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest"; using HttpResponseMessage response = await _http.GetAsync(url, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub не вернул последнюю версию сборки. HTTP {(int)response.StatusCode} {response.StatusCode}.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(token); return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, token) ?? throw new InvalidOperationException("GitHub вернул пустой ответ о Release.");
    }

    private async Task DownloadFileWithProgressAsync(string url, string destination, double min, double max, CancellationToken token)
    {
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode(); long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(token); await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, true);
        byte[] buffer = new byte[1024 * 128]; long readTotal = 0; int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), token); readTotal += read; if (total is > 0) Progress.Value = min + (max - min) * Math.Clamp((double)readTotal / total.Value, 0, 1); }
    }

    private static void ExtractZipSafely(string zipFile, string destination, CancellationToken token)
    {
        string fullDestination = Path.GetFullPath(destination) + Path.DirectorySeparatorChar; using ZipArchive archive = ZipFile.OpenRead(zipFile);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName)); if (!target.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Архив содержит небезопасный путь: " + entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true);
        }
    }

    private static string FindPackRoot(string extractedDir)
    {
        if (Directory.Exists(Path.Combine(extractedDir, "mods")) || Directory.Exists(Path.Combine(extractedDir, "config"))) return extractedDir;
        string[] directories = Directory.GetDirectories(extractedDir);
        if (directories.Length == 1 && (Directory.Exists(Path.Combine(directories[0], "mods")) || Directory.Exists(Path.Combine(directories[0], "config")))) return directories[0];
        throw new InvalidOperationException("Не удалось найти клиентскую сборку в SolarisClient.zip.");
    }

    private void InstallManagedPack(string sourceRoot, CancellationToken token)
    {
        string[] managedDirectories = { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "journeymap", "tacz", "xaero", "patchouli_books", "scripts" };
        foreach (string relative in managedDirectories)
        {
            token.ThrowIfCancellationRequested();
            string source = Path.Combine(sourceRoot, relative); string destination = Path.Combine(_gameDir, relative);
            if (!Directory.Exists(source)) continue;
            Directory.CreateDirectory(destination);
            CopyDirectory(source, destination, token);
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken token)
    {
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, directory); Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file); string target = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
    }

    private string FindBundledJava(string rootDir)
    {
        string[] candidates = {
            Path.Combine(rootDir, "runtime", "windows-x64", "java-runtime-delta", "bin", "java.exe"),
            Path.Combine(rootDir, "runtime", "windows-x64", "java-runtime-gamma", "bin", "java.exe"),
            Path.Combine(rootDir, "runtime", "windows-x64", "java-runtime-alpha", "bin", "java.exe"),
            Path.Combine(rootDir, "runtime", "windows-x64", "java-runtime-beta", "bin", "java.exe"),
            Path.Combine(rootDir, "runtime", "windows-x64", "java-runtime-epison", "bin", "java.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? Directory.GetFiles(Path.Combine(rootDir, "runtime"), "java.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private sealed class LocalAccount
    {
        public string Username { get; set; } = "";
        public string Salt { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public bool RememberMe { get; set; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}