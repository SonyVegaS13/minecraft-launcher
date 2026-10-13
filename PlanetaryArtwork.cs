using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SolarisLauncher;

/// <summary>
/// Optional approved layers for the native WPF scene. Missing layers keep their
/// vector fallback. WebP is converted at build preparation time, never at runtime.
/// </summary>
internal static class PlanetaryArtwork
{
    private static readonly IReadOnlyDictionary<string, int> DecodeLimits =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["space-background.png"] = 1600,
            ["bridge-foreground.png"] = 1600,
            ["operator-body.png"] = 512,
            ["operator-head.png"] = 256,
            ["vanilla-surface.png"] = 512,
            ["vanilla-atmosphere.png"] = 512,
            ["modded-surface.png"] = 512,
            ["modded-atmosphere.png"] = 512,
            ["modded-rings-back.png"] = 512,
            ["modded-rings-front.png"] = 512
        };

    // A frozen source may safely be shared by multiple scene instances. Cache
    // missing optional resources as well; embedded resources cannot appear later.
    private static readonly ConcurrentDictionary<string, Lazy<ImageSource?>> Cache = new();

    internal static ImageSource? TryLoad(string filename)
    {
        if (!DecodeLimits.TryGetValue(filename, out var maximumSide)) return null;
        return Cache.GetOrAdd(filename, name => new Lazy<ImageSource?>(
            () => LoadOptional(name, maximumSide))).Value;
    }

    private static ImageSource? LoadOptional(string filename, int maximumSide)
    {
        foreach (var folder in new[] { "Prepared/", "" })
        {
            try
            {
                var uri = new Uri("pack://application:,,,/SolarisLauncher;component/Assets/Planetary/" + folder + filename,
                    UriKind.Absolute);
                var resource = Application.GetResourceStream(uri);
                if (resource is null) continue;
                using var stream = resource.Stream;

                // Inspect the original aspect ratio before setting just one
                // decode dimension; setting both would distort non-square art.
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                if (decoder.Frames.Count != 1) continue;
                var frame = decoder.Frames[0];
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                stream.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, maximumSide);
                else bitmap.DecodePixelHeight = Math.Min(height, maximumSide);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception error) when (error is IOException or NotSupportedException or
                                          FormatException or InvalidOperationException)
            {
                // Optional missing pack resources and unavailable image codecs
                // must not prevent launcher startup. Supplied art is validated
                // strictly by prepare_planetary_art.py before preview builds.
            }
        }
        return null;
    }
}
