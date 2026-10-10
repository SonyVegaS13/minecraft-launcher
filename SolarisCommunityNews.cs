using System;
using System.Diagnostics;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    // Add the two *official* URLs here when the Solaris website and Telegram
    // channel are publicly announced. Never publish guessed addresses.
    // The DEV updater distributes future changes without reinstalling.
    private const string OfficialWebsiteUrl = "";
    private const string OfficialTelegramUrl = "";

    private static bool IsSafeExternalUrl(string value, bool telegram)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? address) ||
            address.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(address.Host) ||
            !string.IsNullOrWhiteSpace(address.UserInfo))
            return false;

        return !telegram ||
            address.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
            address.Host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase);
    }

    private void NewsNav_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Collapsed;
        ControlView.Visibility = Visibility.Collapsed;

        bool siteReady = IsSafeExternalUrl(OfficialWebsiteUrl, telegram: false);
        bool channelReady = IsSafeExternalUrl(OfficialTelegramUrl, telegram: true);
        NewsWebsiteButton.IsEnabled = siteReady;
        NewsTelegramButton.IsEnabled = channelReady;
        NewsWebsiteButton.Content = siteReady ? "ОТКРЫТЬ САЙТ  ↗" : "САЙТ — СКОРО";
        NewsTelegramButton.Content = channelReady ? "ОТКРЫТЬ TELEGRAM  ↗" : "TELEGRAM — СКОРО";

        NewsView.Visibility = Visibility.Visible;
    }

    private void NewsWebsite_Click(object sender, RoutedEventArgs e) =>
        OpenExternalNewsLink(OfficialWebsiteUrl, telegram: false);

    private void NewsTelegram_Click(object sender, RoutedEventArgs e) =>
        OpenExternalNewsLink(OfficialTelegramUrl, telegram: true);

    private void OpenExternalNewsLink(string url, bool telegram)
    {
        if (!IsSafeExternalUrl(url, telegram))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "Не удалось открыть ссылку. Проверьте браузер по умолчанию.",
                "Solaris — новости", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
