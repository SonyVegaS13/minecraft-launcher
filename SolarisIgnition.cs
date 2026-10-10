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
        for (int i = 0; i < 13; i++)
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
        _ignitionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background, (_, _) => AdvanceIgnition(), Dispatcher);
        _ignitionTimer.Stop();
        IgnitionFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1.0,
            TimeSpan.FromMilliseconds(550))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        RefreshIgnitionPosition();
    }

    private void IgnitionTrack_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RefreshIgnitionPosition();

    private bool _ignitionCelebrated;

    private void IgnitionProgress_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
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
        if (IgnitionTrack is null || IgnitionFill is null || IgnitionPercent is null)
            return;
        double pct = Math.Clamp(Progress?.Value ?? 0, 0, 100);
        double width = Math.Max(0, IgnitionTrack.ActualWidth - 2);
        IgnitionFill.Width = width * pct / 100.0;
        double leading = Math.Clamp(IgnitionFill.Width - 3, 0, width);
        IgnitionFront.Margin = new Thickness(leading, 0, 0, 0);
        // Progress.Value controls the visual multi-stage plasma position.
        // It is NOT a truthful overall byte percentage. Only report numeric
        // percentages for downloads with known ProgressedBytes/TotalBytes.
        IgnitionPercent.Text = pct >= 99.99 ? "100%" : pct <= 0.01
            ? "ГОТОВО" : "ПРОВЕРКА";
        bool active = pct > 0.01 && pct < 99.99;
        if (active) _ignitionTimer?.Start();
        else
        {
            _ignitionTimer?.Stop();
            foreach (IgnitionSpark spark in _ignitionSparks)
                spark.Shape.Opacity = 0;
        }
    }

    private void AdvanceIgnition()
    {
        double leading = IgnitionFill.Width;
        foreach (IgnitionSpark spark in _ignitionSparks)
        {
            spark.Life -= 0.08;
            if (spark.Life <= 0)
            {
                spark.X = leading + (_ignitionRandom.NextDouble() - 0.5) * 10;
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
