using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace SolarisLauncher;

/// <summary>
/// Non-modal, low-overhead launch artwork shown only while Minecraft is starting.
/// This is a launcher window, not a modification of Minecraft's own loading screen.
/// </summary>
internal sealed class SolarisMinecraftLaunchSplash : Window
{
    private readonly TextBlock _phase;

    public SolarisMinecraftLaunchSplash(Window owner, string mode)
    {
        Owner = owner;
        Title = "SOLARIS — запуск Minecraft";
        Width = 1120;
        Height = 630;
        MinWidth = Width;
        MinHeight = Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = new SolidColorBrush(Color.FromRgb(7, 11, 24));

        var root = new Grid { ClipToBounds = true };
        Content = root;

        // Use the approved original still artwork. If it has not yet been
        // checked into a build, show the existing trusted Solaris artwork.
        ImageSource artwork;
        try
        {
            artwork = new BitmapImage(new Uri(
                "pack://application:,,,/Assets/SolarisCosmicIgnition.png", UriKind.Absolute));
        }
        catch (Exception)
        {
            artwork = new BitmapImage(new Uri(
                "pack://application:,,,/Assets/SolarisEmber.bmp", UriKind.Absolute));
        }

        root.Children.Add(new Image
        {
            Source = artwork,
            Stretch = Stretch.UniformToFill,
            CacheMode = new BitmapCache()
        });

        // Restrict the UI to the lower edge; never obscure or animate the
        // SOLARIS wordmark and the black holes in the original picture.
        var footer = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 91,
            Padding = new Thickness(28, 16, 28, 14),
            Background = new LinearGradientBrush(
                Color.FromArgb(0x08, 0x04, 0x09, 0x1A),
                Color.FromArgb(0xF1, 0x04, 0x09, 0x1A),
                new Point(0, 0), new Point(0, 1))
        };
        root.Children.Add(footer);
        var stack = new StackPanel();
        footer.Child = stack;

        var title = new TextBlock
        {
            Text = mode.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
                ? "SOLARIS VANILLA  •  ЗАПУСК MINECRAFT"
                : "SOLARIS MODDED  •  ЗАПУСК MINECRAFT",
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 5, ShadowDepth = 1, Opacity = 0.75
            }
        };
        stack.Children.Add(title);

        _phase = new TextBlock
        {
            Text = "Подготавливаем игру...",
            Foreground = new SolidColorBrush(Color.FromRgb(215, 226, 244)),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 9)
        };
        stack.Children.Add(_phase);

        stack.Children.Add(new ProgressBar
        {
            IsIndeterminate = true,
            Height = 3,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 161, 77))
        });
    }

    public void SetPhase(string description) => _phase.Text = description;

    /// <summary>
    /// Wait until the Java game window exists or a bounded timeout elapses.
    /// No fake percentage and no blocking the WPF dispatcher.
    /// </summary>
    public async Task WaitForGameWindowAsync(Process process)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(12))
        {
            try
            {
                if (process.HasExited) break;
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    await Task.Delay(450);
                    return;
                }
            }
            catch (InvalidOperationException) { break; }
            await Task.Delay(350);
        }
    }
}
