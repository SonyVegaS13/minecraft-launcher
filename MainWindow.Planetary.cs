using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;

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
        PlanetaryScene.SetBinding(PlanetaryHome.ModdedPresenceProperty,
            new Binding(nameof(TextBlock.Text)) { Source = ModdedStatusText, Converter = new PlanetaryStatusConverter(false) });
        var playersBinding = new Binding(nameof(TextBlock.Text))
        {
            Source = ModdedStatusText, Converter = new PlanetaryStatusConverter(true)
        };
        PlanetaryScene.SetBinding(PlanetaryHome.ModdedPlayersProperty, playersBinding);
        ModdedPlanetPlayersText.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text))
        {
            Source = ModdedStatusText, Converter = new PlanetaryStatusConverter(true)
        });
        PlanetaryScene.SetBinding(PlanetaryHome.ModdedStatusBrushProperty,
            new Binding(nameof(System.Windows.Shapes.Shape.Fill)) { Source = ModdedStatusDot });
        PlanetaryScene.SetBinding(PlanetaryHome.VanillaArtworkProperty,
            new Binding(nameof(Image.Source)) { Source = VanillaArtworkSharp });
        PlanetaryScene.SetBinding(PlanetaryHome.ModdedArtworkProperty,
            new Binding(nameof(Image.Source)) { Source = ModdedArtworkSharp });
        PlanetaryScene.SetBinding(PlanetaryHome.EffectsEnabledProperty,
            new Binding(nameof(CheckBox.IsChecked)) { Source = PlanetaryEffectsCheck, TargetNullValue = false });

        foreach (var auditButton in new[] { VanillaPlanetAuditButton, ModdedPlanetAuditButton })
        {
            auditButton.SetBinding(Button.ContentProperty,
                new Binding(nameof(Button.Content)) { Source = FileCheckButton });
            auditButton.SetBinding(IsEnabledProperty,
                new Binding(nameof(IsEnabled)) { Source = FileCheckButton });
            auditButton.SetBinding(VisibilityProperty,
                new Binding(nameof(Visibility)) { Source = FileCheckButton });
        }

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

    private void PlanetaryAudit_Click(object sender, RoutedEventArgs e)
    {
        if (FileCheckButton.IsEnabled && FileCheckButton.Visibility == Visibility.Visible)
            FileCheckButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, FileCheckButton));
    }

    private void PlanetaryCancel_Click(object sender, RoutedEventArgs e)
    {
        // Route through the original Button instance. Both existing launch
        // handlers use its identity to cancel their current operation.
        if (_launchCancellation is not null && _activeLaunchButton is { IsEnabled: true } button)
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private sealed class PlanetaryStatusConverter(bool players) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => players ? PlanetaryServerSummary.Players(value as string, ModdedServerSlots)
                : PlanetaryServerSummary.Presence(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
