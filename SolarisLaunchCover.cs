using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SolarisLauncher;

public partial class MainWindow
{
    // A launcher-owned cover is necessary for pure Vanilla, which cannot load
    // Forge's client-side early splash mod. This is a COVER, not a modification
    // of the closed Mojang bootstrap renderer itself.
    private void StartMinecraftWithSolarisCover(Process process, string gameDirectory)
    {
        Window? cover = null;
        try
        {
            cover = CreateSolarisLaunchCover();
            cover.Show();
        }
        catch (Exception ex)
        {
            TraceStartup("Solaris cosmetic launch cover unavailable: " + ex.Message);
            try { cover?.Close(); } catch { }
            cover = null;
        }

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить процесс Minecraft.");
        }
        catch
        {
            try { cover?.Close(); } catch { }
            throw;
        }

        if (cover is not null)
            _ = MonitorSolarisLaunchCoverAsync(cover, process,
                Path.Combine(gameDirectory, "logs", "latest.log"), DateTime.UtcNow);
    }

    private Window CreateSolarisLaunchCover()
    {
        var window = new Window
        {
            Title = "SOLARIS — запуск Minecraft",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowState = WindowState.Maximized,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        var layer = new Grid();
        string selected = Path.Combine(_stateDir, "solaris-loading.png");
        ImageSource imageSource;
        if (File.Exists(selected))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(selected), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            imageSource = bitmap;
        }
        else
        {
            imageSource = new BitmapImage(
                new Uri("pack://application:,,,/Assets/SolarisCosmicHome.jpg", UriKind.Absolute));
        }

        layer.Children.Add(new Image
        {
            Source = imageSource,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        });

        var footer = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 54)
        };
        footer.Children.Add(new TextBlock
        {
            Text = "ПОДГОТОВКА MINECRAFT",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 1, BlurRadius = 10, Opacity = 0.95
            }
        });
        var bar = new ProgressBar
        {
            Width = 320,
            Height = 5,
            IsIndeterminate = true,
            Foreground = Brushes.DeepSkyBlue,
            Background = Brushes.Black,
            BorderThickness = new Thickness(0)
        };
        footer.Children.Add(bar);
        layer.Children.Add(footer);

        var close = new Button
        {
            Content = "Пропустить ✕",
            Background = new SolidColorBrush(Color.FromArgb(205, 12, 18, 30)),
            Foreground = Brushes.White,
            BorderBrush = Brushes.SlateGray,
            FontSize = 13,
            Padding = new Thickness(15, 7, 15, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(20)
        };
        close.Click += (_, _) => window.Close();
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        layer.Children.Add(close);
        window.Content = layer;
        return window;
    }

    private async Task MonitorSolarisLaunchCoverAsync(
        Window cover, Process game, string logPath, DateTime startUtc)
    {
        try
        {
            // Covers earliest Mojang/Forge frames. Exit on actual Minecraft
            // initialization, game exit, user dismissal or safety timeout.
            for (int n = 0; n < 160; n++)
            {
                if (!cover.IsVisible || game.HasExited) break;
                await Task.Delay(750);
                try
                {
                    if (!File.Exists(logPath) ||
                        File.GetLastWriteTimeUtc(logPath) < startUtc.AddSeconds(-1)) continue;

                    var info = new FileInfo(logPath);
                    if (info.Length <= 0 || info.Length > 12_000_000) continue;

                    string log = await File.ReadAllTextAsync(logPath);
                    if (log.Contains("Sound engine started", StringComparison.OrdinalIgnoreCase) ||
                        log.Contains("OpenAL initialized", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(2000);
                        break;
                    }
                }
                catch (IOException) { /* Game owns latest.log during startup. */ }
                catch (UnauthorizedAccessException) { /* Fall back to timeout. */ }
            }
        }
        catch (Exception ex)
        {
            TraceStartup("Solaris launch cover monitor: " + ex.Message);
        }
        finally
        {
            try { if (cover.IsVisible) cover.Close(); }
            catch { /* Never disrupt the Minecraft process. */ }
        }
    }
}
