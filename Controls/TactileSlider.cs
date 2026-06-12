using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Lintel.Controls;

/// <summary>
/// A small, physical-feeling slider that lives inline on the bar (used by the Volume and
/// Brightness widgets). It draws an inset track, an accent fill, and a raised glossy knob,
/// and reports drags through <see cref="ValueChanged"/>. Set the displayed value with
/// <see cref="SetValue"/> when the underlying system level changes elsewhere.
/// </summary>
public sealed class TactileSlider : StackPanel
{
    private readonly Grid _trackHost;
    private readonly Border _track;
    private readonly Border _fill;
    private readonly Border _knob;
    private readonly double _trackWidth;
    private const double KnobSize = 15;

    private int _value;
    private bool _dragging;

    /// <summary>Fired (with 0..100) continuously while the user drags or clicks the slider.</summary>
    public event Action<int>? ValueChanged;

    /// <summary>Fired (with 0..100) once when the drag ends — use for expensive setters (e.g. WMI brightness).</summary>
    public event Action<int>? ValueCommitted;

    public TactileSlider(UIElement icon, Color accent, double trackWidth = 74)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _trackWidth = trackWidth;

        if (icon != null)
        {
            if (icon is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0);
            Children.Add(icon);
        }

        // Inset rail: dark, slightly recessed groove with a faint top shadow / bottom highlight.
        _track = new Border
        {
            Height = 6,
            Width = trackWidth,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = VGrad(Color.FromRgb(0x0C, 0x0C, 0x0E), Color.FromRgb(0x2A, 0x2A, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1)
        };

        // Accent fill from the left up to the knob — a glossy capsule.
        Color accentHi = Lift(accent, 0x22), accentLo = Lift(accent, -0x18);
        _fill = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = VGrad(accentHi, accentLo),
            IsHitTestVisible = false
        };

        // Raised physical knob: light-on-top metal pebble with a soft drop shadow.
        _knob = new Border
        {
            Width = KnobSize,
            Height = KnobSize,
            CornerRadius = new CornerRadius(KnobSize / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = VGrad(Color.FromRgb(0xFB, 0xFB, 0xFD), Color.FromRgb(0xC6, 0xCA, 0xD2)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(0.7),
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.5, Color = Colors.Black },
            IsHitTestVisible = false
        };
        // A crisp top-gloss highlight on the knob so it reads as glass/metal.
        var gloss = new Ellipse
        {
            Width = KnobSize - 5,
            Height = (KnobSize - 5) / 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1.6, 0, 0),
            Fill = VGrad(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false
        };
        _knob.Child = gloss;

        _trackHost = new Grid
        {
            Width = trackWidth,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,   // make the whole strip hittable
            Cursor = Cursors.Hand
        };
        _trackHost.Children.Add(_track);
        _trackHost.Children.Add(_fill);
        _trackHost.Children.Add(_knob);
        Children.Add(_trackHost);

        _trackHost.MouseLeftButtonDown += OnDown;
        _trackHost.MouseMove += OnMove;
        _trackHost.MouseLeftButtonUp += OnUp;

        SetValue(0);
    }

    /// <summary>Update the displayed value without firing <see cref="ValueChanged"/>.</summary>
    public void SetValue(int value)
    {
        _value = Math.Clamp(value, 0, 100);
        if (_dragging) return;   // don't fight the user's drag
        Layout();
    }

    private void Layout()
    {
        double usable = _trackWidth - KnobSize;
        double knobLeft = _value / 100.0 * usable;
        _knob.Margin = new Thickness(knobLeft, 0, 0, 0);
        _fill.Width = knobLeft + KnobSize / 2.0;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _trackHost.CaptureMouse();
        e.Handled = true;
        Apply(e.GetPosition(_trackHost).X);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Apply(e.GetPosition(_trackHost).X);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _trackHost.ReleaseMouseCapture();
        e.Handled = true;
        ValueCommitted?.Invoke(_value);
    }

    private void Apply(double x)
    {
        double usable = _trackWidth - KnobSize;
        double pos = Math.Clamp(x - KnobSize / 2.0, 0, usable);
        int v = (int)Math.Round(pos / usable * 100);
        if (v != _value)
        {
            _value = v;
            ValueChanged?.Invoke(v);
        }
        Layout();
    }

    // ---- helpers ----

    private static Color Lift(Color c, int d) =>
        Color.FromArgb(c.A, (byte)Math.Clamp(c.R + d, 0, 255), (byte)Math.Clamp(c.G + d, 0, 255), (byte)Math.Clamp(c.B + d, 0, 255));

    private static Brush VGrad(Color top, Color bottom)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(top, 0));
        b.GradientStops.Add(new GradientStop(bottom, 1));
        b.Freeze();
        return b;
    }
}
