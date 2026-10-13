using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SolarisLauncher;

public partial class MainWindow
{
    private void InitializePlanetaryHome()
    {
        // WPF cannot compile names in inline properties of a named UserControl.
        // Keep each action registered in the window's namescope and detach its
        // previous visual parent before the scene's presenter adopts it.
        PlanetaryDetailsStorage.Children.Remove(VanillaPlanetDetails);
        PlanetaryDetailsStorage.Children.Remove(ModdedPlanetDetails);
        PlanetaryScene.VanillaDetails = VanillaPlanetDetails;
        PlanetaryScene.ModdedDetails = ModdedPlanetDetails;
        PlanetaryScene.SetBinding(PlanetaryHome.ModdedStatusProperty,
            new Binding(nameof(TextBlock.Text)) { Source = ModdedStatusText });

        // Existing overlay handlers keep MainView visible behind their screens.
        // Explicit Sources avoid crossing the UserControl's separate namescope.
        var style = new Style(typeof(PlanetaryHome));
        foreach (var overlay in new FrameworkElement[] { SettingsView, ProfileView, ControlView, AboutOverlay })
        {
            var trigger = new DataTrigger
            {
                Binding = new Binding(nameof(Visibility)) { Source = overlay },
                Value = Visibility.Visible
            };
            trigger.Setters.Add(new Setter(VisibilityProperty, Visibility.Hidden));
            style.Triggers.Add(trigger);
        }
        PlanetaryScene.Style = style;
    }

    private void PlanetaryWorlds_Click(object sender, RoutedEventArgs e)
        => PlanetaryScene.ResetSelection();

    private void PlanetaryCancel_Click(object sender, RoutedEventArgs e)
    {
        // Route through the original Button instance. Both existing launch
        // handlers use its identity to cancel their current operation.
        if (_launchCancellation is not null && _activeLaunchButton is { IsEnabled: true } button)
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
}
