using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SolarisLauncher;

internal static class SkinAvatarBootstrap
{
    private const string DefaultNickname = "Sony_VegaS";
    private static readonly HttpClient Http = new();

    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMainWindowLoaded));
    }

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
            return;

        Grid? mainView = FindNamedGrid(window, "MainView");
        if (mainView is null)
            return;

        mainView.IsVisibleChanged -= MainView_IsVisibleChanged;
        mainView.IsVisibleChanged += MainView_IsVisibleChanged;

        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => ApplyAvatar(window)));
    }

    private static void MainView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Grid mainView && e.NewValue is true && mainView.TemplatedParent is null)
        {
            if (Window.GetWindow(mainView) is MainWindow window)
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => ApplyAvatar(window)));
        }
    }

    private static async void ApplyAvatar(MainWindow window)
    {
        try
        {
            RadioButton[] radios = FindVisualChildren<RadioButton>(window).ToArray();
            RadioButton? skinRadio = radios.FirstOrDefault(r => string.Equals(r.Content?.ToString(), "Голова скина", StringComparison.Ordinal));
            RadioButton? solarisRadio = radios.FirstOrDefault(r => string.Equals(r.Content?.ToString(), "Аватар Solaris", StringComparison.Ordinal));

            if (solarisRadio is not null)
            {
                solarisRadio.IsChecked = false;
                solarisRadio.Visibility = Visibility.Collapsed;
                solarisRadio.IsHitTestVisible = false;
            }

            if (skinRadio is null)
                return;

            skinRadio.IsChecked = true;
            skinRadio.IsHitTestVisible = false;
            skinRadio.Focusable = false;
            skinRadio.Cursor = System.Windows.Input.Cursors.Arrow;

            Border? avatarBorder = FindVisualChildren<Border>(skinRadio)
                .FirstOrDefault(b => Math.Abs(b.Width - 54) < 0.1 && Math.Abs(b.Height - 54) < 0.1);

            if (avatarBorder is null)
                return;

            string nickname = window.WelcomeText.Text.Trim();
            if (string.IsNullOrWhiteSpace(nickname))
                nickname = DefaultNickname;

            string encodedNickname = Uri.EscapeDataString(nickname);
            string avatarUrl = $"https://mc-heads.net/avatar/{encodedNickname}/64.png";

            byte[] imageBytes = await Http.GetByteArrayAsync(avatarUrl);
            BitmapImage bitmap = new();
            using (var stream = new System.IO.MemoryStream(imageBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            Image image = new()
            {
                Width = 54,
                Height = 54,
                Source = bitmap,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true,
                ToolTip = $"Скин игрока {nickname}"
            };

            image.Clip = new EllipseGeometry(new Rect(0, 0, 54, 54));
            avatarBorder.Child = image;
        }
        catch
        {
            // Если сервис скинов временно недоступен, оставляем стандартную иконку Solaris.
        }
    }

    private static Grid? FindNamedGrid(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
            return root as Grid;

        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyChild)
            {
                Grid? result = FindNamedGrid(dependencyChild, name);
                if (result is not null)
                    return result;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
