using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SolarisLauncher;

internal static class AchievementsBootstrap
{
    private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solaris");
    private static readonly string AchievementsFile = Path.Combine(StateDir, "achievements.json");
    private static readonly HashSet<string> Unlocked = new(StringComparer.OrdinalIgnoreCase);

    static AchievementsBootstrap()
    {
        try
        {
            if (File.Exists(AchievementsFile))
            {
                string json = File.ReadAllText(AchievementsFile);
                foreach (string? id in JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(id)) Unlocked.Add(id);
            }
        }
        catch { }
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(OnButtonClick),
            true);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                if (window.MainView.Visibility == Visibility.Visible)
                {
                    Unlock("welcome");
                    UpdateUi(window);
                }
            }));
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (Window.GetWindow(button) is not MainWindow window) return;

        if (string.Equals(button.Name, "VanillaPlayButton", StringComparison.Ordinal))
        {
            Unlock("first_launch");
            Unlock("vanilla");
        }
        else if (string.Equals(button.Name, "PlayButton", StringComparison.Ordinal))
        {
            Unlock("first_launch");
            Unlock("modded");
        }
        else
        {
            return;
        }

        if (Unlocked.Contains("vanilla") && Unlocked.Contains("modded"))
            Unlock("explorer");

        UpdateUi(window);
    }

    private static void Unlock(string id)
    {
        if (!Unlocked.Add(id)) return;
        try
        {
            Directory.CreateDirectory(StateDir);
            string[] saved = Unlocked
                .Where(x => x is "welcome" or "first_launch" or "explorer")
                .OrderBy(x => x)
                .ToArray();
            File.WriteAllText(AchievementsFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void UpdateUi(MainWindow window)
    {
        int count = 0;
        if (Unlocked.Contains("welcome")) count++;
        if (Unlocked.Contains("first_launch")) count++;
        if (Unlocked.Contains("explorer")) count++;

        window.AchievementsCount.Text = $"{count} / 3";
        SetAchievement(window.AchievementWelcome, Unlocked.Contains("welcome"), "Добро пожаловать — аккаунт создан");
        SetAchievement(window.AchievementFirstLaunch, Unlocked.Contains("first_launch"), "Первый запуск — запусти Minecraft");
        SetAchievement(window.AchievementExplorer, Unlocked.Contains("explorer"), "Исследователь — попробуй Vanilla и Modded");
    }

    private static void SetAchievement(TextBlock textBlock, bool unlocked, string title)
    {
        textBlock.Text = unlocked ? $"✓  {title}" : $"○  {title}";
        textBlock.Foreground = unlocked
            ? new SolidColorBrush(Color.FromRgb(134, 239, 172))
            : new SolidColorBrush(Color.FromRgb(94, 102, 118));
    }
}
