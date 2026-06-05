using System.Windows;
using System.Windows.Media;

namespace Lintel.Controls;

/// <summary>
/// Draws a metric's rolling history as a colour-coded area chart: the line/fill shade
/// shifts from green (low) to red (high), with light gridlines and the latest value marked.
/// </summary>
public sealed class HistoryGraph : FrameworkElement
{
    private double[] _data = Array.Empty<double>();

    public void SetData(double[] data)
    {
        _data = data ?? Array.Empty<double>();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Background
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0)), null, new Rect(0, 0, w, h));

        // Gridlines at 25/50/75%
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
        grid.Freeze();
        for (int i = 1; i < 4; i++)
        {
            double y = h * i / 4.0;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        if (_data.Length < 2) return;

        double n = _data.Length;
        Func<int, double> X = i => i / (n - 1) * w;
        Func<double, double> Y = v => h - Math.Clamp(v, 0, 100) / 100.0 * h;

        // Filled area
        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(new Point(0, Y(_data[0])), true, false);
            for (int i = 1; i < _data.Length; i++)
                ctx.LineTo(new Point(X(i), Y(_data[i])), true, false);
            ctx.LineTo(new Point(w, h), true, false);
        }
        fill.Freeze();

        double latest = _data[^1];
        var topColor = RingGauge.ColorFor(latest);
        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x80, topColor.R, topColor.G, topColor.B), 0),
                new GradientStop(Color.FromArgb(0x10, topColor.R, topColor.G, topColor.B), 1)
            }
        };
        grad.Freeze();
        dc.DrawGeometry(grad, null, fill);

        // Line on top, colour-coded per segment
        for (int i = 1; i < _data.Length; i++)
        {
            var c = RingGauge.ColorFor(_data[i]);
            var pen = new Pen(new SolidColorBrush(c), 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawLine(pen, new Point(X(i - 1), Y(_data[i - 1])), new Point(X(i), Y(_data[i])));
        }

        // Latest point marker
        var dot = new SolidColorBrush(topColor);
        dot.Freeze();
        dc.DrawEllipse(dot, null, new Point(w, Y(latest)), 2.6, 2.6);
    }
}
