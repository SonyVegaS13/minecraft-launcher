using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(DisableUnusedAvatarPicker));
    }

    private static void DisableUnusedAvatarPicker(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;

        foreach (RadioButton radio in FindRadioButtons(window))
        {
            string label = radio.Content?.ToString() ?? string.Empty;
            if (string.Equals(label, "Аватар Solaris", StringComparison.Ordinal))
            {
                radio.Visibility = Visibility.Collapsed;
            }
            else if (string.Equals(label, "Голова скина", StringComparison.Ordinal))
            {
                radio.IsHitTestVisible = false;
                radio.Focusable = false;
                radio.IsChecked = true;
            }
        }
    }

    private static IEnumerable<RadioButton> FindRadioButtons(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is RadioButton radio)
                yield return radio;

            if (child is DependencyObject dependencyChild)
            {
                foreach (RadioButton nested in FindRadioButtons(dependencyChild))
                    yield return nested;
            }
        }
    }
}
