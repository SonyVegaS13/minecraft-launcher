using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SolarisLauncher;

internal static class ProfileStatsBootstrap
{
    private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solaris");
    private static readonly string StatsFile = Path.Combine(StateDir, "profile-stats.json");
    private static readonly string AchievementsFile = Path.Combine(StateDir, "achievements.json");
    private const string ServerHost = "solarisplay.millida.host";
    private const int ServerPort = 25565;

    private sealed class Stats { public int Games { get; set; } }
    private sealed record ServerInfo(bool Online, int Players, int MaxPlayers, long PingMs);
    private static Stats Current = Load();

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(Button), Button.ClickEvent, new RoutedEventHandler(OnButtonClick), true);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            PrepareProfile(window);
            UpdateProfile(window);
            StartServerStatusTimer(window);
        }));
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Window.GetWindow(button) is not MainWindow window) return;
        if (button.Name is not ("VanillaPlayButton" or "PlayButton")) return;
        Current.Games++;
        Save();
        UpdateProfile(window);
    }

    private static void PrepareProfile(MainWindow window)
    {
        StackPanel? gamesPanel = FindMetricPanel(window.MainView, "ИГРЫ");
        if (gamesPanel is not null)
        {
            gamesPanel.Visibility = Visibility.Collapsed;
            if (gamesPanel.Parent is Grid grid)
            {
                grid.ColumnDefinitions.Clear();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                StackPanel? serverPanel = FindMetricPanel(grid, "СЕРВЕР") ?? FindMetricPanel(grid, "ОНЛАЙН");
                StackPanel? levelPanel = FindMetricPanel(grid, "LEVEL");
                if (serverPanel is not null) Grid.SetColumn(serverPanel, 0);
                if (levelPanel is not null) Grid.SetColumn(levelPanel, 1);
            }
        }

        StackPanel? profile = FindPanelContainingText(window.MainView, "ПРОФИЛЬ");
        if (profile is not null && !profile.Children.OfType<FrameworkElement>().Any(x => x.Tag as string == "SolarisXpBar"))
        {
            var xpBlock = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Tag = "SolarisXpBar" };
            var header = new Grid();
            header.Children.Add(new TextBlock { Text = "ПРОГРЕСС", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(96, 105, 122)) });
            var xpText = new TextBlock { FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)), HorizontalAlignment = HorizontalAlignment.Right, Tag = "SolarisXpText" };
            header.Children.Add(xpText);
            xpBlock.Children.Add(header);
            xpBlock.Children.Add(new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 7, 0, 0), Tag = "SolarisXpProgress" });
            profile.Children.Add(xpBlock);
        }

        StackPanel? vanilla = FindPanelContainingText(window.MainView, "solarisplay.millida.host:25565");
        if (vanilla is not null && !vanilla.Children.OfType<TextBlock>().Any(x => x.Tag as string == "SolarisServerDetails"))
        {
            vanilla.Children.Add(new TextBlock { Text = "Онлайн: —/—  •  Ping: — ms", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(96, 105, 122)), Margin = new Thickness(0, 5, 0, 0), Tag = "SolarisServerDetails" });
        }
    }

    private static void StartServerStatusTimer(MainWindow window)
    {
        if (window.Resources["SolarisProfileTimerStarted"] is true) return;
        window.Resources["SolarisProfileTimerStarted"] = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += async (_, _) => { if (window.MainView.Visibility == Visibility.Visible) await RefreshServerAsync(window); };
        timer.Start();
        _ = RefreshServerAsync(window);
    }

    private static async System.Threading.Tasks.Task RefreshServerAsync(MainWindow window)
    {
        ServerInfo info;
        try { info = await QueryServerAsync(ServerHost, ServerPort); }
        catch { info = new ServerInfo(false, 0, 0, 0); }
        if (window.Dispatcher.HasShutdownStarted) return;
        window.Dispatcher.Invoke(() => ApplyServerInfo(window, info));
    }

    private static async System.Threading.Tasks.Task<ServerInfo> QueryServerAsync(string host, int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        var stopwatch = Stopwatch.StartNew();
        await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(3));
        using var stream = client.GetStream();

        using var handshake = new MemoryStream();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, -1);
        WriteString(handshake, host);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)(port & 0xFF));
        WriteVarInt(handshake, 1);
        await WritePacketAsync(stream, handshake.ToArray());
        await WritePacketAsync(stream, new byte[] { 0x00 });

        int packetLength = await ReadVarIntAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
        if (packetLength <= 0 || packetLength > 1_000_000) throw new InvalidDataException("Invalid packet");
        byte[] packet = await ReadExactAsync(stream, packetLength);
        stopwatch.Stop();

        using var response = new MemoryStream(packet, writable: false);
        if (ReadVarInt(response) != 0) throw new InvalidDataException("Invalid status packet");
        int jsonLength = ReadVarInt(response);
        if (jsonLength <= 0 || jsonLength > response.Length - response.Position) throw new InvalidDataException("Invalid JSON");
        byte[] json = new byte[jsonLength];
        await response.ReadExactlyAsync(json);
        using JsonDocument doc = JsonDocument.Parse(json);
        int players = 0, maxPlayers = 0;
        if (doc.RootElement.TryGetProperty("players", out JsonElement p))
        {
            if (p.TryGetProperty("online", out JsonElement o)) players = o.GetInt32();
            if (p.TryGetProperty("max", out JsonElement m)) maxPlayers = m.GetInt32();
        }
        return new ServerInfo(true, players, maxPlayers, Math.Max(1, stopwatch.ElapsedMilliseconds));
    }

    private static void ApplyServerInfo(MainWindow window, ServerInfo info)
    {
        SetServerMetric(window, info);
        TextBlock? details = FindElementByTag<TextBlock>(window.MainView, "SolarisServerDetails");
        if (details is not null)
        {
            details.Text = info.Online ? $"Онлайн: {info.Players}/{info.MaxPlayers}  •  Ping: {info.PingMs} ms" : "Сервер офлайн  •  Ping: —";
            details.Foreground = new SolidColorBrush(info.Online ? Color.FromRgb(134, 239, 172) : Color.FromRgb(248, 113, 113));
        }
    }

    private static void SetServerMetric(MainWindow window, ServerInfo info)
    {
        TextBlock? label = FindText(window.MainView, "ОНЛАЙН");
        if (label is not null) label.Text = "СЕРВЕР";
        TextBlock? value = FindMetricValue(window.MainView, "СЕРВЕР") ?? FindMetricValue(window.MainView, "ОНЛАЙН");
        if (value is not null)
        {
            value.Text = info.Online ? "Online" : "Offline";
            value.Foreground = new SolidColorBrush(info.Online ? Color.FromRgb(134, 239, 172) : Color.FromRgb(248, 113, 113));
        }
    }

    private static void UpdateProfile(MainWindow window)
    {
        TextBlock? level = FindMetricValue(window.MainView, "LEVEL");
        int xp = Current.Games * 20 + LoadAchievementCount() * 25;
        int levelNumber = Math.Max(1, xp / 100 + 1);
        if (level is not null)
        {
            level.Text = levelNumber.ToString();
            level.ToolTip = $"{xp % 100} / 100 XP до следующего уровня";
        }
        TextBlock? xpText = FindElementByTag<TextBlock>(window.MainView, "SolarisXpText");
        if (xpText is not null) xpText.Text = $"{xp % 100} / 100 XP";
        ProgressBar? xpProgress = FindElementByTag<ProgressBar>(window.MainView, "SolarisXpProgress");
        if (xpProgress is not null) xpProgress.Value = xp % 100;
    }

    private static int LoadAchievementCount()
    {
        try { return File.Exists(AchievementsFile) ? (JsonSerializer.Deserialize<string[]>(File.ReadAllText(AchievementsFile)) ?? Array.Empty<string>()).Length : 0; }
        catch { return 0; }
    }

    private static StackPanel? FindMetricPanel(DependencyObject root, string label)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is StackPanel panel)
            {
                var texts = panel.Children.OfType<TextBlock>().ToList();
                if (texts.Count >= 2 && texts[0].Text == label) return panel;
            }
            if (child is DependencyObject dep)
            {
                var result = FindMetricPanel(dep, label);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static TextBlock? FindMetricValue(DependencyObject root, string label)
    {
        StackPanel? panel = FindMetricPanel(root, label);
        return panel?.Children.OfType<TextBlock>().FirstOrDefault(tb => tb != panel.Children.OfType<TextBlock>().First());
    }

    private static StackPanel? FindPanelContainingText(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is StackPanel panel && panel.Children.OfType<TextBlock>().Any(tb => tb.Text == text)) return panel;
            if (child is DependencyObject dep)
            {
                var result = FindPanelContainingText(dep, text);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static T? FindElementByTag<T>(DependencyObject root, string tag) where T : FrameworkElement
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T element && element.Tag as string == tag) return element;
            if (child is DependencyObject dep)
            {
                var result = FindElementByTag<T>(dep, tag);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static TextBlock? FindText(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock tb && tb.Text == text) return tb;
            if (child is DependencyObject dep)
            {
                var result = FindText(dep, text);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        uint u = unchecked((uint)value);
        while ((u & ~0x7Fu) != 0) { stream.WriteByte((byte)((u & 0x7F) | 0x80)); u >>= 7; }
        stream.WriteByte((byte)u);
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async System.Threading.Tasks.Task WritePacketAsync(Stream stream, byte[] payload)
    {
        using var header = new MemoryStream();
        WriteVarInt(header, payload.Length);
        await stream.WriteAsync(header.ToArray());
        await stream.WriteAsync(payload);
    }

    private static async System.Threading.Tasks.Task<int> ReadVarIntAsync(Stream stream)
    {
        int result = 0, shift = 0;
        for (int i = 0; i < 5; i++)
        {
            int b = await ReadByteAsync(stream);
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidDataException("Invalid VarInt");
    }

    private static int ReadVarInt(Stream stream)
    {
        int result = 0, shift = 0;
        for (int i = 0; i < 5; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidDataException("Invalid VarInt");
    }

    private static async System.Threading.Tasks.Task<int> ReadByteAsync(Stream stream)
    {
        byte[] one = new byte[1];
        await stream.ReadExactlyAsync(one);
        return one[0];
    }

    private static async System.Threading.Tasks.Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static Stats Load()
    {
        try { if (File.Exists(StatsFile)) return JsonSerializer.Deserialize<Stats>(File.ReadAllText(StatsFile)) ?? new Stats(); }
        catch { }
        return new Stats();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StatsFile, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
