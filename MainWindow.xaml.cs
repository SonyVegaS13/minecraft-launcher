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
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SolarisLauncher;

public partial class MainWindow : Window
{
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { /* The mouse was released during the drag. */ }
    }

    private void TitleBarMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleBarMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleBarClose_Click(object sender, RoutedEventArgs e) => Close();

    private const string MinecraftVersion = "1.20.1";
    private const string ForgeVersion = "47.4.20";
    private const string VanillaVersion = "26.2";

    private const string GitHubOwner = "SonyVegaS13";
    private const string GitHubRepo = "minecraft-launcher";
    private const string ClientPackAssetName = "SolarisClient.zip";

    private const string ServerHost = "solarisplay.millida.host";
    private const int ServerPort = 25565;
    private const string ModdedServerHost = "";

    private readonly string _gameDir = Path.Combine(SolarisDirectories.StateDir, "game");
    private readonly string _stateDir = SolarisDirectories.StateDir;
    private readonly string _accountFile = Path.Combine(SolarisDirectories.StateDir, "account.json");
    private readonly HttpClient _http = new();
    private bool _registerMode;
    private CancellationTokenSource? _launchCancellation;
    private Button? _activeLaunchButton;
    private bool _updateCheckStarted;

    private const string LauncherVersion = "2.2.10";
    // Neon versions check signed-off GitHub release assets, not the 2.1.x stable manifest.

    public MainWindow()
    {
        InitializeComponent();
        InitializeArtworkPreviews();
        TryLoadNeonArt();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SolarisLauncher/3.0");
        _http.Timeout = TimeSpan.FromMinutes(20);
        Directory.CreateDirectory(_stateDir);
        LoadRamSettings();
        InitializeIgnition();
        InitializeCloudMode();
        TryRestoreAccount();
        _ = UpdateServerStatusAsync();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_updateCheckStarted)
            return;

        _updateCheckStarted = true;
        if (App.IsDeveloperMode)
        {
            StatusText.Text = "Режим разработки: данные и обновления изолированы от Solaris 2.2.9.";
            return;
        }
        // The new executable acknowledges successful WPF initialization to the
        // detached updater. Never re-offer the same update during its handshake.
        if (App.PendingUpdateAcknowledgement is { } attempt)
        {
            SolarisSafeUpdate.PublishReady(attempt);
            return;
        }
        if (!App.LaunchedAfterRecovery)
            await CheckForUpdatesAsync();
    }

    private async void AuthActionButton_Click(object sender, RoutedEventArgs e)
    {
        string login = LoginBox.Text.Trim();
        string password = CurrentPassword();
        AuthStatus.Text = "";
        if (!_cloudMode && !IsValidLogin(login)) { AuthStatus.Text = "Логин: 3–16 символов, только буквы, цифры и _."; return; }
        if (_cloudMode && !login.Contains('@')) { AuthStatus.Text = "Введите email Solaris ID."; return; }
        int minPasswordLength = _cloudMode ? 12 : 8;
        if (password.Length < minPasswordLength)
        {
            AuthStatus.Text = $"Пароль должен содержать минимум {minPasswordLength} символов.";
            return;
        }
        if ((_registerMode || _cloudResetMode) &&
            !string.Equals(password, CurrentConfirmationPassword(), StringComparison.Ordinal))
        {
            AuthStatus.Text = "Пароли не совпадают.";
            return;
        }
        AuthActionButton.IsEnabled = false; SwitchAuthButton.IsEnabled = false;
        try
        {
            if (_cloudMode) await AuthenticateCloudAsync(login, password);
            else if (_registerMode) await RegisterLocalAsync(login, password);
            else await LoginLocalAsync(login, password);
        }
        catch (Exception ex) { AuthStatus.Text = ex.Message; }
        finally { AuthActionButton.IsEnabled = true; SwitchAuthButton.IsEnabled = true; }
    }

    private void SwitchAuthButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudResetMode)
        {
            ResetAuthScreen();
            return;
        }
        _registerMode = !_registerMode;
        AuthTitle.Text = _registerMode ? "Создание аккаунта" : "Вход в аккаунт";
        AuthActionButton.Content = _registerMode ? "СОЗДАТЬ АККАУНТ" : "ВОЙТИ  ›";
        SwitchAuthButton.Content = _registerMode ? "У меня уже есть аккаунт" : "Создать аккаунт";
        AuthStatus.Text = ""; ClearPasswordFields();
        ConfirmPasswordPanel.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshCloudAuthLayout();
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
        await SaveAccountAsync(new LocalAccount { Username = login, Salt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(hash), RememberMe = RememberMeCheck.IsChecked == true, CreatedUtc = DateTimeOffset.UtcNow });
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
        account.RememberMe = RememberMeCheck.IsChecked == true;
        await SaveAccountAsync(account);
        ShowMainView(account.Username);
    }

    private async void TryRestoreAccount()
    {
        try
        {
            if (await TryRestoreCloudAsync()) return;
            LocalAccount? account = await ReadAccountAsync();
            if (account is not null)
            {
                RememberMeCheck.IsChecked = account.RememberMe;
                if (account.RememberMe && !string.IsNullOrWhiteSpace(account.Username))
                    ShowMainView(account.Username);
            }
        }
        catch { /* Offline login stays available. */ }
    }

    private void ShowMainView(string username)
    {
        AuthView.Visibility = Visibility.Collapsed; MainView.Visibility = Visibility.Visible;
        WelcomeText.Text = username;
        ProfileName.Text = username;
        ProfileText.Text = _cloudSession is not null
            ? (_cloudOffline ? "SOLARIS ID — без соединения" : "SOLARIS ID — подключён")
            : $"Локальный аккаунт: {username}";
        FullProfileStatus.Text = ProfileText.Text;
        CloudLegacyImportPanel.Visibility = _cloudSession is null
            ? Visibility.Collapsed : Visibility.Visible;
        OpenControlButton.Visibility = _cloudSession?.IsAdmin == true && !_cloudOffline
            ? Visibility.Visible : Visibility.Collapsed;
        FullProfileName.Text = username;
        _ = RefreshActivityAndProfileAsync(username);
        StatusText.Text = "Готов к запуску."; Progress.Value = 0;
        _ = UpdateServerStatusAsync();
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_cloudSession is not null) ClearCloudSession();
            else
            {
                LocalAccount? account = await ReadAccountAsync();
                if (account is not null) { account.RememberMe = false; await SaveAccountAsync(account); }
            }
        }
        catch { }
        MainView.Visibility = Visibility.Collapsed; AuthView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Collapsed;
        ControlView.Visibility = Visibility.Collapsed;
        LoginBox.Clear(); ClearPasswordFields(); ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
        RememberMeCheck.IsChecked = false; AuthStatus.Text = ""; _registerMode = false;
        AuthTitle.Text = "Вход в аккаунт"; AuthActionButton.Content = "ВОЙТИ  ›"; SwitchAuthButton.Content = "Создать аккаунт";
        RefreshCloudAuthLayout();
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

    // Individual memory settings are maintained by SolarisSettings.cs.

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
            StatusText.Text = $"Проверяем Minecraft {VanillaVersion} и Java 25..."; Progress.Value = 4;
            string nickname = await GetCurrentLaunchUsernameAsync();
            token.ThrowIfCancellationRequested();
            var path = new MinecraftPath(vanillaDir); var launcher = new MinecraftLauncher(path);
            WireMinecraftProgress(launcher, "Vanilla", 25, 85);
            string javaBefore = FindJavaForMode(vanillaDir, 25);
            StatusText.Text = File.Exists(javaBefore)
                ? "Java 25 найдена. Проверяем Minecraft..."
                : "Java 25 отсутствует. Проверяем и загружаем необходимые компоненты...";
            bool alreadyInstalled = Directory.Exists(Path.Combine(vanillaDir, "versions", VanillaVersion));
            StatusText.Text = alreadyInstalled
                ? $"Minecraft {VanillaVersion} найден. Проверяем файлы..."
                : $"Minecraft {VanillaVersion} отсутствует. Устанавливаем...";
            Progress.Value = 25;
            // CmlLib checks local files and fetches only missing/corrupted libraries,
            // assets, native dependencies and the runtime supplied by Mojang.
            await launcher.InstallAsync(VanillaVersion, token);
            token.ThrowIfCancellationRequested();
            Progress.Value = 85;
            string javaPath = await EnsureJavaForModeAsync(vanillaDir, 25, 86, 96, token);
            StatusText.Text = "Java 25 готова. Все компоненты проверены. Запускаем Vanilla...";
            int ram = GetRamForMode("vanilla");
            var options = new MLaunchOption { Session = MSession.CreateOfflineSession(nickname), JavaPath = javaPath, MaximumRamMb = ram, MinimumRamMb = Math.Min(2048, ram), ServerIp = ServerHost, ServerPort = ServerPort, GameLauncherName = "SolarisLauncher", GameLauncherVersion = "3.0" };
            token.ThrowIfCancellationRequested();
            var process = await launcher.BuildProcessAsync(VanillaVersion, options); token.ThrowIfCancellationRequested(); process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.Start(); Progress.Value = 100; StatusText.Text = "Minecraft Vanilla запущен.";
            await TrackGameProcessAsync(process, nickname, "vanilla");
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
            StatusText.Text = "Проверяем Minecraft 1.20.1, Forge 47.4.20 и Java 17..."; Progress.Value = 41;
            string java17 = await EnsureJavaForModeAsync(_gameDir, 17, 41, 47, token);
            string versionName = await EnsureForgeAsync(java17, token); token.ThrowIfCancellationRequested();
            string nickname = await GetCurrentLaunchUsernameAsync(); token.ThrowIfCancellationRequested();
            StatusText.Text = "Все компоненты готовы. Запускаем Minecraft Modded..."; Progress.Value = 98;
            await LaunchMinecraftAsync(versionName, nickname, ModdedServerHost, token);
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

    private async Task<string> EnsureForgeAsync(string javaPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = new MinecraftPath(_gameDir);
        var launcher = new MinecraftLauncher(path);
        WireMinecraftProgress(launcher, "Modded", 47, 89);
        var installer = new ForgeInstaller(launcher);
        string forgeVersion = await installer.Install(MinecraftVersion, ForgeVersion,
            new ForgeInstallOptions { CancellationToken = token, JavaPath = javaPath,
                SkipIfAlreadyInstalled = true });
        token.ThrowIfCancellationRequested();
        await launcher.InstallAsync(forgeVersion, token);
        token.ThrowIfCancellationRequested();
        return forgeVersion;
    }

    private async Task LaunchMinecraftAsync(string versionName, string nick, string serverHost, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));
        string javaPath = await EnsureJavaForModeAsync(_gameDir, 17, 90, 96, token);
        int ram = GetRamForMode("modded");
        var options = new MLaunchOption { Session = MSession.CreateOfflineSession(nick), JavaPath = javaPath, MaximumRamMb = ram, MinimumRamMb = Math.Min(2048, ram), GameLauncherName = "SolarisLauncher", GameLauncherVersion = "3.0" };
        if (!string.IsNullOrWhiteSpace(serverHost)) { options.ServerIp = serverHost; options.ServerPort = ServerPort; }
        token.ThrowIfCancellationRequested();
        var process = await launcher.BuildProcessAsync(versionName, options); token.ThrowIfCancellationRequested(); process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.Start();
        await TrackGameProcessAsync(process, nick, "modded");
    }

    private async Task UpdateClientPackAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var release = await GetLatestClientReleaseAsync(token); string tag = release.TagName;
        string stateFile = Path.Combine(_stateDir, "client-release.txt"); string installedTag = File.Exists(stateFile) ? (await File.ReadAllTextAsync(stateFile, token)).Trim() : "";
        string packUrl = release.Assets.FirstOrDefault(a => string.Equals(a.Name, ClientPackAssetName, StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl ?? throw new InvalidOperationException($"В GitHub Release {tag} не найден файл {ClientPackAssetName}.");
        VersionText.Text = $"Сборка: {tag}";
        if (string.Equals(installedTag, tag, StringComparison.OrdinalIgnoreCase) &&
            await ValidateManagedPackManifestAsync(tag, token))
        {
            StatusText.Text = $"Сборка {tag} проверена. Все моды на месте.";
            Progress.Value = 40;
            return;
        }
        StatusText.Text = $"Сборка {tag}: отсутствуют файлы либо требуется проверка. Восстанавливаем...";
        string tempZip = Path.Combine(Path.GetTempPath(), $"SolarisClient-{Guid.NewGuid():N}.zip"); string tempExtract = Path.Combine(Path.GetTempPath(), $"SolarisClient-{Guid.NewGuid():N}");
        try { StatusText.Text = $"Скачиваем сборку {tag} с GitHub..."; await DownloadFileWithProgressAsync(packUrl, tempZip, 5, 35, token); token.ThrowIfCancellationRequested(); StatusText.Text = "Распаковываем сборку..."; Progress.Value = 36; Directory.CreateDirectory(tempExtract); ExtractZipSafely(tempZip, tempExtract, token); string sourceRoot = FindPackRoot(tempExtract); InstallManagedPack(sourceRoot, token); await SaveManagedPackManifestAsync(sourceRoot, tag, token);
            await File.WriteAllTextAsync(stateFile, tag, token); Progress.Value = 40; }
        finally { TryDeleteFile(tempZip); TryDeleteDirectory(tempExtract); }
    }

    private async Task<GitHubRelease> GetLatestClientReleaseAsync(CancellationToken token)
    {
        string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases?per_page=20";
        using HttpResponseMessage response = await _http.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub не вернул список релизов. HTTP {(int)response.StatusCode} {response.StatusCode}.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        List<GitHubRelease> releases =
            await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                token) ?? new List<GitHubRelease>();

        GitHubRelease? release = releases.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.TagName) &&
            r.Assets.Any(a => string.Equals(a.Name, ClientPackAssetName, StringComparison.OrdinalIgnoreCase)));

        return release ?? throw new InvalidOperationException(
            $"В GitHub Release не найден файл {ClientPackAssetName}.");
    }

    private async Task DownloadFileWithProgressAsync(string url, string destination,
        double min, double max, CancellationToken token)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(token);
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 1024 * 64, useAsync: true);
        byte[] buffer = new byte[1024 * 128];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            readTotal += read;
            if (total is > 0)
            {
                double fraction = Math.Clamp((double)readTotal / total.Value, 0, 1);
                Progress.Value = min + (max - min) * fraction;
                IgnitionPercent.Text = $"{fraction * 100:0}%";
            }
        }
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
            CopyDirectory(source, destination, token, overwriteManaged: relative == "mods");
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken token, bool overwriteManaged)
    {
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                // Keep user-edited configuration, resource packs and scripts
                // intact. Only the declared managed mod binaries are repaired.
                if (!overwriteManaged) continue;
                if (new FileInfo(file).Length == new FileInfo(target).Length)
                {
                    using var sourceStream = File.OpenRead(file);
                    using var targetStream = File.OpenRead(target);
                    if (CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(sourceStream), SHA256.HashData(targetStream)))
                        continue; // Correct file: do not overwrite.
                }
            }
            File.Copy(file, target, overwrite: true);
        }
    }

    // Resolve Java separately for each game profile. Never silently use an
    // incompatible version just because some java.exe exists on this computer.
    private string FindJavaForMode(string rootDir, int requiredMajor)
    {
        string runtimeRoot = Path.Combine(rootDir, "runtime");
        if (!Directory.Exists(runtimeRoot)) return "";
        string[] candidates = Directory.EnumerateFiles(runtimeRoot, "java.exe", SearchOption.AllDirectories)
            .Where(path => path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (string candidate in candidates)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (process is null) continue;
                string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(3500))
                {
                    try { process.Kill(); } catch { }
                    continue;
                }
                // Modern Java: 'version "25..."; Java 8: version "1.8...".
                var match = System.Text.RegularExpressions.Regex.Match(output, @"version\s+""(?<major>\d+)");
                if (match.Success && int.TryParse(match.Groups["major"].Value, out int major)
                    && major == requiredMajor)
                    return candidate;
            }
            catch { /* Try next bundled runtime. */ }
        }
        return "";
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private sealed class LocalAccount
    {
        public string Username { get; set; } = "";
        public string Salt { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public bool RememberMe { get; set; }
        public DateTimeOffset? CreatedUtc { get; set; }
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
    private bool _vanillaRevealed;
    private bool _moddedRevealed;

    private void VanillaCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_vanillaRevealed) return;
        _vanillaRevealed = true;
        AnimateArtworkReveal(VanillaArtworkSharp, true);
    }

    private void VanillaCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_vanillaRevealed) return;
        _vanillaRevealed = false;
        AnimateArtworkReveal(VanillaArtworkSharp, false);
    }

    private void ModdedCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_moddedRevealed) return;
        _moddedRevealed = true;
        AnimateArtworkReveal(ModdedArtworkSharp, true);
    }

    private void ModdedCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_moddedRevealed) return;
        _moddedRevealed = false;
        AnimateArtworkReveal(ModdedArtworkSharp, false);
    }

    // Only fade in/out an already-cached image; the blurred card is static.
    // Repeated MouseEnter events inside a card do not restart its animation.
    private static void AnimateArtworkReveal(System.Windows.Controls.Image image, bool reveal)
    {
        var animation = new DoubleAnimation
        {
            To = reveal ? 0.92 : 0.0,
            Duration = TimeSpan.FromMilliseconds(reveal ? 185 : 235),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        image.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
