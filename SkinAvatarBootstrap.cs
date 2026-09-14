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
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnMainWindowLoaded));
    }

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        Grid? mainView = FindNamedGrid(window, "MainView");
        if (mainView is null) return;
        mainView.IsVisibleChanged -= MainView_IsVisibleChanged;
        mainView.IsVisibleChanged += MainView_IsVisibleChanged;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ApplyAvatar(window)));
    }

    private static void MainView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Grid mainView && e.NewValue is true && mainView.TemplatedParent is null && Window.GetWindow(mainView) is MainWindow window)
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ApplyAvatar(window)));
    }

    private static async void ApplyAvatar(MainWindow window)
    {
        try
        {
            RadioButton[] radios = FindVisualChildren<RadioButton>(window).ToArray();
            RadioButton? skinRadio = radios.FirstOrDefault(r => string.Equals(r.Content?.ToString(), "Голова скина", StringComparison.Ordinal));
            RadioButton? solarisRadio = radios.FirstOrDefault(r => string.Equals(r.Content?.ToString(), "Аватар Solaris", StringComparison.Ordinal));
            if (skinRadio is not null)
            {
                DependencyObject? avatarSection = skinRadio;
                while (avatarSection is not null && avatarSection is not Border) avatarSection = VisualTreeHelper.GetParent(avatarSection);
                if (avatarSection is Border sectionBorder && VisualTreeHelper.GetParent(sectionBorder) is StackPanel) sectionBorder.Visibility = Visibility.Collapsed;
                else skinRadio.Visibility = Visibility.Collapsed;
            }
            if (solarisRadio is not null)
            {
                solarisRadio.IsChecked = false;
                solarisRadio.Visibility = Visibility.Collapsed;
                solarisRadio.IsHitTestVisible = false;
            }
            TextBlock? profileTitle = FindVisualChildren<TextBlock>(window).FirstOrDefault(t => string.Equals(t.Text, "ПРОФИЛЬ", StringComparison.Ordinal));
            if (profileTitle is null) return;
            Border? profileBorder = FindParent<Border>(profileTitle);
            if (profileBorder is null) return;
            Border? profileAvatar = FindVisualChildren<Border>(profileBorder).FirstOrDefault(b => Math.Abs(b.Width - 66) < 0.1 && Math.Abs(b.Height - 66) < 0.1);
            if (profileAvatar is null) return;
            string nickname = window.WelcomeText.Text.Trim();
            if (string.IsNullOrWhiteSpace(nickname)) nickname = DefaultNickname;
            byte[] imageBytes = await Http.GetByteArrayAsync($"https://mc-heads.net/avatar/{Uri.EscapeDataString(nickname)}/64.png");
            BitmapImage bitmap = new();
            using (var stream = new System.IO.MemoryStream(imageBytes))
            {
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            }
            const double skinSize = 58;
            Image image = new()
            {
                Width = skinSize, Height = skinSize, Source = bitmap, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true, ToolTip = $"Скин игрока {nickname}"
            };
            image.Clip = new EllipseGeometry(new Rect(0, 0, skinSize, skinSize));
            profileAvatar.Child = image;
        }
        catch { }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match) return match;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static Grid? FindNamedGrid(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal)) return root as Grid;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependencyChild)
            {
                Grid? result = FindNamedGrid(dependencyChild, name);
                if (result is not null) return result;
            }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
