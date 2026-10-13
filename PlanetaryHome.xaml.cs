using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SolarisLauncher;

public enum WorldSelection { None, Vanilla, Modded }

/// <summary>
/// Optional future seam between a confirmed launch and its camera/flight transition.
/// The prototype never calls this: existing launch handlers remain authoritative.
/// </summary>
public interface IPlanetaryLaunchTransition
{
    Task PrepareAsync(WorldSelection world, CancellationToken cancellationToken);
}

public partial class PlanetaryHome : UserControl
{
    public static readonly DependencyProperty VanillaDetailsProperty = DependencyProperty.Register(
        nameof(VanillaDetails), typeof(object), typeof(PlanetaryHome));
    public static readonly DependencyProperty ModdedDetailsProperty = DependencyProperty.Register(
        nameof(ModdedDetails), typeof(object), typeof(PlanetaryHome));
    public static readonly DependencyProperty VanillaStatusProperty = DependencyProperty.Register(
        nameof(VanillaStatus), typeof(string), typeof(PlanetaryHome),
        new PropertyMetadata("Сервер офлайн · хостинг не подключён"));
    public static readonly DependencyProperty ModdedStatusProperty = DependencyProperty.Register(
        nameof(ModdedStatus), typeof(string), typeof(PlanetaryHome),
        new PropertyMetadata("Проверяем сервер…"));

    public object? VanillaDetails { get => GetValue(VanillaDetailsProperty); set => SetValue(VanillaDetailsProperty, value); }
    public object? ModdedDetails { get => GetValue(ModdedDetailsProperty); set => SetValue(ModdedDetailsProperty, value); }
    public string VanillaStatus { get => (string)GetValue(VanillaStatusProperty); set => SetValue(VanillaStatusProperty, value); }
    public string ModdedStatus { get => (string)GetValue(ModdedStatusProperty); set => SetValue(ModdedStatusProperty, value); }
    public WorldSelection SelectedWorld { get; private set; }
    public IPlanetaryLaunchTransition? LaunchTransition { get; set; }
    public event EventHandler? SelectionChanged;

    private readonly DispatcherTimer _pointerTimer;
    private readonly AnimationClock[] _ambientClocks;
    private Window? _hostWindow;
    private bool _allowMotion;
    private double _targetX, _targetY;
    private double _pointerX, _pointerY;

    public PlanetaryHome()
    {
        InitializeComponent();
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _pointerTimer.Tick += PointerTimer_Tick;
        _ambientClocks =
        [
            CreateRotation(VanillaRotation, 105, 0, 360),
            CreateRotation(ModdedRotation, 125, 0, -360),
            CreateRotation(RingRotation, 32, -3, 3, true)
        ];
        Loaded += Home_Loaded;
        Unloaded += Home_Unloaded;
        IsVisibleChanged += (_, _) => UpdateMotionState();
    }

    private static AnimationClock CreateRotation(RotateTransform transform, double seconds,
        double from, double to, bool autoReverse = false)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = autoReverse
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        var clock = (AnimationClock)animation.CreateClock(true);
        transform.ApplyAnimationClock(RotateTransform.AngleProperty, clock);
        clock.Controller?.Pause();
        return clock;
    }

    private void Home_Loaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow != null)
        {
            _hostWindow.Activated += HostStateChanged;
            _hostWindow.Deactivated += HostStateChanged;
            _hostWindow.StateChanged += HostStateChanged;
        }
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        RenderCapability.TierChanged += HostStateChanged;
        UpdateMotionState();
    }

    private void Home_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow != null)
        {
            _hostWindow.Activated -= HostStateChanged;
            _hostWindow.Deactivated -= HostStateChanged;
            _hostWindow.StateChanged -= HostStateChanged;
        }
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        RenderCapability.TierChanged -= HostStateChanged;
        _hostWindow = null;
        UpdateMotionState();
    }

    private void HostStateChanged(object? sender, EventArgs e) => UpdateMotionState();
    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateMotionState();
    }

    private void UpdateMotionState()
    {
        _allowMotion = IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation &&
            (RenderCapability.Tier >> 16) > 0 && _hostWindow is { IsActive: true } &&
            _hostWindow.WindowState != WindowState.Minimized;
        foreach (var clock in _ambientClocks)
        {
            if (_allowMotion) clock.Controller?.Resume();
            else clock.Controller?.Pause();
        }
        if (!_allowMotion)
        {
            _pointerTimer.Stop();
            _pointerX = _pointerY = _targetX = _targetY = 0;
            ApplyPointerPose();
            SetHover(VanillaPlanet, false, false);
            SetHover(ModdedPlanet, false, false);
            ApplySelection(animate: false);
        }
    }

    private void Scene_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_allowMotion) return;
        var position = e.GetPosition(DesignCanvas);
        _targetX = Math.Clamp((position.X - 640) / 640, -1, 1);
        _targetY = Math.Clamp((position.Y - 300) / 300, -1, 1);
        if (!_pointerTimer.IsEnabled) _pointerTimer.Start();
    }

    private void Scene_MouseLeave(object sender, MouseEventArgs e)
    {
        _targetX = _targetY = 0;
        if (_allowMotion && !_pointerTimer.IsEnabled) _pointerTimer.Start();
    }

    private void PointerTimer_Tick(object? sender, EventArgs e)
    {
        _pointerX += (_targetX - _pointerX) * 0.19;
        _pointerY += (_targetY - _pointerY) * 0.19;
        if (Math.Abs(_targetX - _pointerX) + Math.Abs(_targetY - _pointerY) < 0.002)
        {
            _pointerX = _targetX;
            _pointerY = _targetY;
            _pointerTimer.Stop();
        }
        ApplyPointerPose();
    }

    private void ApplyPointerPose()
    {
        SpaceParallax.X = -_pointerX * 7;
        SpaceParallax.Y = -_pointerY * 4;
        HeadTurn.Angle = _pointerX * 5.5;
        HeadShift.X = _pointerX * 2;
        HeadShift.Y = _pointerY * 0.8;
    }

    private void Planet_MouseEnter(object sender, MouseEventArgs e) => SetHover((Button)sender, true, _allowMotion);
    private void Planet_MouseLeave(object sender, MouseEventArgs e) => SetHover((Button)sender, false, _allowMotion);

    private void SetHover(Button planet, bool hovering, bool animate)
    {
        var vanilla = ReferenceEquals(planet, VanillaPlanet);
        var scale = vanilla ? VanillaHover : ModdedHover;
        var glow = vanilla ? VanillaGlow : ModdedGlow;
        var selected = vanilla ? SelectedWorld == WorldSelection.Vanilla : SelectedWorld == WorldSelection.Modded;
        var size = hovering && !selected ? 1.035 : 1;
        SetAnimated(scale, ScaleTransform.ScaleXProperty, size, animate, 180);
        SetAnimated(scale, ScaleTransform.ScaleYProperty, size, animate, 180);
        SetAnimated(glow, OpacityProperty, hovering ? 0.95 : (vanilla ? 0.56 : 0.61), animate, 180);
    }

    private void VanillaPlanet_Click(object sender, RoutedEventArgs e) => Select(WorldSelection.Vanilla);
    private void ModdedPlanet_Click(object sender, RoutedEventArgs e) => Select(WorldSelection.Modded);
    private void ReturnButton_Click(object sender, RoutedEventArgs e) => ResetSelection();

    private void Select(WorldSelection world)
    {
        if (SelectedWorld == world) return;
        SelectedWorld = world;
        SetHover(VanillaPlanet, false, _allowMotion);
        SetHover(ModdedPlanet, false, _allowMotion);
        ApplySelection(_allowMotion);
        // Announce a concrete destination and preserve normal keyboard navigation into details.
        ReturnButton.Focus();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetSelection()
    {
        var previous = SelectedWorld;
        if (previous == WorldSelection.None) return;
        SelectedWorld = WorldSelection.None;
        ApplySelection(_allowMotion);
        (previous == WorldSelection.Vanilla ? VanillaPlanet : ModdedPlanet).Focus();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySelection(bool animate)
    {
        var selected = SelectedWorld != WorldSelection.None;
        var vanilla = SelectedWorld == WorldSelection.Vanilla;
        var modded = SelectedWorld == WorldSelection.Modded;
        VanillaPlanet.IsHitTestVisible = !modded;
        ModdedPlanet.IsHitTestVisible = !vanilla;
        VanillaPlanet.IsTabStop = !modded;
        ModdedPlanet.IsTabStop = !vanilla;
        SetAnimated(VanillaPlanet, OpacityProperty, modded ? 0 : 1, animate, 240);
        SetAnimated(ModdedPlanet, OpacityProperty, vanilla ? 0 : 1, animate, 240);
        SetAnimated(VanillaFocus, ScaleTransform.ScaleXProperty, vanilla ? 1.16 : 1, animate);
        SetAnimated(VanillaFocus, ScaleTransform.ScaleYProperty, vanilla ? 1.16 : 1, animate);
        SetAnimated(ModdedFocus, ScaleTransform.ScaleXProperty, modded ? 1.16 : 1, animate);
        SetAnimated(ModdedFocus, ScaleTransform.ScaleYProperty, modded ? 1.16 : 1, animate);
        SetAnimated(VanillaPosition, TranslateTransform.XProperty, vanilla ? 5 : 0, animate);
        SetAnimated(ModdedPosition, TranslateTransform.XProperty, modded ? -477 : 0, animate);
        SetAnimated(VanillaPosition, TranslateTransform.YProperty, vanilla ? 12 : 0, animate);
        SetAnimated(ModdedPosition, TranslateTransform.YProperty, modded ? 12 : 0, animate);
        VanillaLabel.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        ModdedLabel.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        ReturnButton.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        SelectedPlanetCaption.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        SelectedPlanetCaption.Text = vanilla ? "SOLARIS VANILLA  /  MINECRAFT 26.2" : "SOLARIS MODDED  /  FORGE 47.4.20";
        SceneInstruction.Text = selected ? "ТВОЙ МИР НА ОРБИТЕ" : "ВЫБЕРИ СВОЙ МИР";
        SceneHint.Visibility = selected ? Visibility.Hidden : Visibility.Visible;
        SceneCoordinates.Visibility = selected ? Visibility.Hidden : Visibility.Visible;
        VanillaDetailsPresenter.Visibility = vanilla ? Visibility.Visible : Visibility.Collapsed;
        ModdedDetailsPresenter.Visibility = modded ? Visibility.Visible : Visibility.Collapsed;
        DetailsEyebrow.Text = vanilla ? "КООРДИНАТЫ / VANILLA" : "КООРДИНАТЫ / MODDED";
        DetailsPanel.BorderBrush = vanilla ? WarmPanelBorder : PurplePanelBorder;
        DetailsPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        SetAnimated(DetailsPanel, OpacityProperty, selected ? 1 : 0, animate, 320);
    }

    private static readonly SolidColorBrush WarmPanelBorder = FrozenBrush(Color.FromArgb(150, 222, 136, 67));
    private static readonly SolidColorBrush PurplePanelBorder = FrozenBrush(Color.FromArgb(150, 169, 116, 230));
    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void SetAnimated(Animatable target, DependencyProperty property, double destination,
        bool animate, int milliseconds = 420)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, destination);
        if (!animate || Math.Abs(current - destination) < 0.0001) return;
        var animation = new DoubleAnimation(current, destination, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetAnimated(UIElement target, DependencyProperty property, double destination,
        bool animate, int milliseconds = 420)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, destination);
        if (!animate || Math.Abs(current - destination) < 0.0001) return;
        var animation = new DoubleAnimation(current, destination, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Home_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SelectedWorld != WorldSelection.None)
        {
            ResetSelection();
            e.Handled = true;
        }
    }
}
