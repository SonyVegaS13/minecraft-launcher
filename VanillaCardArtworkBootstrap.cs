using System.Runtime.CompilerServices;
using System.Windows;

namespace SolarisLauncher;

internal static class VanillaCardArtworkBootstrap
{
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
        if (sender is MainWindow window)
            window.Dispatcher.BeginInvoke(new Action(window.AddVanillaServerArtwork));
    }
}
