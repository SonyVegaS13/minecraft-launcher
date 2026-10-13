using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SolarisLauncher;

namespace PlanetaryWpf.Tests;

internal static class Program
{
    // This harness never creates SolarisLauncher.App or MainWindow. A generic WPF
    // Application/Window loads the real control without account, update, Minecraft,
    // settings, or installed-launcher side effects. Fixture statuses are test inputs.
    [STAThread]
    private static int Main(string[] args)
    {
        string? screenshotDirectory = null;
        if (args.Length == 2 && args[0] == "--screenshots")
            screenshotDirectory = Path.GetFullPath(args[1]);
        else if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: PlanetaryWpf.Tests [--screenshots output-directory]");
            return 2;
        }
        // The reusable scene resolves its resources through assembly-qualified
        // pack URIs; WPF disallows changing ResourceAssembly in a managed EXE.
        // Real cursor hit tests must not interfere with synthetic routed events.
        // Dispose restores the prior cursor position even if a fixture fails.
        using var cursor = new CursorScope();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var cases = new (string Name, Action<Fixture> Run)[]
        {
            ("Real PlanetaryHome XAML loads with both planet buttons and no pinned card", fixture =>
            {
                Overview(fixture);
                Equal(WorldSelection.None, fixture.Home.SelectedWorld);
                Equal(Visibility.Collapsed, fixture.Find<Border>("DetailsPanel").Visibility);
                Equal(Visibility.Collapsed, fixture.Find<Border>("HoverCard").Visibility);
            }),
            ("Routed planet clicks pin and switch content without hiding the other planet", fixture =>
            {
                Click(fixture.Vanilla);
                Equal(WorldSelection.Vanilla, fixture.Home.SelectedWorld);
                Equal(Visibility.Visible, fixture.Find<ContentPresenter>("VanillaDetailsPresenter").Visibility);
                Equal(Visibility.Collapsed, fixture.Find<ContentPresenter>("ModdedDetailsPresenter").Visibility);
                Overview(fixture);
                Click(fixture.Modded);
                Equal(WorldSelection.Modded, fixture.Home.SelectedWorld);
                Equal(Visibility.Collapsed, fixture.Find<ContentPresenter>("VanillaDetailsPresenter").Visibility);
                Equal(Visibility.Visible, fixture.Find<ContentPresenter>("ModdedDetailsPresenter").Visibility);
                Overview(fixture);
            }),
            ("Close and routed Escape dismiss pinned cards", fixture =>
            {
                Click(fixture.Vanilla);
                Click(fixture.Find<Button>("CloseCardButton"));
                Equal(WorldSelection.None, fixture.Home.SelectedWorld);
                Equal(Visibility.Collapsed, fixture.Find<Border>("DetailsPanel").Visibility);
                Click(fixture.Modded);
                var source = PresentationSource.FromVisual(fixture.Home)
                    ?? throw new InvalidOperationException("Shown test control has no presentation source.");
                fixture.Home.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source,
                    Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump();
                Equal(WorldSelection.None, fixture.Home.SelectedWorld);
                Equal(Visibility.Collapsed, fixture.Find<Border>("DetailsPanel").Visibility);
                Overview(fixture);
            }),
            ("Synthetic planet to hover-card events preserve the actual card through leave grace", fixture =>
            {
                Pointer(fixture.Vanilla, Mouse.MouseEnterEvent);
                Equal(Visibility.Visible, fixture.Find<Border>("HoverCard").Visibility);
                Equal(WorldSelection.Vanilla, fixture.State.VisibleCardWorld);
                Pointer(fixture.Vanilla, Mouse.MouseLeaveEvent);
                Pointer(fixture.Find<Border>("HoverCard"), Mouse.MouseEnterEvent);
                PumpFor(TimeSpan.FromMilliseconds(300));
                Equal(Visibility.Visible, fixture.Find<Border>("HoverCard").Visibility);
                Equal(WorldSelection.Vanilla, fixture.State.VisibleCardWorld);
                Pointer(fixture.Find<Border>("HoverCard"), Mouse.MouseLeaveEvent);
                PumpFor(TimeSpan.FromMilliseconds(500));
                Equal(WorldSelection.None, fixture.State.VisibleCardWorld);
                Equal(Visibility.Collapsed, fixture.Find<Border>("HoverCard").Visibility);
            }),
            ("Pinned content survives pointer exit and another planet hover", fixture =>
            {
                Pointer(fixture.Vanilla, Mouse.MouseEnterEvent);
                Click(fixture.Vanilla);
                Pointer(fixture.Vanilla, Mouse.MouseLeaveEvent);
                Pointer(fixture.Modded, Mouse.MouseEnterEvent);
                Equal(WorldSelection.Modded, fixture.State.HoveredWorld);
                Equal(WorldSelection.Vanilla, fixture.Home.SelectedWorld);
                Pointer(fixture.Modded, Mouse.MouseLeaveEvent);
                PumpUntil(() => fixture.State.HoveredWorld == WorldSelection.None,
                    "The real leave timer did not clear the unpinned hover while preserving the pin.");
                Equal(WorldSelection.None, fixture.State.HoveredWorld);
                Equal(WorldSelection.Vanilla, fixture.Home.SelectedWorld);
                Equal(Visibility.Visible, fixture.Find<Border>("DetailsPanel").Visibility);
                Overview(fixture);
            }),
            ("Dependency-property status projections reach planet and hover labels", fixture =>
            {
                fixture.Status(" Сервер онлайн · игроков: 37/200", Brushes.LimeGreen);
                AssertVisibleText(fixture.Find<StackPanel>("ModdedLabel"), "Сервер онлайн");
                AssertVisibleText(fixture.Find<StackPanel>("ModdedLabel"), "Игроки: 37 / 200");
                Pointer(fixture.Modded, Mouse.MouseEnterEvent);
                AssertVisibleText(fixture.Find<StackPanel>("ModdedSummary"), "Игроки: 37 / 200");
                var onlineColor = SummaryIndicatorColor(fixture);
                fixture.Status(" Сервер недоступен", Brushes.IndianRed);
                AssertVisibleText(fixture.Find<StackPanel>("ModdedLabel"), "Сервер недоступен");
                AssertVisibleText(fixture.Find<StackPanel>("ModdedSummary"), "Игроки: — / 200");
                var unavailableColor = SummaryIndicatorColor(fixture);
                fixture.Status(" Проверяем сервер...", Brushes.Goldenrod);
                AssertVisibleText(fixture.Find<StackPanel>("ModdedLabel"), "Проверяем сервер…");
                var checkingColor = SummaryIndicatorColor(fixture);
                Check(onlineColor.G > onlineColor.R, "Online indicator must have a green dominant channel.");
                Check(unavailableColor.R > unavailableColor.G, "Unavailable indicator must have a red dominant channel.");
                Check(checkingColor.R > checkingColor.B && checkingColor.G > checkingColor.B,
                    "Checking indicator must be yellow rather than online/offline.");
            }),
            ("ContentPresenter retains injected launch, update, check and cancel event handlers", fixture =>
            {
                Click(fixture.Vanilla);
                Check(ReferenceEquals(fixture.Home.VanillaDetails,
                    fixture.Find<ContentPresenter>("VanillaDetailsPresenter").Content), "Vanilla actions were replaced.");
                foreach (var action in fixture.VanillaActions) Click(action);
                Click(fixture.Modded);
                Check(ReferenceEquals(fixture.Home.ModdedDetails,
                    fixture.Find<ContentPresenter>("ModdedDetailsPresenter").Content), "Modded actions were replaced.");
                foreach (var action in fixture.ModdedActions) Click(action);
                Equal(8, fixture.ActionCalls);
            }),
            ("Card and both planets fit without overlap at multiple window sizes", fixture =>
            {
                foreach (var size in new[] { new Size(1440, 820), new Size(1100, 660), new Size(940, 550) })
                {
                    fixture.Window.Width = size.Width;
                    fixture.Window.Height = size.Height;
                    Pump();
                    foreach (var planet in new[] { fixture.Vanilla, fixture.Modded })
                    {
                        fixture.Home.ResetSelection();
                        Pointer(planet, Mouse.MouseEnterEvent);
                        fixture.Home.UpdateLayout();
                        var hover = fixture.Find<Border>("HoverCard");
                        var corridor = fixture.Find<Border>("HoverBridge");
                        Contained(fixture.Home, hover);
                        foreach (var surfaceName in new[] { "VanillaSurface", "ModdedSurface" })
                            Check(!Bounds(fixture.Home, hover).IntersectsWith(Bounds(fixture.Home,
                                fixture.Find<Grid>(surfaceName))), $"Hover card covers {surfaceName} at {size}.");
                        var other = planet == fixture.Vanilla ? fixture.Modded : fixture.Vanilla;
                        Check(Bounds(fixture.Home, corridor).IntersectsWith(Bounds(fixture.Home, planet)),
                            "The hover handoff corridor no longer touches its own planet.");
                        Check(!Bounds(fixture.Home, corridor).IntersectsWith(Bounds(fixture.Home, other)),
                            "The hover handoff corridor crosses the other planet.");
                        Click(planet);
                        fixture.Home.UpdateLayout();
                        Overview(fixture);
                        var panel = fixture.Find<Border>("DetailsPanel");
                        Contained(fixture.Home, panel);
                        Contained(fixture.Home, fixture.Vanilla);
                        Contained(fixture.Home, fixture.Modded);
                        Check(!Bounds(fixture.Home, panel).IntersectsWith(Bounds(fixture.Home, fixture.Vanilla)),
                            $"Card overlaps Vanilla at {size}.");
                        Check(!Bounds(fixture.Home, panel).IntersectsWith(Bounds(fixture.Home, fixture.Modded)),
                            $"Card overlaps Modded at {size}.");
                        Contained(panel, fixture.Find<Button>("CloseCardButton"));
                        var actions = planet == fixture.Vanilla ? fixture.VanillaActions : fixture.ModdedActions;
                        foreach (var action in actions) Contained(panel, action);
                    }
                }
            }),
            ("Disabling decorative effects pauses clocks and keeps planet interaction working", fixture =>
            {
                fixture.Home.SetValue(PlanetaryHome.EffectsEnabledProperty, true);
                Pump();
                fixture.Home.SetValue(PlanetaryHome.EffectsEnabledProperty, false);
                Pump();
                Paused(fixture);
                var before = fixture.Find<RotateTransform>("VanillaRotation").Angle;
                PumpFor(TimeSpan.FromMilliseconds(120));
                Equal(before, fixture.Find<RotateTransform>("VanillaRotation").Angle);
                Pointer(fixture.Modded, Mouse.MouseEnterEvent);
                Equal(WorldSelection.Modded, fixture.State.VisibleCardWorld);
                Click(fixture.Modded);
                Equal(WorldSelection.Modded, fixture.Home.SelectedWorld);
            }),
            ("Controlled production hover and card timelines reach endpoints and survive close-reopen races", fixture =>
            {
                // Exercise real one-shot WPF timelines even when CI has a software
                // rendering tier or Windows animations disabled. This fixture-only
                // override never resumes decorative clocks or alters app defaults.
                fixture.Home.GetType().GetField("_allowMotion", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(fixture.Home, true);
                var scale = fixture.Find<ScaleTransform>("VanillaHover");
                var hover = fixture.Find<Border>("HoverCard");
                bool intermediateScale = false, intermediateOpacity = false;
                Near(1, scale.ScaleX);
                Near(0, hover.Opacity);
                SampleFor(TimeSpan.FromMilliseconds(350), () => Pointer(fixture.Vanilla, Mouse.MouseEnterEvent), () =>
                {
                    intermediateScale |= scale.ScaleX is > 1 and < 1.05;
                    intermediateOpacity |= hover.Opacity is > 0 and < 1;
                });
                Check(intermediateScale, "Hover scale jumped to its endpoint without an intermediate sampled animated value.");
                Check(intermediateOpacity, "Hover card jumped to full opacity without an intermediate sampled fade value.");
                Near(1.05, scale.ScaleX);
                Near(1.05, scale.ScaleY);
                Near(1, hover.Opacity);

                fixture.Home.ResetSelection();
                PumpUntil(() => hover.Opacity is > 0 and < 1, "Transient close did not start its fade.");
                Pointer(fixture.Modded, Mouse.MouseEnterEvent);
                PumpFor(TimeSpan.FromMilliseconds(350));
                Equal(WorldSelection.Modded, fixture.State.VisibleCardWorld);
                Equal(Visibility.Visible, hover.Visibility);
                Near(1, hover.Opacity);
                Near(1, scale.ScaleX);

                Click(fixture.Modded);
                var pinned = fixture.Find<Border>("DetailsPanel");
                PumpFor(TimeSpan.FromMilliseconds(350));
                Near(1, pinned.Opacity);
                Click(fixture.Find<Button>("CloseCardButton"));
                PumpUntil(() => pinned.Opacity is > 0 and < 1, "Pinned close did not start its fade.");
                Click(fixture.Vanilla);
                PumpFor(TimeSpan.FromMilliseconds(350));
                Equal(WorldSelection.Vanilla, fixture.Home.SelectedWorld);
                Equal(Visibility.Visible, pinned.Visibility);
                Near(1, pinned.Opacity);
                Overview(fixture);
                Paused(fixture);
            }),
            ("Hidden and unloaded scene clears transient input and pauses clocks while preserving pin", fixture =>
            {
                Pointer(fixture.Vanilla, Mouse.MouseEnterEvent);
                Click(fixture.Vanilla);
                fixture.Home.Visibility = Visibility.Collapsed;
                Pump();
                Equal(WorldSelection.None, fixture.State.HoveredWorld);
                Equal(WorldSelection.Vanilla, fixture.State.PinnedWorld);
                Equal(false, fixture.State.IsActive);
                Paused(fixture);
                fixture.Home.Visibility = Visibility.Visible;
                Pump();
                Equal(WorldSelection.Vanilla, fixture.Home.SelectedWorld);
                fixture.Window.Content = null;
                Pump();
                Equal(false, fixture.Home.IsLoaded);
                Equal(false, fixture.State.IsActive);
                Equal(WorldSelection.Vanilla, fixture.State.PinnedWorld);
                Paused(fixture);
            })
        };

        var failures = 0;
        foreach (var (name, run) in cases)
        {
            try
            {
                using var fixture = new Fixture();
                run(fixture);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                ++failures;
                Console.Error.WriteLine($"FAIL {name}: {error}");
                ReportCiFailure(name, error);
            }
        }
        var total = cases.Length;
        if (screenshotDirectory is not null)
        {
            ++total;
            try
            {
                using var fixture = new Fixture();
                CaptureStates(fixture, screenshotDirectory);
                Console.WriteLine($"PASS Deterministic static-layout screenshots written to {screenshotDirectory}");
            }
            catch (Exception error)
            {
                ++failures;
                Console.Error.WriteLine($"FAIL Deterministic static-layout screenshots: {error}");
                ReportCiFailure("Static-layout screenshots", error);
            }
        }
        application.Shutdown();
        Console.WriteLine($"{total - failures}/{total} isolated WPF control checks passed.");
        Console.WriteLine("These checks do not measure rendered art quality, real mouse hit regions, animation smoothness, or GPU performance.");
        return failures == 0 ? 0 : 1;
    }

    private sealed class Fixture : IDisposable
    {
        public PlanetaryHome Home { get; }
        public Window Window { get; }
        public Button Vanilla => Find<Button>("VanillaPlanet");
        public Button Modded => Find<Button>("ModdedPlanet");
        public Button[] VanillaActions { get; }
        public Button[] ModdedActions { get; }
        public int ActionCalls { get; private set; }
        public PlanetaryInteractionState State => Field<PlanetaryInteractionState>(Home, "_interaction");

        public Fixture()
        {
            Home = new PlanetaryHome();
            Home.SetValue(PlanetaryHome.EffectsEnabledProperty, false);
            VanillaActions = Actions();
            ModdedActions = Actions();
            Home.VanillaDetails = Panel(VanillaActions);
            Home.ModdedDetails = Panel(ModdedActions);
            Window = new Window
            {
                Title = "Solaris isolated planetary control checks",
                Width = 1350,
                Height = 750,
                Content = Home,
                Foreground = Brushes.GhostWhite,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowActivated = true
            };
            CursorScope.Park();
            Window.Show();
            Window.Activate();
            Pump();
            Check(Home.IsLoaded && Home.IsVisible, "Real planetary XAML did not load into the test window.");
            Check(Window.IsActive && State.IsActive,
                "Windows CI could not activate its isolated WPF window; control input checks require an interactive desktop.");
            // Initial OS keyboard/pointer focus is not a deliberate hover in this
            // fixture. Start each scenario from the real control's close action.
            Home.ResetSelection();
            Pump();
        }

        public T Find<T>(string name) where T : class => Home.FindName(name) as T
            ?? throw new InvalidOperationException($"Required WPF element '{name}' ({typeof(T).Name}) was not loaded.");

        public void Status(string pollText, Brush indicator)
        {
            // MainWindow owns the actual poll and supplies these separate DPs.
            // Exercise that public surface without constructing its bootstrap.
            Home.ModdedStatus = pollText;
            Home.ModdedPresence = PlanetaryServerSummary.Presence(pollText);
            Home.ModdedPlayers = PlanetaryServerSummary.Players(pollText);
            Home.ModdedStatusBrush = indicator;
            Pump();
        }

        private Button[] Actions() => new[] { "Играть", "Обновить", "Проверить файлы", "Отмена" }
            .Select(label =>
            {
                var button = new Button { Content = $"{label} (тест)", Height = 30, Margin = new Thickness(0, 3, 0, 3) };
                button.Click += (_, _) => ++ActionCalls;
                return button;
            }).ToArray();

        private static StackPanel Panel(IEnumerable<Button> actions)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "Тестовая панель · игровые действия не выполняются",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 6)
            });
            foreach (var action in actions) panel.Children.Add(action);
            return panel;
        }

        public void Dispose()
        {
            Window.Close();
            Pump();
        }
    }

    private sealed class CursorScope : IDisposable
    {
        private readonly CursorPoint _original;

        public CursorScope()
        {
            Check(GetCursorPos(out _original), "Could not read the Windows test desktop cursor position.");
            Park();
        }

        public static void Park() => Check(SetCursorPos(0, 0),
            "Could not park the test desktop cursor away from the planets.");

        public void Dispose()
        {
            if (!SetCursorPos(_original.X, _original.Y))
                Console.Error.WriteLine("Could not restore the original test desktop cursor position.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint { public int X; public int Y; }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out CursorPoint point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);
    }

    private static void CaptureStates(Fixture fixture, string directory)
    {
        Directory.CreateDirectory(directory);
        fixture.Window.WindowStyle = WindowStyle.None;
        fixture.Window.ResizeMode = ResizeMode.NoResize;
        fixture.Window.Width = 1280;
        fixture.Window.Height = 600;
        fixture.Home.EffectsEnabled = false;
        fixture.Status(" Проверяем сервер...", Brushes.Goldenrod);
        fixture.Home.VanillaArtwork = ApprovedArtwork("SolarisVanillaCard.png");
        fixture.Home.ModdedArtwork = ApprovedArtwork("SolarisModdedCard.png");
        fixture.Home.ResetSelection();
        Pump();
        Capture(fixture, Path.Combine(directory, "overview.png"));
        Pointer(fixture.Vanilla, Mouse.MouseEnterEvent);
        Capture(fixture, Path.Combine(directory, "hover.png"));
        Click(fixture.Vanilla);
        Capture(fixture, Path.Combine(directory, "pinned-vanilla.png"));
        Click(fixture.Modded);
        Capture(fixture, Path.Combine(directory, "pinned-modded.png"));
    }

    private static BitmapImage ApprovedArtwork(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri($"pack://application:,,,/SolarisLauncher;component/Assets/{fileName}", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void Capture(Fixture fixture, string path)
    {
        fixture.Home.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(fixture.Home);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Overview(Fixture fixture)
    {
        foreach (var planet in new[] { fixture.Vanilla, fixture.Modded })
        {
            Check(planet.IsVisible && planet.IsHitTestVisible && planet.IsEnabled,
                "A planet became hidden, disabled, or unavailable to pointer input.");
            double opacity = 1;
            for (DependencyObject? element = planet; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is UIElement ui) opacity *= ui.Opacity;
            Check(opacity > 0.8, "A planet was faded away when a card opened.");
        }
    }

    private static void Paused(Fixture fixture)
    {
        foreach (var clock in Field<AnimationClock[]>(fixture.Home, "_ambientClocks"))
            Check(clock.IsPaused, "A decorative rotation clock is still running.");
        Check(!Field<DispatcherTimer>(fixture.Home, "_pointerTimer").IsEnabled,
            "The pointer animation timer is still running.");
    }

    private static Color SummaryIndicatorColor(Fixture fixture) => Descendants(
        fixture.Find<StackPanel>("ModdedSummary")).OfType<System.Windows.Shapes.Ellipse>()
        .Select(ellipse => ellipse.Fill).OfType<SolidColorBrush>().First().Color;

    private static void AssertVisibleText(DependencyObject root, string expected) => Check(
        Descendants(root).OfType<TextBlock>().Any(text => text.Text == expected && text.IsVisible),
        $"A visible bound label did not display '{expected}'.");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); ++index)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Cannot inspect actual control field '{name}'."));

    private static Rect Bounds(Visual parent, FrameworkElement child) =>
        child.TransformToAncestor(parent).TransformBounds(new Rect(new Point(), child.RenderSize));

    private static void Contained(FrameworkElement parent, FrameworkElement child)
    {
        var available = new Rect(new Point(-1, -1), new Size(parent.ActualWidth + 2, parent.ActualHeight + 2));
        Check(available.Contains(Bounds(parent, child)), $"'{child.Name}' was clipped outside its available layout area.");
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        Pump();
    }

    private static void Pointer(UIElement target, RoutedEvent routedEvent)
    {
        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = routedEvent, Source = target });
        Pump();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Pump();
    }

    private static void SampleFor(TimeSpan duration, Action begin, Action sample)
    {
        // Animated properties are sampled, rather than relying on descriptor
        // notifications. Start before begin's dispatcher pump so even early
        // animation frames are observed on a slower CI software renderer.
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => sample();
        timer.Start();
        try
        {
            begin();
            PumpFor(duration);
            sample();
        }
        finally { timer.Stop(); }
    }

    private static void PumpUntil(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(2))
            PumpFor(TimeSpan.FromMilliseconds(15));
        Check(condition(), message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ReportCiFailure(string name, Exception error)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        // Make the exact test failure readable through GitHub's annotations API,
        // independently of access to the separate Actions log storage host.
        string message = $"{name}: {error.GetBaseException().Message}"
            .Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
        Console.Error.WriteLine($"::error title=Planetary WPF check::{message}");
    }

    private static void Equal<T>(T expected, T actual) => Check(
        EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");

    private static void Near(double expected, double actual) => Check(
        Math.Abs(expected - actual) < 0.00001, $"Expected {expected}; actual {actual}.");
}
