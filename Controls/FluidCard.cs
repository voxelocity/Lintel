using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lintel.Controls;

/// <summary>
/// A dropdown container whose top edge merges with the bar: the two top corners flare
/// outward with concave "shoulder" curves so the panel looks like it is being pulled down
/// out of the bar (Dynamic-Island style), rather than a separate floating window.
/// </summary>
public sealed class FluidCard : Decorator
{
    public Brush Fill { get; set; } = Brushes.Black;
    public double BodyRadius { get; set; } = 16;     // bottom corners
    public double Shoulder { get; set; } = 20;       // flare connecting to the bar
    public Thickness ContentPadding { get; set; } = new(14);

    protected override Size MeasureOverride(Size constraint)
    {
        double padW = Shoulder * 2 + ContentPadding.Left + ContentPadding.Right;
        double padH = Shoulder + ContentPadding.Top + ContentPadding.Bottom;
        if (Child != null)
        {
            Child.Measure(new Size(Math.Max(0, constraint.Width - padW), double.PositiveInfinity));
            return new Size(Child.DesiredSize.Width + padW, Child.DesiredSize.Height + padH);
        }
        return new Size(padW, padH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(
            Shoulder + ContentPadding.Left,
            Shoulder + ContentPadding.Top,
            Math.Max(0, finalSize.Width - Shoulder * 2 - ContentPadding.Left - ContentPadding.Right),
            Math.Max(0, finalSize.Height - Shoulder - ContentPadding.Top - ContentPadding.Bottom)));
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double sh = Shoulder, rb = Math.Min(BodyRadius, (w - 2 * sh) / 2);
        double L = sh, R = w - sh;

        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Point(0, 0), true, true);                                                              // top-left tip (at the bar)
            c.ArcTo(new Point(L, sh), new Size(sh, sh), 0, false, SweepDirection.Clockwise, true, false);            // left shoulder (flares down)
            c.LineTo(new Point(L, h - rb), true, false);                                                            // left wall
            c.ArcTo(new Point(L + rb, h), new Size(rb, rb), 0, false, SweepDirection.Counterclockwise, true, false);// bottom-left
            c.LineTo(new Point(R - rb, h), true, false);                                                            // bottom
            c.ArcTo(new Point(R, h - rb), new Size(rb, rb), 0, false, SweepDirection.Counterclockwise, true, false);// bottom-right
            c.LineTo(new Point(R, sh), true, false);                                                                // right wall
            c.ArcTo(new Point(w, 0), new Size(sh, sh), 0, false, SweepDirection.Clockwise, true, false);            // right shoulder
            // implicit close: straight top edge (along the bar) from (w,0) back to (0,0)
        }
        geo.Freeze();
        dc.DrawGeometry(Fill, null, geo);
    }
}
