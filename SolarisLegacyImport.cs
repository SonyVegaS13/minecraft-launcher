using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    private async void CloudLegacyImport_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? cloud = _cloudSession;
        if (cloud is null || _cloudOffline)
        {
            LegacyImportStatus.Text = "Для переноса сначала подключись к Solaris ID.";
            return;
        }
        string password = LegacyPasswordBox.Password;
        LegacyPasswordBox.Clear();
        if (password.Length == 0)
        {
            LegacyImportStatus.Text = "Введите пароль старого локального аккаунта.";
            return;
        }
        try
        {
            LocalAccount? original = await ReadAccountAsync();
            if (original is null)
                throw new InvalidOperationException("На этом ПК нет старого локального аккаунта.");
            byte[] actual = HashPassword(password, Convert.FromBase64String(original.Salt));
            byte[] expected = Convert.FromBase64String(original.PasswordHash);
            bool verified = CryptographicOperations.FixedTimeEquals(actual, expected);
            CryptographicOperations.ZeroMemory(actual);
            if (!verified)
                throw new InvalidOperationException("Пароль старого локального аккаунта неверен.");

            string safeNick = new string(original.Username.ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string activityPath = Path.Combine(_stateDir, "profiles", safeNick, "activity.json");
            var activity = new PlayerActivity();
            if (File.Exists(activityPath))
            {
                activity = JsonSerializer.Deserialize<PlayerActivity>(
                    await File.ReadAllTextAsync(activityPath)) ?? new PlayerActivity();
            }
            string sourceKey = "legacy-v1:" + original.Username.ToLowerInvariant() +
                ":" + original.Salt;
            string sourceId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)));

            if (MessageBox.Show(this,
                $"Импортировать игровое время из локального профиля {original.Username} в " +
                $"SOLARIS ID {cloud.Nickname}?\n\n" +
                $"Vanilla: {DisplayDuration(activity.VanillaSeconds)}\n" +
                $"Modded: {DisplayDuration(activity.ModdedSeconds)}\n\n" +
                "Оригинальные данные останутся на ПК.",
                "SOLARIS ID — перенос", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var payload = JsonContent.Create(new
            {
                sourceId,
                vanillaSeconds = activity.VanillaSeconds,
                moddedSeconds = activity.ModdedSeconds
            });
            using HttpResponseMessage response = await SendCloudAsync(client, cloud,
                HttpMethod.Post, "api/v1/me/legacy", payload);
            response.EnsureSuccessStatusCode();
            LegacyImportStatus.Text = "Игровые часы перенесены (без дублирования). " +
                "Локальный аккаунт и его файлы не удалены.";
            await SyncCloudDataAsync(cloud.Nickname);
        }
        catch (Exception ex)
        {
            LegacyImportStatus.Text = "Импорт не выполнен: " + ex.Message;
        }
    }
}
