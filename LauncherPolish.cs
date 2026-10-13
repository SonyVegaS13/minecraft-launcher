using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SolarisLauncher;

public partial class MainWindow
{
    private bool _uiPolished;

    // Server availability now belongs to the relevant card:
    // Modded has live polling; Vanilla is not hosted and its offline label is in XAML.
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_uiPolished)
            Dispatcher.BeginInvoke(new Action(PolishMainView), DispatcherPriority.Loaded);
    }

    private void PolishMainView()
    {
        if (_uiPolished || MainView is null) return;
        _uiPolished = true;

        // Make the lower content breathe instead of leaving a large dead area.
        TextBlock? newsTitle = FindTextBlockByText(MainView, "📰  НОВОСТИ SOLARIS");
        if (newsTitle is not null && FindAncestor<Border>(newsTitle) is Border newsCard)
            newsCard.MinHeight = 150;

        TextBlock? quickTitle = FindTextBlockByText(MainView, "⚡  БЫСТРЫЙ СТАТУС");
        if (quickTitle is not null && FindAncestor<Border>(quickTitle) is Border quickCard)
            quickCard.MinHeight = 150;

        if (Progress is not null)
        {
            Progress.Height = 5;
            Progress.Foreground = new SolidColorBrush(Color.FromRgb(139, 92, 246));
            Progress.Background = new SolidColorBrush(Color.FromRgb(28, 33, 45));
        }

        // Keep the existing Minecraft skin-head avatar untouched.
        TextBlock? profileName = FindTextBlockByText(MainView, "Игрок Solaris");
        if (profileName is not null && !string.IsNullOrWhiteSpace(WelcomeText.Text))
            profileName.Text = WelcomeText.Text;

        // Achievements removed: SOLARIS ACTIVITY is now the account dashboard.
    }

    private void PolishAchievements()
    {
        TextBlock? achievementTitle = FindTextBlockByText(MainView, "🏆  ДОСТИЖЕНИЯ");
        if (achievementTitle is null) achievementTitle = FindTextBlockByText(MainView, "🏆 ДОСТИЖЕНИЯ");
        if (achievementTitle is null) return;

        Border? card = FindAncestor<Border>(achievementTitle);
        StackPanel? content = card is null ? null : FindDescendant<StackPanel>(card);
        if (content is null) return;

        TextBlock? counter = FindTextBlockContaining(content, "/ 3");
        if (counter is not null) counter.Text = "2 / 4";

        SetAchievement(content, "Добро пожаловать", "✓ Добро пожаловать — аккаунт создан", true);
        SetAchievement(content, "Первый запуск", "✓ Первый запуск — запусти Minecraft", true);
        SetAchievement(content, "Исследователь", "□ Исследователь — попробуй Vanilla и Modded", false);

        if (!content.Children.OfType<TextBlock>().Any(x => x.Text.StartsWith("□ Покоритель мира", StringComparison.Ordinal)))
        {
            content.Children.Add(new TextBlock
            {
                Text = "□ Покоритель мира — исследуй Solaris",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(111, 122, 140)),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
    }

    private static void SetAchievement(StackPanel content, string name, string text, bool unlocked)
    {
        TextBlock? item = content.Children.OfType<TextBlock>().FirstOrDefault(x => x.Text.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        item.Text = text;
        item.Foreground = new SolidColorBrush(unlocked ? Color.FromRgb(134, 239, 172) : Color.FromRgb(111, 122, 140));
    }

    private static TextBlock? FindTextBlockByText(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock && string.Equals(textBlock.Text, text, StringComparison.Ordinal)) return textBlock;
            if (child is DependencyObject dependencyChild) { TextBlock? result = FindTextBlockByText(dependencyChild, text); if (result is not null) return result; }
        }
        return null;
    }

    private static TextBlock? FindTextBlockContaining(DependencyObject root, string text)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock textBlock && textBlock.Text.Contains(text, StringComparison.Ordinal)) return textBlock;
            if (child is DependencyObject dependencyChild) { TextBlock? result = FindTextBlockContaining(dependencyChild, text); if (result is not null) return result; }
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = LogicalTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is T match) return match;
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match) return match;
            if (child is DependencyObject dependencyChild) { T? result = FindDescendant<T>(dependencyChild); if (result is not null) return result; }
        }
        return null;
    }

}
