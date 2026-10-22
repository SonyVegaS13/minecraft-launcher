using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    private async void CloudLegacyImport_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? cloud = _cloudSession;
        if (cloud is null || _cloudOffline) { LegacyImportStatus.Text = "Для переноса скина подключись к Solaris ID."; return; }
        string password = LegacyPasswordBox.Password; LegacyPasswordBox.Clear();
        try
        {
            LocalAccount? original = await ReadAccountAsync();
            if (original is null) throw new InvalidOperationException("На этом ПК нет старого локального аккаунта.");
            byte[] actual = HashPassword(password, Convert.FromBase64String(original.Salt));
            byte[] expected = Convert.FromBase64String(original.PasswordHash);
            bool verified = CryptographicOperations.FixedTimeEquals(actual, expected);
            CryptographicOperations.ZeroMemory(actual); CryptographicOperations.ZeroMemory(expected);
            if (!verified) throw new InvalidOperationException("Пароль старого локального аккаунта неверен.");
            if (MessageBox.Show(this, "Перенести доступный скин старого локального профиля?\nЛокальные часы и исходные данные останутся на ПК. Они не прибавляются к подтверждённой серверной статистике.",
                "Solaris ID — безопасный перенос", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            string safeNick = new string(original.Username.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string legacySkinPath = Path.Combine(_stateDir, "profiles", safeNick, "skin.png");
            if (!ReferenceEquals(cloud, _cloudSession)) return;
            if (File.Exists(legacySkinPath))
            {
                byte[] skin = await File.ReadAllBytesAsync(legacySkinPath); DecodeSkin(skin);
                if (skin.Length > 524288) throw new InvalidDataException("Скин превышает лимит 512 КиБ.");
                if (!ReferenceEquals(cloud, _cloudSession)) return;
                string destination = CurrentSkinPath(); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                    File.Copy(destination, destination + ".before-legacy-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), overwrite: false);
                await File.WriteAllBytesAsync(destination, skin); await UploadCloudSkinAsync(skin, cloud);
            }
            if (!ReferenceEquals(cloud, _cloudSession)) return;
            LegacyImportStatus.Text = "Исходный аккаунт, настройки и локальная история сохранены. Доступный скин скопирован; серверные часы не изменены.";
            await PrepareSkinPreviewAsync(cloud.Nickname);
        }
        catch (Exception ex) { LegacyImportStatus.Text = "Перенос не выполнен: " + ex.Message; }
    }
}
