using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lintel.Controls;

/// <summary>
/// A compact circular gauge that fills clockwise from the top and is colour-coded by load
/// (green → amber → red). The sweep animates whenever the value changes for a fluid feel.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(RingGauge),
            new PropertyMetadata(0.0, OnPercentChanged));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Internal animated value actually drawn.
    private static readonly DependencyProperty AnimatedProperty =
        DependencyProperty.Register(nameof(Animated), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    private double Animated { get => (double)GetValue(AnimatedProperty); set => SetValue(AnimatedProperty, value); }

    public RingGauge()
    {
        Width = 20;
        Height = 20;
    }

    private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RingGauge)d;
        var anim = new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        g.BeginAnimation(AnimatedProperty, anim);
    }

    public static Color ColorFor(double percent)
    {
        // green (#30D158) → amber (#FFD60A) → red (#FF453A)
        if (percent <= 50)
            return Lerp(Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0xFF, 0xD6, 0x0A), percent / 50.0);
        return Lerp(Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0x45, 0x3A), (percent - 50) / 50.0);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        double th = Thickness;
        double r = (size - th) / 2.0;
        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
        double value = Math.Clamp(Animated, 0, 100);

        // Track
        var track = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), th);
        dc.DrawEllipse(null, track, center, r, r);

        if (value <= 0.01) return;

        var color = ColorFor(value);
        var pen = new Pen(new SolidColorBrush(color), th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        double sweep = value / 100.0 * 360.0;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var start = PointOnCircle(center, r, -90);
            var end = PointOnCircle(center, r, -90 + sweep);
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static Point PointOnCircle(Point c, double r, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }
}
