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
        nameof(VanillaStatus), typeof(string), typeof(PlanetaryHome), new PropertyMetadata("Сервер офлайн"));
    public static readonly DependencyProperty VanillaPlayersProperty = DependencyProperty.Register(
        nameof(VanillaPlayers), typeof(string), typeof(PlanetaryHome), new PropertyMetadata("Онлайн: —"));
    public static readonly DependencyProperty ModdedStatusProperty = DependencyProperty.Register(
        nameof(ModdedStatus), typeof(string), typeof(PlanetaryHome), new PropertyMetadata("Проверяем сервер…"));
    public static readonly DependencyProperty ModdedPresenceProperty = DependencyProperty.Register(
        nameof(ModdedPresence), typeof(string), typeof(PlanetaryHome), new PropertyMetadata("Проверяем сервер…"));
    public static readonly DependencyProperty ModdedPlayersProperty = DependencyProperty.Register(
        nameof(ModdedPlayers), typeof(string), typeof(PlanetaryHome), new PropertyMetadata("Игроки: — / 200"));
    public static readonly DependencyProperty ModdedStatusBrushProperty = DependencyProperty.Register(
        nameof(ModdedStatusBrush), typeof(Brush), typeof(PlanetaryHome), new PropertyMetadata(Brushes.Goldenrod));
    public static readonly DependencyProperty VanillaArtworkProperty = DependencyProperty.Register(
        nameof(VanillaArtwork), typeof(ImageSource), typeof(PlanetaryHome), new PropertyMetadata(null, ArtworkChanged));
    public static readonly DependencyProperty ModdedArtworkProperty = DependencyProperty.Register(
        nameof(ModdedArtwork), typeof(ImageSource), typeof(PlanetaryHome), new PropertyMetadata(null, ArtworkChanged));
    public static readonly DependencyProperty EffectsEnabledProperty = DependencyProperty.Register(
        nameof(EffectsEnabled), typeof(bool), typeof(PlanetaryHome), new PropertyMetadata(true, EffectsChanged));

    public object? VanillaDetails { get => GetValue(VanillaDetailsProperty); set => SetValue(VanillaDetailsProperty, value); }
    public object? ModdedDetails { get => GetValue(ModdedDetailsProperty); set => SetValue(ModdedDetailsProperty, value); }
    public string VanillaStatus { get => (string)GetValue(VanillaStatusProperty); set => SetValue(VanillaStatusProperty, value); }
    public string VanillaPlayers { get => (string)GetValue(VanillaPlayersProperty); set => SetValue(VanillaPlayersProperty, value); }
    public string ModdedStatus { get => (string)GetValue(ModdedStatusProperty); set => SetValue(ModdedStatusProperty, value); }
    public string ModdedPresence { get => (string)GetValue(ModdedPresenceProperty); set => SetValue(ModdedPresenceProperty, value); }
    public string ModdedPlayers { get => (string)GetValue(ModdedPlayersProperty); set => SetValue(ModdedPlayersProperty, value); }
    public Brush ModdedStatusBrush { get => (Brush)GetValue(ModdedStatusBrushProperty); set => SetValue(ModdedStatusBrushProperty, value); }
    public ImageSource? VanillaArtwork { get => (ImageSource?)GetValue(VanillaArtworkProperty); set => SetValue(VanillaArtworkProperty, value); }
    public ImageSource? ModdedArtwork { get => (ImageSource?)GetValue(ModdedArtworkProperty); set => SetValue(ModdedArtworkProperty, value); }
    public bool EffectsEnabled { get => (bool)GetValue(EffectsEnabledProperty); set => SetValue(EffectsEnabledProperty, value); }
    public WorldSelection SelectedWorld => _interaction.PinnedWorld;
    public IPlanetaryLaunchTransition? LaunchTransition { get; set; }
    public event EventHandler? SelectionChanged;

    private readonly PlanetaryInteractionState _interaction = new();
    private readonly DispatcherTimer _pointerTimer;
    private readonly DispatcherTimer _leaveTimer;
    private readonly AnimationClock[] _ambientClocks;
    private Window? _hostWindow;
    private bool _allowMotion;
    private bool _suppressFocusHover;
    private WorldSelection _artworkWorld;
    private long _pendingLeaveToken;
    private double _targetX, _targetY;
    private double _pointerX, _pointerY;

    public PlanetaryHome()
    {
        InitializeComponent();
        LoadArtworkLayers();
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _pointerTimer.Tick += PointerTimer_Tick;
        _leaveTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(180) };
        _leaveTimer.Tick += LeaveTimer_Tick;
        _ambientClocks =
        [
            CreateRotation(VanillaRotation, 105, 0, 360),
            CreateRotation(ModdedRotation, 125, 0, -360),
            CreateRotation(RingRotation, 32, -3, 3, true),
            CreatePulse(VanillaAmbientGlow, 5.5),
            CreatePulse(ModdedAmbientGlow, 6.5)
        ];
        // A shared transform keeps the optional two ring layers aligned without another timer.
        ModdedRingsFrontImage.RenderTransform = RingRotation;
        ModdedRingsFrontFallback.RenderTransform = RingRotation;
        Loaded += Home_Loaded;
        Unloaded += Home_Unloaded;
        IsVisibleChanged += (_, _) => UpdateMotionState();
    }

    private static void EffectsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var home = (PlanetaryHome)sender;
        if (home._ambientClocks != null) home.UpdateMotionState();
    }

    private static void ArtworkChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var home = (PlanetaryHome)sender;
        if (home.ArtworkFallback != null) home.UpdateArtworkVisibility();
    }

    private void LoadArtworkLayers()
    {
        LoadLayer(SpaceBackgroundImage, "space-background.png", SpaceVectorFallback);
        LoadLayer(BridgeForegroundImage, "bridge-foreground.png", BridgeVectorFallback);
        LoadLayer(OperatorBodyImage, "operator-body.png", OperatorBodyFallback);
        LoadLayer(OperatorHeadImage, "operator-head.png", OperatorHeadFallback);
        LoadLayer(VanillaSurfaceImage, "vanilla-surface.png", VanillaTextureFallback);
        LoadLayer(VanillaAtmosphereImage, "vanilla-atmosphere.png");
        LoadLayer(ModdedSurfaceImage, "modded-surface.png", ModdedTextureFallback);
        LoadLayer(ModdedAtmosphereImage, "modded-atmosphere.png");
        LoadLayer(ModdedRingsBackImage, "modded-rings-back.png", ModdedRingsBackFallback);
        LoadLayer(ModdedRingsFrontImage, "modded-rings-front.png", ModdedRingsFrontFallback);
    }

    private static void LoadLayer(Image image, string fileName, UIElement? fallback = null)
    {
        image.Source = PlanetaryArtwork.TryLoad(fileName);
        image.Visibility = image.Source == null ? Visibility.Collapsed : Visibility.Visible;
        if (fallback != null) fallback.Visibility = image.Source == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static AnimationClock CreateRotation(RotateTransform transform, double seconds,
        double from, double to, bool autoReverse = false)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = autoReverse };
        return ApplyAmbientClock(transform, RotateTransform.AngleProperty, animation);
    }

    private static AnimationClock CreatePulse(UIElement target, double seconds)
    {
        var animation = new DoubleAnimation(0.10, 0.27, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        var clock = (AnimationClock)animation.CreateClock(true);
        target.ApplyAnimationClock(OpacityProperty, clock);
        clock.Controller?.Pause();
        return clock;
    }

    private static AnimationClock ApplyAmbientClock(Animatable target, DependencyProperty property, DoubleAnimation animation)
    {
        Timeline.SetDesiredFrameRate(animation, 30);
        var clock = (AnimationClock)animation.CreateClock(true);
        target.ApplyAnimationClock(property, clock);
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
        if (_ambientClocks == null) return;
        var active = IsLoaded && IsVisible && _hostWindow is { IsActive: true } &&
            _hostWindow.WindowState != WindowState.Minimized;
        if (active && !_interaction.IsActive) _interaction.Resume();
        else if (!active)
        {
            _interaction.Deactivate();
            CancelLeave();
        }
        _allowMotion = active && EffectsEnabled && SystemParameters.ClientAreaAnimation &&
            (RenderCapability.Tier >> 16) > 0 &&
            RenderOptions.ProcessRenderMode != System.Windows.Interop.RenderMode.SoftwareOnly;
        foreach (var clock in _ambientClocks)
        {
            if (_allowMotion) clock.Controller?.Resume();
            else clock.Controller?.Pause();
        }
        VanillaAmbientGlow.Visibility = ModdedAmbientGlow.Visibility = _allowMotion ? Visibility.Visible : Visibility.Collapsed;
        if (!_allowMotion)
        {
            _pointerTimer.Stop();
            _pointerX = _pointerY = _targetX = _targetY = 0;
            ApplyPointerPose();
        }
        ApplyInteraction(animate: false);
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
        ScheduleLeave(_interaction.HoveredWorld);
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
        VanillaParallax.X = ModdedParallax.X = _pointerX * 2;
        VanillaParallax.Y = ModdedParallax.Y = _pointerY * 1.2;
        HeadTurn.Angle = _pointerX * 5.5;
        HeadShift.X = _pointerX * 2;
        HeadShift.Y = _pointerY * 0.8;
    }

    private WorldSelection WorldOf(object sender) => ReferenceEquals(sender, VanillaPlanet) ? WorldSelection.Vanilla : WorldSelection.Modded;

    private void Planet_MouseEnter(object sender, MouseEventArgs e) => EnterWorld(WorldOf(sender));
    private void Planet_MouseLeave(object sender, MouseEventArgs e) => ScheduleLeave(WorldOf(sender));
    private void Planet_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_suppressFocusHover) EnterWorld(WorldOf(sender));
    }
    private void Planet_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!((Button)sender).IsMouseOver) ScheduleLeave(WorldOf(sender));
    }

    private void EnterWorld(WorldSelection world)
    {
        CancelLeave();
        _interaction.EnterWorld(world);
        ApplyInteraction(_allowMotion);
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        var world = _interaction.VisibleCardWorld;
        if (world == WorldSelection.None) return;
        CancelLeave();
        _interaction.EnterCard(world);
        ApplyInteraction(_allowMotion);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e) => ScheduleLeave(_interaction.HoveredWorld);

    private void ScheduleLeave(WorldSelection world)
    {
        if (world == WorldSelection.None) return;
        var token = _interaction.LeaveArea(world);
        if (token == 0) return;
        _pendingLeaveToken = token;
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    private void CancelLeave()
    {
        _leaveTimer.Stop();
        _pendingLeaveToken = 0;
    }

    private void LeaveTimer_Tick(object? sender, EventArgs e)
    {
        _leaveTimer.Stop();
        if (_interaction.CompleteLeave(_pendingLeaveToken)) ApplyInteraction(_allowMotion);
        _pendingLeaveToken = 0;
    }

    private void VanillaPlanet_Click(object sender, RoutedEventArgs e) => Select(WorldSelection.Vanilla);
    private void ModdedPlanet_Click(object sender, RoutedEventArgs e) => Select(WorldSelection.Modded);
    private void CloseCardButton_Click(object sender, RoutedEventArgs e) => ResetSelection();

    private void Select(WorldSelection world)
    {
        if (!_interaction.IsActive) return;
        var changed = SelectedWorld != world;
        CancelLeave();
        _interaction.PinWorld(world);
        ApplyInteraction(_allowMotion);
        // Keyboard users can reach the existing launch/audit controls normally.
        CloseCardButton.Focus();
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetSelection()
    {
        var previous = SelectedWorld;
        CancelLeave();
        _interaction.Close();
        ApplyInteraction(_allowMotion);
        if (previous == WorldSelection.None) return;
        // Returning focus must not reopen the just-closed summary under a stationary cursor.
        _suppressFocusHover = true;
        try { (previous == WorldSelection.Vanilla ? VanillaPlanet : ModdedPlanet).Focus(); }
        finally { _suppressFocusHover = false; }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyInteraction(bool animate)
    {
        var vanilla = SelectedWorld == WorldSelection.Vanilla;
        var modded = SelectedWorld == WorldSelection.Modded;
        // Pinning affects the rail and an outline only. Both planets stay visible and actionable.
        VanillaPlanet.IsHitTestVisible = ModdedPlanet.IsHitTestVisible = true;
        VanillaPlanet.IsTabStop = ModdedPlanet.IsTabStop = true;
        VanillaPlanet.Opacity = ModdedPlanet.Opacity = 1;
        VanillaLabel.Visibility = ModdedLabel.Visibility = Visibility.Visible;
        SetAnimated(VanillaSelectionRing, OpacityProperty, vanilla ? 1 : 0, animate, 230);
        SetAnimated(ModdedSelectionRing, OpacityProperty, modded ? 1 : 0, animate, 230);
        SetHover(VanillaPlanet, _interaction.HoveredWorld == WorldSelection.Vanilla, animate);
        SetHover(ModdedPlanet, _interaction.HoveredWorld == WorldSelection.Modded, animate);

        if (_interaction.IsPinned)
        {
            // Preserve complete content during close fade; a new pin replaces it immediately.
            VanillaDetailsPresenter.Visibility = vanilla ? Visibility.Visible : Visibility.Collapsed;
            ModdedDetailsPresenter.Visibility = modded ? Visibility.Visible : Visibility.Collapsed;
            _artworkWorld = SelectedWorld;
            DetailsEyebrow.Text = vanilla ? "ЗАКРЕПЛЕНО / VANILLA" : "ЗАКРЕПЛЕНО / MODDED";
            DetailsPanel.BorderBrush = vanilla ? WarmPanelBorder : PurplePanelBorder;
        }
        SetPanelVisible(DetailsPanel, _interaction.IsPinned, animate);
        UpdateArtworkVisibility();

        var summary = !_interaction.IsPinned && _interaction.VisibleCardWorld != WorldSelection.None;
        if (summary)
        {
            var warm = _interaction.VisibleCardWorld == WorldSelection.Vanilla;
            HoverTitle.Text = warm ? "SOLARIS VANILLA" : "SOLARIS MODDED";
            HoverTitle.Foreground = warm ? WarmTitleBrush : PurpleTitleBrush;
            HoverCard.BorderBrush = warm ? WarmPanelBorder : PurplePanelBorder;
            VanillaSummary.Visibility = warm ? Visibility.Visible : Visibility.Collapsed;
            ModdedSummary.Visibility = warm ? Visibility.Collapsed : Visibility.Visible;
            // Each card is directly above its source, with a short vertical handoff.
            // Crossing into this connector/card cancels the leave without crossing the other planet.
            Canvas.SetLeft(HoverCard, warm ? 69 : 473);
            Canvas.SetLeft(HoverBridge, warm ? 69 : 473);
        }
        HoverBridge.Visibility = summary ? Visibility.Visible : Visibility.Collapsed;
        SetPanelVisible(HoverCard, summary, animate);
        SceneInstruction.Visibility = summary ? Visibility.Hidden : Visibility.Visible;
        SceneHint.Visibility = summary ? Visibility.Hidden : Visibility.Visible;
        SceneInstruction.Text = _interaction.IsPinned ? "ОБА МИРА НА ОРБИТЕ" : "ВЫБЕРИ СВОЙ МИР";
    }

    private void UpdateArtworkVisibility()
    {
        var vanilla = _artworkWorld == WorldSelection.Vanilla;
        var source = vanilla ? VanillaArtwork : ModdedArtwork;
        VanillaArtworkImage.Visibility = vanilla && source != null ? Visibility.Visible : Visibility.Collapsed;
        ModdedArtworkImage.Visibility = !vanilla && source != null ? Visibility.Visible : Visibility.Collapsed;
        ArtworkFallback.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
        ArtworkFallbackTitle.Text = vanilla ? "SOLARIS VANILLA" : "SOLARIS MODDED";
        ArtworkFallbackTitle.Foreground = vanilla ? WarmTitleBrush : PurpleTitleBrush;
    }

    private void SetHover(Button planet, bool hovering, bool animate)
    {
        var vanilla = ReferenceEquals(planet, VanillaPlanet);
        var scale = vanilla ? VanillaHover : ModdedHover;
        var glow = vanilla ? VanillaGlow : ModdedGlow;
        var selected = vanilla ? SelectedWorld == WorldSelection.Vanilla : SelectedWorld == WorldSelection.Modded;
        SetAnimated(scale, ScaleTransform.ScaleXProperty, hovering ? 1.05 : 1, animate, 230);
        SetAnimated(scale, ScaleTransform.ScaleYProperty, hovering ? 1.05 : 1, animate, 230);
        SetAnimated(glow, OpacityProperty, hovering ? 0.94 : selected ? 0.78 : vanilla ? 0.56 : 0.61, animate, 230);
    }

    private static readonly SolidColorBrush WarmPanelBorder = FrozenBrush(Color.FromArgb(180, 222, 136, 67));
    private static readonly SolidColorBrush PurplePanelBorder = FrozenBrush(Color.FromArgb(180, 169, 116, 230));
    private static readonly SolidColorBrush WarmTitleBrush = FrozenBrush(Color.FromRgb(255, 227, 190));
    private static readonly SolidColorBrush PurpleTitleBrush = FrozenBrush(Color.FromRgb(234, 219, 255));

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static DoubleAnimation Transition(double current, double destination, int milliseconds)
    {
        var animation = new DoubleAnimation(current, destination, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        return animation;
    }

    private static void SetAnimated(Animatable target, DependencyProperty property, double destination, bool animate, int milliseconds = 230)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, destination);
        if (animate && Math.Abs(current - destination) >= 0.0001)
            target.BeginAnimation(property, Transition(current, destination, milliseconds), HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetAnimated(UIElement target, DependencyProperty property, double destination, bool animate, int milliseconds = 230)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, destination);
        if (animate && Math.Abs(current - destination) >= 0.0001)
            target.BeginAnimation(property, Transition(current, destination, milliseconds), HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetPanelVisible(UIElement panel, bool visible, bool animate)
    {
        var current = panel.Opacity;
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = visible ? 1 : 0;
        panel.IsHitTestVisible = visible;
        if (visible) panel.Visibility = Visibility.Visible;
        if (!animate || Math.Abs(current - panel.Opacity) < 0.0001)
        {
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var animation = Transition(current, visible ? 1 : 0, 230);
        if (!visible)
            animation.Completed += (_, _) =>
            {
                // A newer show cancels this collapse even if an old completion was queued.
                if ((double)panel.GetAnimationBaseValue(OpacityProperty) == 0) panel.Visibility = Visibility.Collapsed;
            };
        panel.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Home_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _interaction.VisibleCardWorld != WorldSelection.None)
        {
            ResetSelection();
            e.Handled = true;
        }
    }
}
