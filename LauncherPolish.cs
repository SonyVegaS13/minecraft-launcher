using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SolarisLauncher;

public partial class MainWindow
{
    private Border? _vanillaStatusBadge;
    private TextBlock? _vanillaStatusText;
    private DispatcherTimer? _serverStatusTimer;
    private bool _uiPolished;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_serverStatusTimer is not null) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            InstallVanillaStatusBadge();
            PolishMainView();
            _ = RefreshVanillaServerStatusAsync();
            _serverStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _serverStatusTimer.Tick += async (_, _) => await RefreshVanillaServerStatusAsync();
            _serverStatusTimer.Start();
        }), DispatcherPriority.Loaded);
    }

    private void InstallVanillaStatusBadge()
    {
        if (_vanillaStatusBadge is not null) return;
        TextBlock? title = FindTextBlockByText(MainView, "SOLARIS VANILLA");
        if (title is null) return;
        if (LogicalTreeHelper.GetParent(title) is not StackPanel titlePanel || LogicalTreeHelper.GetParent(titlePanel) is not StackPanel headerPanel) return;

        _vanillaStatusText = new TextBlock
        {
            Text = "ПРОВЕРКА...",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(196, 181, 253)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _vanillaStatusBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(27, 21, 49)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 50, 106)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _vanillaStatusText
        };
        headerPanel.Children.Add(_vanillaStatusBadge);

        // The compact badge is now the single source of truth for the server state.
        TextBlock? legacyStatus = FindTextBlockByText(MainView, " Сервер готов");
        if (legacyStatus is not null && LogicalTreeHelper.GetParent(legacyStatus) is StackPanel legacyRow)
            legacyRow.Visibility = Visibility.Collapsed;
    }

    private void PolishMainView()
    {
        if (_uiPolished || MainView is null) return;
        _uiPolished = true;

        // Make the lower content breathe instead of leaving a large dead area.
        TextBlock? newsTitle = FindTextBlockByText(MainView, "📰  НОВОСТИ SOLARIS");
        if (newsTitle is not null && FindAncestor<Border>(newsTitle) is Border newsCard)
            newsCard.MinHeight = 150;

        TextBlock? quickTitle = FindTextBlockByText(MainView, "⚡  БЫСТРЫЙ СТАТУС");
        if (quickTitle is not null && FindAncestor<Border>(quickTitle) is Border quickCard)
            quickCard.MinHeight = 150;

        if (Progress is not null)
        {
            Progress.Height = 5;
            Progress.Foreground = new SolidColorBrush(Color.FromRgb(139, 92, 246));
            Progress.Background = new SolidColorBrush(Color.FromRgb(28, 33, 45));
        }

        // Keep the existing Minecraft skin-head avatar untouched.
        TextBlock? profileName = FindTextBlockByText(MainView, "Игрок Solaris");
        if (profileName is not null && !string.IsNullOrWhiteSpace(WelcomeText.Text))
            profileName.Text = WelcomeText.Text;

        PolishAchievements();
    }

    private void PolishAchievements()
    {
        TextBlock? achievementTitle = FindTextBlockByText(MainView, "🏆  ДОСТИЖЕНИЯ");
        if (achievementTitle is null) achievementTitle = FindTextBlockByText(MainView, "🏆 ДОСТИЖЕНИЯ");
        if (achievementTitle is null) return;

        Border? card = FindAncestor<Border>(achievementTitle);
        StackPanel? content = card is null ? null : FindDescendant<StackPanel>(card);
        if (content is null) return;

        TextBlock? counter = FindTextBlockContaining(content, "/ 3");
        if (counter is not null) counter.Text = "2 / 4";

        SetAchievement(content, "Добро пожаловать", "✓ Добро пожаловать — аккаунт создан", true);
        SetAchievement(content, "Первый запуск", "✓ Первый запуск — запусти Minecraft", true);
        SetAchievement(content, "Исследователь", "□ Исследователь — попробуй Vanilla и Modded", false);

        if (!content.Children.OfType<TextBlock>().Any(x => x.Text.StartsWith("□ Покоритель мира", StringComparison.Ordinal)))
        {
            content.Children.Add(new TextBlock
            {
                Text = "□ Покоритель мира — исследуй Solaris",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(111, 122, 140)),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
    }

    private static void SetAchievement(StackPanel content, string name, string text, bool unlocked)
    {
        TextBlock? item = content.Children.OfType<TextBlock>().FirstOrDefault(x => x.Text.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        item.Text = text;
        item.Foreground = new SolidColorBrush(unlocked ? Color.FromRgb(134, 239, 172) : Color.FromRgb(111, 122, 140));
    }

    private async Task RefreshVanillaServerStatusAsync()
    {
        if (!IsLoaded || _vanillaStatusText is null || _vanillaStatusBadge is null) return;
        SetVanillaStatus("ПРОВЕРКА...", Color.FromRgb(196, 181, 253), Color.FromRgb(27, 21, 49));
        try
        {
            ServerStatusResult result = await QueryMinecraftStatusAsync2(ServerHost, ServerPort, CancellationToken.None);
            if (result.Online)
            {
                string players = result.MaxPlayers > 0 ? $"{result.OnlinePlayers}/{result.MaxPlayers}" : result.OnlinePlayers.ToString();
                SetVanillaStatus($"ОНЛАЙН  •  {players}  •  {result.PingMs} MS", Color.FromRgb(134, 239, 172), Color.FromRgb(15, 45, 31));
            }
            else SetVanillaStatus("ОФЛАЙН", Color.FromRgb(248, 113, 113), Color.FromRgb(55, 23, 29));
        }
        catch { SetVanillaStatus("ОФЛАЙН", Color.FromRgb(248, 113, 113), Color.FromRgb(55, 23, 29)); }
    }

    private void SetVanillaStatus(string text, Color foreground, Color background)
    {
        if (_vanillaStatusText is null || _vanillaStatusBadge is null) return;
        _vanillaStatusText.Text = text;
        _vanillaStatusText.Foreground = new SolidColorBrush(foreground);
        _vanillaStatusBadge.Background = new SolidColorBrush(background);
    }

    private static async Task<ServerStatusResult> QueryMinecraftStatusAsync2(string host, int port, CancellationToken token)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using TcpClient client = new();
        await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(4), token);
        using NetworkStream stream = client.GetStream();
        using MemoryStream handshake = new();
        WriteVarInt2(handshake, 0); WriteVarInt2(handshake, -1); WriteString2(handshake, host);
        handshake.WriteByte((byte)(port >> 8)); handshake.WriteByte((byte)(port & 0xFF)); WriteVarInt2(handshake, 1);
        await WritePacketAsync2(stream, handshake.ToArray(), token);
        await WritePacketAsync2(stream, new byte[] { 0 }, token);

        int packetLength = await ReadVarIntAsync2(stream, token);
        if (packetLength <= 0 || packetLength > 1_000_000) return ServerStatusResult.Offline;
        byte[] packet = await ReadExactAsync2(stream, packetLength, token);
        stopwatch.Stop();
        using MemoryStream response = new(packet, writable: false);
        if (ReadVarInt2(response) != 0) return ServerStatusResult.Offline;
        int jsonLength = ReadVarInt2(response);
        if (jsonLength <= 0 || jsonLength > response.Length - response.Position) return ServerStatusResult.Offline;
        byte[] json = new byte[jsonLength];
        int read = 0;
        while (read < json.Length)
        {
            int chunk = await response.ReadAsync(json.AsMemory(read, json.Length - read), token);
            if (chunk == 0) return ServerStatusResult.Offline;
            read += chunk;
        }
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        int online = 0, max = 0;
        if (root.TryGetProperty("players", out JsonElement players))
        {
            if (players.TryGetProperty("online", out JsonElement onlineElement)) online = onlineElement.GetInt32();
            if (players.TryGetProperty("max", out JsonElement maxElement)) max = maxElement.GetInt32();
        }
        return new ServerStatusResult(true, online, max, Math.Max(1, stopwatch.ElapsedMilliseconds));
    }

    private static async Task WritePacketAsync2(Stream stream, byte[] payload, CancellationToken token)
    {
        using MemoryStream packet = new(); WriteVarInt2(packet, payload.Length);
        await stream.WriteAsync(packet.ToArray().AsMemory(), token); await stream.WriteAsync(payload.AsMemory(), token);
    }
    private static void WriteVarInt2(Stream stream, int value)
    {
        uint unsigned = unchecked((uint)value);
        while ((unsigned & ~0x7Fu) != 0) { stream.WriteByte((byte)((unsigned & 0x7F) | 0x80)); unsigned >>= 7; }
        stream.WriteByte((byte)unsigned);
    }
    private static void WriteString2(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value); WriteVarInt2(stream, bytes.Length); stream.Write(bytes, 0, bytes.Length);
    }
    private static async Task<int> ReadVarIntAsync2(Stream stream, CancellationToken token)
    {
        int result = 0, shift = 0;
        for (int i = 0; i < 5; i++) { int value = await ReadByteAsync2(stream, token); result |= (value & 0x7F) << shift; if ((value & 0x80) == 0) return result; shift += 7; }
        throw new InvalidOperationException("Некорректный Minecraft VarInt.");
    }
    private static int ReadVarInt2(Stream stream)
    {
        int result = 0, shift = 0;
        for (int i = 0; i < 5; i++) { int value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); result |= (value & 0x7F) << shift; if ((value & 0x80) == 0) return result; shift += 7; }
        throw new InvalidOperationException("Некорректный Minecraft VarInt.");
    }
    private static async Task<int> ReadByteAsync2(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[1]; int read = await stream.ReadAsync(buffer.AsMemory(), token); if (read == 0) throw new EndOfStreamException(); return buffer[0];
    }
    private static async Task<byte[]> ReadExactAsync2(Stream stream, int length, CancellationToken token)
    {
        byte[] buffer = new byte[length]; int read = 0;
        while (read < length) { int chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), token); if (chunk == 0) throw new EndOfStreamException(); read += chunk; }
        return buffer;
    }

    private static TextBlock? FindTextBlockByText(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock && string.Equals(textBlock.Text, text, StringComparison.Ordinal)) return textBlock;
            if (child is DependencyObject dependencyChild) { TextBlock? result = FindTextBlockByText(dependencyChild, text); if (result is not null) return result; }
        }
        return null;
    }

    private static TextBlock? FindTextBlockContaining(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock && textBlock.Text.Contains(text, StringComparison.Ordinal)) return textBlock;
            if (child is DependencyObject dependencyChild) { TextBlock? result = FindTextBlockContaining(dependencyChild, text); if (result is not null) return result; }
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = LogicalTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is T match) return match;
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match) return match;
            if (child is DependencyObject dependencyChild) { T? result = FindDescendant<T>(dependencyChild); if (result is not null) return result; }
        }
        return null;
    }

    private readonly record struct ServerStatusResult(bool Online, int OnlinePlayers, int MaxPlayers, long PingMs)
    { public static ServerStatusResult Offline => new(false, 0, 0, 0); }
}
