using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SolarisLauncher;

internal static class ProfileStatsBootstrap
{
    private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solaris");
    private static readonly string StatsFile = Path.Combine(StateDir, "profile-stats.json");
    private static readonly string AchievementsFile = Path.Combine(StateDir, "achievements.json");

    private sealed class Stats
    {
        public int Games { get; set; }
    }

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
            UpdateProfile(window);
            StartServerStatusTimer(window);
        }));
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (Window.GetWindow(button) is not MainWindow window) return;
        if (button.Name is not ("VanillaPlayButton" or "PlayButton")) return;

        Current.Games++;
        Save();
        UpdateProfile(window);
    }

    private static void StartServerStatusTimer(MainWindow window)
    {
        if (window.Resources["SolarisProfileTimerStarted"] is true) return;
        window.Resources["SolarisProfileTimerStarted"] = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += async (_, _) =>
        {
            if (window.MainView.Visibility != Visibility.Visible) return;
            bool online;
            try
            {
                online = await PingAsync("solarisplay.millida.host", 25565);
            }
            catch { online = false; }
            SetServerMetric(window, online);
        };
        timer.Start();
        _ = RefreshServerMetricAsync(window);
    }

    private static async System.Threading.Tasks.Task RefreshServerMetricAsync(MainWindow window)
    {
        bool online;
        try { online = await PingAsync("solarisplay.millida.host", 25565); }
        catch { online = false; }
        if (window.Dispatcher.HasShutdownStarted) return;
        window.Dispatcher.Invoke(() => SetServerMetric(window, online));
    }

    private static async System.Threading.Tasks.Task<bool> PingAsync(string host, int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(3));
        return client.Connected;
    }

    private static void UpdateProfile(MainWindow window)
    {
        TextBlock? games = FindMetricValue(window.MainView, "ИГРЫ");
        if (games is not null) games.Text = Current.Games.ToString();

        TextBlock? level = FindMetricValue(window.MainView, "LEVEL");
        if (level is not null)
        {
            int xp = Current.Games * 20 + LoadAchievementCount() * 25;
            int levelNumber = Math.Max(1, xp / 100 + 1);
            level.Text = levelNumber.ToString();
            level.ToolTip = $"{xp % 100} / 100 XP до следующего уровня";
        }
    }

    private static int LoadAchievementCount()
    {
        try
        {
            if (!File.Exists(AchievementsFile)) return 0;
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(AchievementsFile)) ?? Array.Empty<string>()).Length;
        }
        catch { return 0; }
    }

    private static void SetServerMetric(MainWindow window, bool online)
    {
        TextBlock? label = FindText(window.MainView, "ОНЛАЙН");
        if (label is not null) label.Text = "СЕРВЕР";
        TextBlock? value = FindMetricValue(window.MainView, "СЕРВЕР");
        if (value is not null)
        {
            value.Text = online ? "Online" : "Offline";
            value.Foreground = new System.Windows.Media.SolidColorBrush(
                online ? System.Windows.Media.Color.FromRgb(134, 239, 172) : System.Windows.Media.Color.FromRgb(248, 113, 113));
        }
    }

    private static TextBlock? FindMetricValue(DependencyObject root, string label)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is StackPanel panel)
            {
                var texts = panel.Children.OfType<TextBlock>().ToList();
                if (texts.Count >= 2 && texts[0].Text == label) return texts[1];
            }
            if (child is DependencyObject dependencyChild)
            {
                var result = FindMetricValue(dependencyChild, label);
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
            if (child is DependencyObject dependencyChild)
            {
                var result = FindText(dependencyChild, text);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static Stats Load()
    {
        try
        {
            if (File.Exists(StatsFile)) return JsonSerializer.Deserialize<Stats>(File.ReadAllText(StatsFile)) ?? new Stats();
        }
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
