using System.Windows.Controls;

namespace SolarisLauncher;

// Keeps older non-visual helpers compatible with the new server-selector UI.
public partial class MainWindow
{
    internal TextBlock ProfileText { get; } = new();
    internal TextBlock AchievementsCount { get; } = new();
    internal TextBlock AchievementWelcome { get; } = new();
    internal TextBlock AchievementFirstLaunch { get; } = new();
    internal TextBlock AchievementExplorer { get; } = new();
}
