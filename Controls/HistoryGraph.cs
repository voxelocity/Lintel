using System.Windows;
using System.Windows.Media;

namespace Lintel.Controls;

public enum GraphStyle { Area, Bars, Line }

/// <summary>
/// Draws a metric's rolling history. The visual style varies per metric — smooth filled
/// <see cref="GraphStyle.Area"/> for steady loads (CPU/RAM/GPU), discrete
/// <see cref="GraphStyle.Bars"/> for bursty I/O (disk/network), and a clean
/// <see cref="GraphStyle.Line"/> for slow-moving values (battery). Colour is load-coded.
/// </summary>
public sealed class HistoryGraph : FrameworkElement
{
    private double[] _data = Array.Empty<double>();

    public GraphStyle Kind { get; set; } = GraphStyle.Area;

    /// <summary>Optional fixed accent; when null, segments are coloured by load.</summary>
    public Color? Accent { get; set; }

    public void SetData(double[] data)
    {
        _data = data ?? Array.Empty<double>();
        InvalidateVisual();
    }

    private Color ColorAt(double v) => Accent ?? RingGauge.ColorFor(v);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
        grid.Freeze();
        for (int i = 1; i < 4; i++)
        {
            double y = h * i / 4.0;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        if (_data.Length < 1) return;

        switch (Kind)
        {
            case GraphStyle.Bars: RenderBars(dc, w, h); break;
            case GraphStyle.Line: RenderLine(dc, w, h); break;
            default: RenderArea(dc, w, h); break;
        }
    }

    private double X(int i, double w) => _data.Length < 2 ? w : i / (double)(_data.Length - 1) * w;
    private static double Y(double v, double h) => h - Math.Clamp(v, 0, 100) / 100.0 * h;

    private void RenderArea(DrawingContext dc, double w, double h)
    {
        if (_data.Length < 2) return;
        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(new Point(0, Y(_data[0], h)), true, false);
            for (int i = 1; i < _data.Length; i++)
                ctx.LineTo(new Point(X(i, w), Y(_data[i], h)), true, false);
            ctx.LineTo(new Point(w, h), true, false);
        }
        fill.Freeze();

        double latest = _data[^1];
        var top = ColorAt(latest);
        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x80, top.R, top.G, top.B), 0),
                new GradientStop(Color.FromArgb(0x10, top.R, top.G, top.B), 1)
            }
        };
        grad.Freeze();
        dc.DrawGeometry(grad, null, fill);

        for (int i = 1; i < _data.Length; i++)
        {
            var pen = new Pen(new SolidColorBrush(ColorAt(_data[i])), 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawLine(pen, new Point(X(i - 1, w), Y(_data[i - 1], h)), new Point(X(i, w), Y(_data[i], h)));
        }
        var dot = new SolidColorBrush(top); dot.Freeze();
        dc.DrawEllipse(dot, null, new Point(w, Y(latest, h)), 2.6, 2.6);
    }

    private void RenderBars(DrawingContext dc, double w, double h)
    {
        int n = _data.Length;
        double slot = w / Math.Max(1, n);
        double barW = Math.Max(1.5, slot * 0.62);
        for (int i = 0; i < n; i++)
        {
            double v = _data[i];
            double bh = Math.Clamp(v, 0, 100) / 100.0 * h;
            double x = i * slot + (slot - barW) / 2.0;
            var c = ColorAt(v);
            var brush = new SolidColorBrush(Color.FromArgb(i == n - 1 ? (byte)0xFF : (byte)0xCC, c.R, c.G, c.B));
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, new Rect(x, h - bh, barW, bh), barW / 2.5, barW / 2.5);
        }
    }

    private void RenderLine(DrawingContext dc, double w, double h)
    {
        if (_data.Length < 2)
        {
            if (_data.Length == 1)
            {
                var c0 = ColorAt(_data[0]); var b0 = new SolidColorBrush(c0); b0.Freeze();
                dc.DrawEllipse(b0, null, new Point(w, Y(_data[0], h)), 3, 3);
            }
            return;
        }
        for (int i = 1; i < _data.Length; i++)
        {
            var pen = new Pen(new SolidColorBrush(ColorAt(_data[i])), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawLine(pen, new Point(X(i - 1, w), Y(_data[i - 1], h)), new Point(X(i, w), Y(_data[i], h)));
        }
        var top = ColorAt(_data[^1]); var dot = new SolidColorBrush(top); dot.Freeze();
        dc.DrawEllipse(dot, null, new Point(w, Y(_data[^1], h)), 3, 3);
    }
}
