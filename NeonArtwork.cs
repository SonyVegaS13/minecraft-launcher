using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SolarisLauncher;

public partial class MainWindow
{
    // Optional artwork can be shipped alongside a test EXE as a ZIP or as separate files.
    // The official embedded SolarisEmber.bmp remains the safe fallback.
    private void TryLoadNeonArt()
    {
        try
        {
            string root = AppContext.BaseDirectory;
            string assetsDir = Path.Combine(root, "Assets");
            var images = new List<NeonArtwork>();

            if (Directory.Exists(assetsDir))
            {
                foreach (string file in Directory.EnumerateFiles(assetsDir, "*.*", SearchOption.AllDirectories))
                {
                    if (!IsSupportedImage(file)) continue;
                    var info = new FileInfo(file);
                    if (info.Length < 20_000 || info.Length > 16_000_000) continue;
                    images.Add(new NeonArtwork(Path.GetFileName(file).ToLowerInvariant(), File.ReadAllBytes(file)));
                }
            }

            string[] archives =
            {
                Path.Combine(root, "Solaris-Neon-UI(1).zip"),
                Path.Combine(root, "Solaris-Neon-UI.zip")
            };
            foreach (string archivePath in archives)
            {
                if (!File.Exists(archivePath)) continue;
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!IsSupportedImage(entry.Name) ||
                        entry.Length < 20_000 || entry.Length > 16_000_000) continue;

                    using Stream input = entry.Open();
                    using var output = new MemoryStream();
                    input.CopyTo(output);
                    if (output.Length > 16_000_000) continue;

                    images.Add(new NeonArtwork(entry.FullName.ToLowerInvariant(), output.ToArray()));
                }
                break;
            }

            if (images.Count == 0) return;

            var sorted = images.OrderByDescending(i => i.Data.Length).ToList();
            NeonArtwork? login = Find(sorted, "login", "auth", "вход", "signin", "entry")
                ?? Find(sorted, "login-bg", "background", "backdrop", "wallpaper", "фон")
                ?? sorted.FirstOrDefault();

            NeonArtwork? dashboard = Find(sorted, "dashboard", "main-bg", "main_background", "desktop", "home", "главн")
                ?? sorted.Skip(1).FirstOrDefault() ?? login;
            NeonArtwork? vanilla = Find(sorted, "vanilla", "survival", "classic")
                ?? dashboard;
            NeonArtwork? modded = Find(sorted, "modded", "forge", "endportal", "modpack")
                ?? sorted.Skip(2).FirstOrDefault() ?? dashboard;

            if (login is not null && Decode(login.Data) is BitmapImage loginBitmap)
                LoginBackdrop.Background = new ImageBrush(loginBitmap) { Stretch = Stretch.UniformToFill };
            if (dashboard is not null && Decode(dashboard.Data) is BitmapImage dashboardBitmap)
                MainView.Background = new ImageBrush(dashboardBitmap) { Stretch = Stretch.UniformToFill, Opacity = 0.32 };
            if (vanilla is not null && Decode(vanilla.Data) is BitmapImage vanillaBitmap)
                VanillaArtwork.Source = vanillaBitmap;
            if (modded is not null && Decode(modded.Data) is BitmapImage moddedBitmap)
                ModdedArtwork.Source = moddedBitmap;
        }
        catch (Exception ex)
        {
            // Missing/malformed ZIP must not stop the launcher from opening.
            System.Diagnostics.Debug.WriteLine("Solaris artwork could not be loaded: " + ex);
        }
    }

    private static NeonArtwork? Find(IEnumerable<NeonArtwork> images, params string[] keys)
        => images.FirstOrDefault(item => keys.Any(key =>
            item.Name.Contains(key, StringComparison.OrdinalIgnoreCase)));

    private static bool IsSupportedImage(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            var result = new BitmapImage();
            result.BeginInit();
            result.CacheOption = BitmapCacheOption.OnLoad;
            result.DecodePixelWidth = 1920;
            result.StreamSource = memory;
            result.EndInit();
            result.Freeze();
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record NeonArtwork(string Name, byte[] Data);
}
