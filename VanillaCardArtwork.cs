using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SolarisLauncher;

public partial class MainWindow
{
    internal void AddVanillaServerArtwork()
    {
        try
        {
            TextBlock? title = FindTextBlock(MainView, "SOLARIS VANILLA");
            if (title is null) return;

            DependencyObject? current = title;
            Border? card = null;
            Grid? cardGrid = null;
            while (current is not null)
            {
                if (current is Border border && border.Child is Grid grid)
                {
                    card = border;
                    cardGrid = grid;
                    break;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }

            if (card is null || cardGrid is null || cardGrid.Children.Count < 2) return;
            if (cardGrid.Children[0] is Image) return;

            var image = new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/vanilla-server.jpg", UriKind.Absolute)),
                Stretch = Stretch.UniformToFill,
                Opacity = 0.72,
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(image, 2);

            var overlay = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(155, 5, 8, 14)),
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(overlay, 2);

            cardGrid.Children.Insert(0, image);
            cardGrid.Children.Insert(1, overlay);
            card.Background = new SolidColorBrush(Color.FromRgb(10, 13, 20));
        }
        catch
        {
            // Artwork is decorative; never block the launcher if it cannot load.
        }
    }
}
