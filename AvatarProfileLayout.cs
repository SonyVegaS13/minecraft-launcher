using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow
{
    private static readonly bool _avatarProfileLayoutHook = RegisterAvatarProfileLayoutHook();

    private static bool RegisterAvatarProfileLayoutHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MoveAvatarPickerIntoProfile));
        return true;
    }

    private static void MoveAvatarPickerIntoProfile(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window.MainView is null) return;

        TextBlock? avatarTitle = FindAvatarTextBlockByText(window.MainView, "АВАТАР");
        TextBlock? profileTitle = FindAvatarTextBlockByText(window.MainView, "ПРОФИЛЬ");
        if (avatarTitle is null || profileTitle is null) return;

        Border? avatarCard = FindAvatarAncestor<Border>(avatarTitle);
        Border? profileCard = FindAvatarAncestor<Border>(profileTitle);
        if (avatarCard is null || profileCard is null) return;

        StackPanel? avatarContent = FindAvatarDescendant<StackPanel>(avatarCard);
        StackPanel? profileContent = FindAvatarDescendant<StackPanel>(profileCard);
        if (avatarContent is null || profileContent is null) return;

        RadioButton? avatarPicker = avatarContent.Children
            .OfType<RadioButton>()
            .FirstOrDefault(r => string.Equals(r.Content?.ToString(), "Голова скина", StringComparison.Ordinal));

        if (avatarPicker is null) return;

        avatarContent.Children.Remove(avatarPicker);
        avatarCard.Visibility = Visibility.Collapsed;

        if (!profileContent.Children.OfType<TextBlock>().Any(x => x.Text == "АВАТАР"))
        {
            profileContent.Children.Add(new TextBlock
            {
                Text = "АВАТАР",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(115, 124, 141)),
                Margin = new Thickness(0, 16, 0, 8)
            });
        }

        avatarPicker.Margin = new Thickness(0, 0, 0, 0);
        profileContent.Children.Add(avatarPicker);
    }

    private static TextBlock? FindAvatarTextBlockByText(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock && string.Equals(textBlock.Text, text, StringComparison.Ordinal))
                return textBlock;

            if (child is DependencyObject dependencyChild)
            {
                TextBlock? result = FindAvatarTextBlockByText(dependencyChild, text);
                if (result is not null) return result;
            }
        }

        return null;
    }

    private static T? FindAvatarAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = LogicalTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is T match) return match;
            current = LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindAvatarDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match) return match;

            if (child is DependencyObject dependencyChild)
            {
                T? result = FindAvatarDescendant<T>(dependencyChild);
                if (result is not null) return result;
            }
        }

        return null;
    }
}
