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

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_serverStatusTimer is not null)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            InstallVanillaStatusBadge();
            _ = RefreshVanillaServerStatusAsync();

            _serverStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _serverStatusTimer.Tick += async (_, _) => await RefreshVanillaServerStatusAsync();
            _serverStatusTimer.Start();
        }), DispatcherPriority.Loaded);
    }

    private void InstallVanillaStatusBadge()
    {
        if (_vanillaStatusBadge is not null)
            return;

        TextBlock? title = FindTextBlockByText(MainView, "SOLARIS VANILLA");
        if (title is null)
            return;

        if (LogicalTreeHelper.GetParent(title) is not StackPanel titlePanel ||
            LogicalTreeHelper.GetParent(titlePanel) is not StackPanel headerPanel)
            return;

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
    }

    private async Task RefreshVanillaServerStatusAsync()
    {
        if (!IsLoaded || _vanillaStatusText is null || _vanillaStatusBadge is null)
            return;

        SetVanillaStatus("ПРОВЕРКА...", Color.FromRgb(196, 181, 253), Color.FromRgb(27, 21, 49));

        try
        {
            ServerStatusResult result = await QueryMinecraftStatusAsync(ServerHost, ServerPort, CancellationToken.None);

            if (result.Online)
            {
                string players = result.MaxPlayers > 0
                    ? $"{result.OnlinePlayers}/{result.MaxPlayers}"
                    : result.OnlinePlayers.ToString();
                SetVanillaStatus($"ОНЛАЙН  •  {players}  •  {result.PingMs} MS", Color.FromRgb(134, 239, 172), Color.FromRgb(15, 45, 31));
            }
            else
            {
                SetVanillaStatus("ОФЛАЙН", Color.FromRgb(248, 113, 113), Color.FromRgb(55, 23, 29));
            }
        }
        catch
        {
            SetVanillaStatus("ОФЛАЙН", Color.FromRgb(248, 113, 113), Color.FromRgb(55, 23, 29));
        }
    }

    private void SetVanillaStatus(string text, Color foreground, Color background)
    {
        if (_vanillaStatusText is null || _vanillaStatusBadge is null)
            return;

        _vanillaStatusText.Text = text;
        _vanillaStatusText.Foreground = new SolidColorBrush(foreground);
        _vanillaStatusBadge.Background = new SolidColorBrush(background);
    }

    private static async Task<ServerStatusResult> QueryMinecraftStatusAsync(string host, int port, CancellationToken token)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
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
        if (packetLength <= 0 || packetLength > 1_000_000)
            return ServerStatusResult.Offline;

        byte[] packet = await ReadExactAsync(stream, packetLength, token);
        stopwatch.Stop();

        using MemoryStream response = new(packet, writable: false);
        int packetId = ReadVarInt(response);
        if (packetId != 0)
            return ServerStatusResult.Offline;

        int jsonLength = ReadVarInt(response);
        if (jsonLength <= 0 || jsonLength > response.Length - response.Position)
            return ServerStatusResult.Offline;

        byte[] json = new byte[jsonLength];
        int read = 0;
        while (read < json.Length)
        {
            int chunk = await response.ReadAsync(json.AsMemory(read, json.Length - read), token);
            if (chunk == 0)
                return ServerStatusResult.Offline;
            read += chunk;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        int online = 0;
        int max = 0;
        if (root.TryGetProperty("players", out JsonElement players))
        {
            if (players.TryGetProperty("online", out JsonElement onlineElement))
                online = onlineElement.GetInt32();
            if (players.TryGetProperty("max", out JsonElement maxElement))
                max = maxElement.GetInt32();
        }

        return new ServerStatusResult(true, online, max, Math.Max(1, stopwatch.ElapsedMilliseconds));
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
            if ((value & 0x80) == 0)
                return result;
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
            if (value < 0)
                throw new EndOfStreamException();
            result |= (value & 0x7F) << shift;
            if ((value & 0x80) == 0)
                return result;
            shift += 7;
        }
        throw new InvalidOperationException("Некорректный Minecraft VarInt.");
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[1];
        int read = await stream.ReadAsync(buffer.AsMemory(), token);
        if (read == 0)
            throw new EndOfStreamException();
        return buffer[0];
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
    {
        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), token);
            if (chunk == 0)
                throw new EndOfStreamException();
            read += chunk;
        }
        return buffer;
    }

    private static TextBlock? FindTextBlockByText(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock && string.Equals(textBlock.Text, text, StringComparison.Ordinal))
                return textBlock;

            if (child is DependencyObject dependencyChild)
            {
                TextBlock? result = FindTextBlockByText(dependencyChild, text);
                if (result is not null)
                    return result;
            }
        }

        return null;
    }

    private readonly record struct ServerStatusResult(bool Online, int OnlinePlayers, int MaxPlayers, long PingMs)
    {
        public static ServerStatusResult Offline => new(false, 0, 0, 0);
    }
}
