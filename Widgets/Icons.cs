using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Lintel.Widgets;

/// <summary>
/// Code-built vector icons (24×24 space) for each widget, so every gauge and tool reads
/// distinctly at small sizes without shipping font/image assets.
/// </summary>
public static class Icons
{
    public static Geometry? Get(string key) => key switch
    {
        "cpu" => Cpu(),
        "ram" => Ram(),
        "gpu" => Gpu(),
        "disk" => Disk(),
        "net" => Net(),
        "battery" => Battery(),
        "note" => Note(),
        "windows" => Windows(),
        "workspaces" => Workspaces(),
        "settings" => Gear(),
        "media" => Media(),
        _ => null
    };

    private static Geometry Media()
    {
        var g = new GeometryGroup();
        g.Children.Add(E(7.5, 17.5, 2.8));                              // note head
        g.Children.Add(R(9.3, 6, 1.7, 12));                            // stem
        g.Children.Add(Geometry.Parse("M9.3,6 L17,4 L17,7.5 L9.3,9.5 Z")); // flag
        return Freeze(g);
    }

    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
    private static Point Polar(double cx, double cy, double r, double ang) => new(cx + r * Math.Cos(ang), cy + r * Math.Sin(ang));
    private static RectangleGeometry R(double x, double y, double w, double h, double r = 1) =>
        new(new Rect(x, y, w, h), r, r);
    private static EllipseGeometry E(double cx, double cy, double r) => new(new Point(cx, cy), r, r);

    // Chip with pins
    private static Geometry Cpu()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(R(6, 6, 12, 12, 2));
        double[] p = { 8.5, 11, 13.5 };
        foreach (var v in p)
        {
            g.Children.Add(R(v, 3, 2, 3));   // top
            g.Children.Add(R(v, 18, 2, 3));  // bottom
            g.Children.Add(R(3, v, 3, 2));   // left
            g.Children.Add(R(18, v, 3, 2));  // right
        }
        return Freeze(g);
    }

    // Memory stick with legs
    private static Geometry Ram()
    {
        var g = new GeometryGroup();
        g.Children.Add(R(3, 7, 18, 8, 1.5));
        g.Children.Add(R(6, 15, 2, 3));
        g.Children.Add(R(16, 15, 2, 3));
        return Freeze(g);
    }

    // Graphics card with a circular fan (hole)
    private static Geometry Gpu()
    {
        var card = R(2, 7, 20, 10, 1.5);
        var fan = E(12, 12, 3.4);
        return Freeze(new CombinedGeometry(GeometryCombineMode.Exclude, card, fan));
    }

    // Disk platter with centre hole
    private static Geometry Disk()
    {
        var outer = E(12, 12, 9);
        var hole = E(12, 12, 2.2);
        return Freeze(new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole));
    }

    // Up / down network arrows
    private static Geometry Net()
    {
        var g = new GeometryGroup();
        g.Children.Add(Geometry.Parse("M7,3 L11,8 L8.5,8 L8.5,13 L5.5,13 L5.5,8 L3,8 Z"));   // up
        g.Children.Add(Geometry.Parse("M17,21 L21,16 L18.5,16 L18.5,11 L15.5,11 L15.5,16 L13,16 Z")); // down
        return Freeze(g);
    }

    // Battery outline + terminal + charge level
    private static Geometry Battery()
    {
        var frame = new CombinedGeometry(GeometryCombineMode.Exclude, R(3, 8, 16, 9, 2.5), R(5, 10, 12, 5, 1));
        var g = new GeometryGroup();
        g.Children.Add(frame);
        g.Children.Add(R(19.5, 10.5, 2, 4, 1)); // nub
        g.Children.Add(R(6, 11, 6, 3, 0.5));    // charge level
        return Freeze(g);
    }

    // Note paper with text lines
    private static Geometry Note()
    {
        var paper = new CombinedGeometry(GeometryCombineMode.Exclude, R(5, 3, 14, 18, 2.5), R(7, 5, 10, 14, 1));
        var g = new GeometryGroup();
        g.Children.Add(paper);
        g.Children.Add(R(8, 8, 8, 1.4));
        g.Children.Add(R(8, 11, 8, 1.4));
        g.Children.Add(R(8, 14, 5, 1.4));
        return Freeze(g);
    }

    // App grid (windows / tabs)
    private static Geometry Windows()
    {
        var g = new GeometryGroup();
        g.Children.Add(R(4, 4, 7, 7, 1.5));
        g.Children.Add(R(13, 4, 7, 7, 1.5));
        g.Children.Add(R(4, 13, 7, 7, 1.5));
        g.Children.Add(R(13, 13, 7, 7, 1.5));
        return Freeze(g);
    }

    // Workspaces (stacked spaces)
    private static Geometry Workspaces()
    {
        var g = new GeometryGroup();
        g.Children.Add(R(3, 7, 8, 10, 1.5));
        g.Children.Add(R(13, 7, 8, 10, 1.5));
        return Freeze(g);
    }

    // Flat solid gear (toothed disc with a centre hole)
    private static Geometry Gear()
    {
        const int teeth = 8;
        double cx = 12, cy = 12, rOut = 11, rIn = 8.4, hole = 4.4;
        double pitch = Math.PI * 2 / teeth;
        double tw = pitch * 0.32; // half-width of each tooth tip

        var pts = new List<Point>();
        for (int i = 0; i < teeth; i++)
        {
            double c = i * pitch;
            pts.Add(Polar(cx, cy, rIn, c - tw));
            pts.Add(Polar(cx, cy, rOut, c - tw));
            pts.Add(Polar(cx, cy, rOut, c + tw));
            pts.Add(Polar(cx, cy, rIn, c + tw));
        }

        var fig = new PathFigure { StartPoint = pts[0], IsClosed = true };
        for (int i = 1; i < pts.Count; i++) fig.Segments.Add(new LineSegment(pts[i], true));
        var outline = new PathGeometry();
        outline.Figures.Add(fig);

        return Freeze(new CombinedGeometry(GeometryCombineMode.Exclude, outline, E(cx, cy, hole)));
    }
}
