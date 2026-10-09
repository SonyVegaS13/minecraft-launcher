using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SolarisLauncher;

public partial class MainWindow
{
    // The original embedded bitmap is a fallback, NOT the approved neon design.
    // Users can import Solaris-Neon-UI.zip once; it is then cached outside the EXE folder.
    private static readonly string UiDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solaris", "UI");
    private static readonly string CachedNeonArchive = Path.Combine(UiDirectory, "Solaris-Neon-UI.zip");
    private static readonly string ArtworkLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Solaris", "logs", "artwork.log");

    private bool TryLoadNeonArt(string? selectedPath = null, bool notify = false)
    {
        var report = new StringBuilder();
        report.AppendLine($"[{DateTimeOffset.Now:O}] Solaris Neon UI 2.2.2");
        bool loaded = false;
        string? loadedFrom = null;
        try
        {
            IEnumerable<string> candidates = selectedPath is not null
                ? new[] { selectedPath }
                : FindArtworkCandidates();

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                report.AppendLine("Checking: " + candidate);
                if (!File.Exists(candidate)) continue;
                try
                {
                    List<ArtworkImage> images = ReadArtwork(candidate, report);
                    if (images.Count == 0)
                    {
                        report.AppendLine("No supported wide images in this file.");
                        continue;
                    }

                    // Prefer named art, then the largest wide landscape. Do not use icons or logos as backdrops.
                    List<ArtworkImage> wide = images
                        .Where(x => x.Width >= 640 && x.Height >= 300 && x.Width >= x.Height * 1.25)
                        .OrderByDescending(x => x.Width * (long)x.Height)
                        .ToList();

                    if (wide.Count == 0)
                    {
                        report.AppendLine("Image dimensions unsuitable for a landscape backdrop.");
                        continue;
                    }

                    ArtworkImage login = Pick(wide, "login", "auth", "signin", "вход", "login-bg") ?? wide[0];
                    ArtworkImage main = Pick(wide, "dashboard", "main-bg", "main_background", "home", "главн", "main")
                        ?? wide.FirstOrDefault(x => !ReferenceEquals(x, login)) ?? login;
                    ArtworkImage vanilla = Pick(wide, "vanilla", "survival", "classic")
                        ?? wide.FirstOrDefault(x => !ReferenceEquals(x, login)) ?? main;
                    ArtworkImage modded = Pick(wide, "modded", "forge", "endportal", "modpack")
                        ?? wide.FirstOrDefault(x => !ReferenceEquals(x, login) && !ReferenceEquals(x, vanilla))
                        ?? main;

                    LoginBackdrop.Background = new ImageBrush(login.Bitmap) { Stretch = Stretch.UniformToFill };
                    MainView.Background = new ImageBrush(main.Bitmap) { Stretch = Stretch.UniformToFill, Opacity = 0.30 };
                    VanillaArtwork.Source = vanilla.Bitmap;
                    VanillaArtworkSharp.Source = vanilla.Bitmap;
                    ModdedArtwork.Source = modded.Bitmap;
                    ModdedArtworkSharp.Source = modded.Bitmap;

                    loaded = true;
                    loadedFrom = candidate;
                    report.AppendLine("APPLIED login: " + login.Name);
                    report.AppendLine("APPLIED main: " + main.Name);
                    report.AppendLine("APPLIED vanilla: " + vanilla.Name);
                    report.AppendLine("APPLIED modded: " + modded.Name);

                    if (selectedPath is not null && candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(UiDirectory);
                        if (!Path.GetFullPath(candidate).Equals(Path.GetFullPath(CachedNeonArchive), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(candidate, CachedNeonArchive, true);
                            report.AppendLine("Saved for next launch: " + CachedNeonArchive);
                        }
                    }
                    break;
                }
                catch (Exception ex)
                {
                    report.AppendLine("Could not read " + candidate + ": " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("Unexpected artwork loader error: " + ex);
        }

        if (ArtworkDiagnosticText is not null)
        {
            ArtworkDiagnosticText.Text = loaded
                ? "✓ Неоновый фон загружен"
                : "Фон не загружен — нажми «Выбрать оформление»";
            ArtworkDiagnosticText.Foreground = loaded
                ? new SolidColorBrush(Color.FromRgb(137, 238, 185))
                : new SolidColorBrush(Color.FromRgb(241, 181, 128));
        }

        if (notify)
        {
            string message = loaded
                ? "Новое оформление применено.\nИсточник: " + loadedFrom +
                    "\nОформление сохранено и будет загружаться при следующем запуске."
                : "Не удалось найти подходящие изображения в выбранном архиве.\n" +
                    "Нужны широкие картинки PNG/JPG/BMP.\n\nДиагностика: " + ArtworkLog;
            MessageBox.Show(message, "Solaris — оформление",
                MessageBoxButton.OK, loaded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        if (!loaded) report.AppendLine("RESULT: no external artwork loaded; embedded fallback remains.");
        WriteArtworkLog(report.ToString());
        return loaded;
    }

    private static IEnumerable<string> FindArtworkCandidates()
    {
        yield return CachedNeonArchive;
        string root = AppContext.BaseDirectory;
        string? parent = Directory.GetParent(root)?.FullName;
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (string? folder in new[] { root, parent, Path.Combine(root, "Assets"), downloads, desktop })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "Solaris-Neon-UI*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc))
                yield return file;
            foreach (string file in Directory.EnumerateFiles(folder, "Solaris-Neon-UI*.png", SearchOption.TopDirectoryOnly))
                yield return file;
        }
    }

    private static List<ArtworkImage> ReadArtwork(string source, StringBuilder report)
    {
        var images = new List<ArtworkImage>();
        if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (IsSupportedImage(source) && new FileInfo(source).Length <= 30_000_000)
                TryDecodeImage(Path.GetFileName(source), File.ReadAllBytes(source), images, report);
            return images;
        }

        using ZipArchive archive = ZipFile.OpenRead(source);
        int attempted = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!IsSupportedImage(entry.Name)) continue;
            if (entry.Length < 10_000 || entry.Length > 30_000_000) continue;
            if (++attempted > 60) break;
            try
            {
                using Stream input = entry.Open();
                using var output = new MemoryStream();
                byte[] buffer = new byte[32_768];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + count > 30_000_000) throw new InvalidDataException("Image too large");
                    output.Write(buffer, 0, count);
                }
                TryDecodeImage(entry.FullName, output.ToArray(), images, report);
            }
            catch (Exception ex) { report.AppendLine("Skipped " + entry.FullName + ": " + ex.Message); }
        }
        report.AppendLine($"Images decoded: {images.Count}; image entries found: {attempted}.");
        return images;
    }

    private static void TryDecodeImage(string name, byte[] data, List<ArtworkImage> images, StringBuilder report)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1600;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            var item = new ArtworkImage(name.ToLowerInvariant(), bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
            images.Add(item);
            report.AppendLine($"Decoded: {name} [{bitmap.PixelWidth}x{bitmap.PixelHeight}]");
        }
        catch (Exception ex) { report.AppendLine("Not a readable Windows image: " + name + " " + ex.Message); }
    }

    private static ArtworkImage? Pick(IEnumerable<ArtworkImage> items, params string[] keywords)
        => items.FirstOrDefault(x => keywords.Any(k => x.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));

    private static bool IsSupportedImage(string name)
    {
        string ext = Path.GetExtension(name);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteArtworkLog(string details)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArtworkLog)!);
            File.AppendAllText(ArtworkLog, details + Environment.NewLine);
        }
        catch { }
    }

    private void ImportNeonArtwork_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Solaris — выбери архив с фонами",
            Filter = "Solaris Neon UI (*.zip)|*.zip|Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            TryLoadNeonArt(dialog.FileName, notify: true);
    }

    private sealed record ArtworkImage(string Name, BitmapImage Bitmap, int Width, int Height);
}
