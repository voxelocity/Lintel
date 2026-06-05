using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lintel.Controls;

/// <summary>A macOS-style battery glyph whose fill level and colour track the charge percentage.</summary>
public sealed class BatteryIcon : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(BatteryIcon),
            new PropertyMetadata(0.0, OnPercentChanged));

    public static readonly DependencyProperty ChargingProperty =
        DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(BatteryIcon),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty AnimatedProperty =
        DependencyProperty.Register(nameof(Animated), typeof(double), typeof(BatteryIcon),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    private double Animated { get => (double)GetValue(AnimatedProperty); set => SetValue(AnimatedProperty, value); }

    /// <summary>When set, the fill ignores the load palette and uses <see cref="MonoColor"/>.</summary>
    public bool Monochrome { get; set; }
    public Color MonoColor { get; set; } = Colors.White;

    public BatteryIcon() { Width = 25; Height = 13; }

    private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((BatteryIcon)d).BeginAnimation(AnimatedProperty,
            new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double nub = 2.0;
        double bodyW = w - nub - 1;
        var bodyRect = new Rect(0.7, 0.7, bodyW - 0.7, h - 1.4);

        // Outline
        var outline = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)); outline.Freeze();
        var pen = new Pen(outline, 1.3);
        dc.DrawRoundedRectangle(null, pen, bodyRect, 3, 3);

        // Terminal nub
        dc.DrawRoundedRectangle(outline, null, new Rect(bodyW, h / 2 - 2.4, nub + 1, 4.8), 1, 1);

        // Fill
        double pct = Math.Clamp(Animated, 0, 100) / 100.0;
        double pad = 2.0;
        double maxFill = bodyRect.Width - pad * 2;
        double fw = Math.Max(0, maxFill * pct);
        var color = Monochrome ? MonoColor
                  : Charging ? Color.FromRgb(0x30, 0xD1, 0x58)
                  : Animated <= 10 ? Color.FromRgb(0xFF, 0x45, 0x3A)
                  : Animated <= 20 ? Color.FromRgb(0xFF, 0xD6, 0x0A)
                  : Color.FromRgb(0x30, 0xD1, 0x58);
        if (fw > 0.5)
        {
            var fill = new SolidColorBrush(color); fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(bodyRect.X + pad, bodyRect.Y + pad, fw, bodyRect.Height - pad * 2), 1.5, 1.5);
        }

        // Charging bolt
        if (Charging)
        {
            var bolt = Geometry.Parse("M0,5 L3,5 L1.5,8 L4,8 L0.5,13 L2,8.5 L-0.5,8.5 Z");
            var t = new TransformGroup();
            t.Children.Add(new ScaleTransform(0.7, 0.7));
            t.Children.Add(new TranslateTransform(w / 2 - 2.5, h / 2 - 4.5));
            var bb = new SolidColorBrush(Colors.White); bb.Freeze();
            dc.PushTransform(t);
            dc.DrawGeometry(bb, null, bolt);
            dc.Pop();
        }
    }
}
