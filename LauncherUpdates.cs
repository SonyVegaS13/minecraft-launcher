using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    private const string LauncherVersion = "2.1.0";
    private const string UpdateManifestUrl =
        "https://raw.githubusercontent.com/SonyVegaS13/minecraft-launcher/solaris-2.1-polish/update.json";

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            await window.CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                await _http.GetStringAsync(UpdateManifestUrl));

            if (!document.RootElement.TryGetProperty("launcher", out JsonElement launcher))
                return;

            string latestVersion = launcher.TryGetProperty("version", out JsonElement version)
                ? version.GetString() ?? LauncherVersion
                : LauncherVersion;

            string downloadUrl = launcher.TryGetProperty("url", out JsonElement url)
                ? url.GetString() ?? ""
                : "";

            if (!Version.TryParse(latestVersion, out Version? latest) ||
                !Version.TryParse(LauncherVersion, out Version? current) ||
                latest <= current ||
                string.IsNullOrWhiteSpace(downloadUrl))
                return;

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
            // Обновления не должны мешать запуску лаунчера.
        }
    }

    private async Task StartUpdateAsync(string downloadUrl)
    {
        string currentLauncherPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к лаунчеру.");

        string launcherDirectory = Path.GetDirectoryName(currentLauncherPath)
            ?? throw new InvalidOperationException("Не удалось определить папку лаунчера.");

        string updaterPath = Path.Combine(launcherDirectory, "SolarisUpdater.exe");
        string newLauncherPath = Path.Combine(launcherDirectory, "SolarisLauncher_New.exe");

        if (!File.Exists(updaterPath))
        {
            MessageBox.Show(
                "SolarisUpdater.exe не найден рядом с лаунчером.",
                "Ошибка обновления",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        try
        {
            byte[] newLauncher = await _http.GetByteArrayAsync(downloadUrl);

            if (newLauncher.Length < 1_000_000)
                throw new InvalidOperationException("Скачанный EXE выглядит некорректно.");

            await File.WriteAllBytesAsync(newLauncherPath, newLauncher);

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"\"{currentLauncherPath}\" \"{newLauncherPath}\"",
                WorkingDirectory = launcherDirectory,
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(newLauncherPath))
                    File.Delete(newLauncherPath);
            }
            catch
            {
            }

            MessageBox.Show(
                ex.Message,
                "Ошибка обновления Solaris Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
