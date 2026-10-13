using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SolarisLauncher;

// Actions for the selected two-world Solaris dashboard.
public partial class MainWindow
{
    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_gameDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + _gameDir + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть папку Minecraft.\n" + ex.Message,
                "Solaris", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AboutNav_Click(object sender, RoutedEventArgs e)
        => AboutOverlay.Visibility = Visibility.Visible;

    private void AboutBack_Click(object sender, RoutedEventArgs e)
        => AboutOverlay.Visibility = Visibility.Collapsed;
}
