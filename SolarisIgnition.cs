using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SolarisLauncher;

public partial class MainWindow
{
    private readonly Random _ignitionRandom = new();
    private readonly List<IgnitionSpark> _ignitionSparks = new();
    private DispatcherTimer? _ignitionTimer;
    private double _ignitionTargetWidth;
    private double _ignitionDisplayedWidth;
    private bool _ignitionPulseActive;
    private string? _ignitionDetail;

    private void SetIgnitionDetail(string? text)
    {
        _ignitionDetail = text;
        RefreshIgnitionPosition();
    }
    private sealed class IgnitionSpark
    {
        public Ellipse Shape { get; init; } = null!;
        public double X { get; set; }
        public double Y { get; set; }
        public double VX { get; set; }
        public double VY { get; set; }
        public double Life { get; set; }
    }

    private void InitializeIgnition()
    {
        for (int i = 0; i < 9; i++)
        {
            var spark = new Ellipse
            {
                Width = i % 3 == 0 ? 4 : 2,
                Height = i % 3 == 0 ? 4 : 2,
                Fill = new SolidColorBrush(i % 3 == 0
                    ? Color.FromRgb(255, 251, 205) : Color.FromRgb(255, 189, 59)),
                IsHitTestVisible = false,
                Opacity = 0
            };
            IgnitionParticles.Children.Add(spark);
            _ignitionSparks.Add(new IgnitionSpark { Shape = spark });
        }
        _ignitionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60),
            DispatcherPriority.Background, (_, _) => AdvanceIgnition(), Dispatcher);
        _ignitionTimer.Stop();
        IgnitionFront.Opacity = 0;
        RefreshIgnitionPosition();
    }

    private void IgnitionTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Window resizing should relocate the front immediately.
        double pct = Math.Clamp(Progress?.Value ?? 0, 0, 100);
        _ignitionDisplayedWidth = Math.Max(0, IgnitionTrack.ActualWidth - 2) * pct / 100.0;
        RefreshIgnitionPosition();
        DrawIgnitionFront();
    }

    private bool _ignitionCelebrated;

    private void IgnitionProgress_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged may fire before all named controls are initialized.
        if (IgnitionTrack is null || IgnitionFill is null ||
            IgnitionFront is null || IgnitionPercent is null)
            return;

        RefreshIgnitionPosition();
        if (e.NewValue >= 99.99 && !_ignitionCelebrated)
        {
            _ignitionCelebrated = true;
            // Short lightweight golden flash, no expensive blur or per-frame shader.
            IgnitionFill.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.5,
                TimeSpan.FromMilliseconds(130))
                { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
            for (int i = 0; i < _ignitionSparks.Count; i++)
            {
                var spark = _ignitionSparks[i];
                spark.X = IgnitionTrack.ActualWidth - 5;
                spark.Y = 8;
                spark.VX = (_ignitionRandom.NextDouble() - 0.5) * 15;
                spark.VY = (_ignitionRandom.NextDouble() - 0.5) * 20;
                spark.Life = 0.7 + _ignitionRandom.NextDouble() * 0.3;
                spark.Shape.Opacity = spark.Life;
                Canvas.SetLeft(spark.Shape, spark.X);
                Canvas.SetTop(spark.Shape, spark.Y);
                spark.Shape.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(spark.Life, 0, TimeSpan.FromMilliseconds(430)));
            }
        }
        if (e.NewValue <= 0.01)
            _ignitionCelebrated = false;
    }

    private void RefreshIgnitionPosition()
    {
        // WPF can raise size/value notifications while loading XAML. Named
        // components further down the tree are not guaranteed to exist yet.
        if (IgnitionTrack is null || IgnitionFill is null ||
            IgnitionFront is null || IgnitionPercent is null)
            return;
        double pct = Math.Clamp(Progress?.Value ?? 0, 0, 100);
        // The loading track is an affordance, not permanent decoration.
        // Hide it while idle and after completion, keeping a one-line status.
        // Percentage comes from the real installer, never a fake timer.
        bool inProgress = pct > 0.01 && pct < 99.99;
        Visibility trackVisibility = inProgress ? Visibility.Visible : Visibility.Collapsed;
        if (IgnitionTrack.Visibility != trackVisibility)
            IgnitionTrack.Visibility = trackVisibility;
        IgnitionPercent.Visibility = trackVisibility;

        double width = Math.Max(0, IgnitionTrack.ActualWidth - 2);
        _ignitionTargetWidth = width * pct / 100.0;
        IgnitionPercent.Text = _ignitionDetail ?? (pct >= 99.99
            ? "ГОТОВО" : pct <= 0.01 ? "ГОТОВО" : "ПОДГОТОВКА");

        if (!inProgress)
        {
            _ignitionDisplayedWidth = 0;
            _ignitionTimer?.Stop();
            StopIgnitionPulse();
            DrawIgnitionFront();
            foreach (IgnitionSpark spark in _ignitionSparks) spark.Shape.Opacity = 0;
        }
        else
        {
            StartIgnitionPulse();
            _ignitionTimer?.Start();
            DrawIgnitionFront();
        }
    }

    private void StartIgnitionPulse()
    {
        if (_ignitionPulseActive) return;
        _ignitionPulseActive = true;
        IgnitionFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1.0,
            TimeSpan.FromMilliseconds(550))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopIgnitionPulse()
    {
        if (!_ignitionPulseActive) return;
        _ignitionPulseActive = false;
        IgnitionFront.BeginAnimation(OpacityProperty, null);
        IgnitionFront.Opacity = 0;
    }

    private void DrawIgnitionFront()
    {
        if (IgnitionFill is null || IgnitionFront is null) return;
        IgnitionFill.Width = _ignitionDisplayedWidth;
        double width = Math.Max(0, IgnitionTrack.ActualWidth - 2);
        double leading = Math.Clamp(_ignitionDisplayedWidth - 3, 0, width);
        IgnitionFront.Margin = new Thickness(leading, 0, 0, 0);
    }

    private void AdvanceIgnition()
    {
        // Smoothly interpolate progress reported by downloads and the installer.
        // The visual fill may lag the actual stage by a few frames, never lead it.
        double remaining = _ignitionTargetWidth - _ignitionDisplayedWidth;
        _ignitionDisplayedWidth = Math.Abs(remaining) < 0.6
            ? _ignitionTargetWidth : _ignitionDisplayedWidth + remaining * 0.34;
        DrawIgnitionFront();

        if (Progress.Value >= 99.99 && Math.Abs(remaining) < 0.6)
        {
            _ignitionTimer?.Stop();
            StopIgnitionPulse();
            foreach (IgnitionSpark spark in _ignitionSparks) spark.Shape.Opacity = 0;
            return;
        }

        double leading = _ignitionDisplayedWidth;
        foreach (IgnitionSpark spark in _ignitionSparks)
        {
            spark.Life -= 0.08;
            if (spark.Life <= 0)
            {
                spark.X = Math.Max(1, leading - 7) + (_ignitionRandom.NextDouble() - 0.5) * 9;
                spark.Y = 8;
                spark.VX = (_ignitionRandom.NextDouble() - 0.45) * 6;
                spark.VY = (_ignitionRandom.NextDouble() - 0.55) * 8;
                spark.Life = 0.6 + _ignitionRandom.NextDouble() * 0.5;
            }
            spark.X += spark.VX;
            spark.Y += spark.VY;
            Canvas.SetLeft(spark.Shape, spark.X);
            Canvas.SetTop(spark.Shape, spark.Y);
            spark.Shape.Opacity = Math.Clamp(spark.Life, 0, 1);
        }
    }
}
