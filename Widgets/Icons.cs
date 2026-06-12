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
        "claude" => Claude(),
        "github" => GitHub(),
        "todo" => Todo(),
        "pomodoro" => Pomodoro(),
        "weather" => Weather(),
        "stocks" => Stocks(),
        "volume" => Volume(),
        "brightness" => Brightness(),
        "tictactoe" => TicTacToe(),
        "launcher" => Launcher(),
        "start" => Start(),
        "tray" => Tray(),
        _ => null
    };

    // 3×3 app grid (application launcher)
    private static Geometry Launcher()
    {
        var g = new GeometryGroup();
        double[] c = { 4, 10, 16 };
        foreach (var y in c) foreach (var x in c) g.Children.Add(R(x, y, 4, 4, 1.2));
        return Freeze(g);
    }

    // Windows four-pane flag (Start)
    private static Geometry Start()
    {
        var g = new GeometryGroup();
        g.Children.Add(R(3.5, 4, 7.4, 7.4, 0.8));
        g.Children.Add(R(13.1, 4, 7.4, 7.4, 0.8));
        g.Children.Add(R(3.5, 12.6, 7.4, 7.4, 0.8));
        g.Children.Add(R(13.1, 12.6, 7.4, 7.4, 0.8));
        return Freeze(g);
    }

    // Up-chevron (show hidden tray icons)
    private static Geometry Tray() => Freeze(Geometry.Parse("M5,15.5 L12,8.5 L19,15.5 L16.9,17.6 L12,12.7 L7.1,17.6 Z"));

    // Checklist
    private static Geometry Todo()
    {
        var g = new GeometryGroup();
        g.Children.Add(Geometry.Parse("M3,5 L5,7 L8,3.5"));       // tick
        g.Children.Add(R(11, 4, 10, 2));
        g.Children.Add(Geometry.Parse("M3,11 L5,13 L8,9.5"));
        g.Children.Add(R(11, 10, 10, 2));
        g.Children.Add(R(3.5, 16.5, 4, 4, 0.8));                 // empty box
        g.Children.Add(R(11, 16, 10, 2));
        return Freeze(g);
    }

    // Tomato timer
    private static Geometry Pomodoro()
    {
        var g = new GeometryGroup();
        g.Children.Add(E(12, 13.5, 8));
        g.Children.Add(Geometry.Parse("M12,5 C10,3 8,3.5 8.5,5.5 C10.5,5 11,5.5 12,6 C13,5.5 13.5,5 15.5,5.5 C16,3.5 14,3 12,5 Z")); // leaf
        g.Children.Add(R(11.4, 4, 1.2, 2.5, 0.4));   // stem
        return Freeze(g);
    }

    // Sun behind a cloud
    private static Geometry Weather()
    {
        var sun = E(8, 8, 3.6);
        var cloud = new GeometryGroup();
        cloud.Children.Add(E(10, 15, 4));
        cloud.Children.Add(E(15, 14, 5));
        cloud.Children.Add(E(18, 16, 3.4));
        cloud.Children.Add(R(9, 15.5, 10, 4, 0));
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(sun);
        g.Children.Add(cloud);
        return Freeze(g);
    }

    // Upward trend line + bars
    private static Geometry Stocks()
    {
        var g = new GeometryGroup();
        g.Children.Add(R(3, 14, 3, 6, 0.6));
        g.Children.Add(R(8, 10, 3, 10, 0.6));
        g.Children.Add(R(13, 12, 3, 8, 0.6));
        g.Children.Add(R(18, 6, 3, 14, 0.6));
        g.Children.Add(Geometry.Parse("M3,9 L9,6 L14,8 L21,2 L21,4.5 L14,10 L9,8 L3,11 Z")); // arrow line
        return Freeze(g);
    }

    // Speaker
    private static Geometry Volume()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(Geometry.Parse("M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z"));
        g.Children.Add(new CombinedGeometry(GeometryCombineMode.Exclude, E(15, 12, 4.5), E(15, 12, 2.8)));
        g.Children.Add(new CombinedGeometry(GeometryCombineMode.Exclude, E(15, 12, 7.2), E(15, 12, 5.6)));
        return Freeze(g);
    }

    // Sun with rays
    private static Geometry Brightness()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(E(12, 12, 4.2));
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            var p1 = Polar(12, 12, 7, a);
            var p2 = Polar(12, 12, 10, a);
            var n = new Vector(-Math.Sin(a), Math.Cos(a)) * 0.9;
            var fig = new PathFigure { StartPoint = new Point(p1.X + n.X, p1.Y + n.Y), IsClosed = true };
            fig.Segments.Add(new LineSegment(new Point(p2.X + n.X, p2.Y + n.Y), true));
            fig.Segments.Add(new LineSegment(new Point(p2.X - n.X, p2.Y - n.Y), true));
            fig.Segments.Add(new LineSegment(new Point(p1.X - n.X, p1.Y - n.Y), true));
            var pg = new PathGeometry(); pg.Figures.Add(fig);
            g.Children.Add(pg);
        }
        return Freeze(g);
    }

    // Tic-tac-toe grid
    private static Geometry TicTacToe()
    {
        var g = new GeometryGroup();
        g.Children.Add(R(9, 3, 1.6, 18, 0.6));
        g.Children.Add(R(13.4, 3, 1.6, 18, 0.6));
        g.Children.Add(R(3, 9, 18, 1.6, 0.6));
        g.Children.Add(R(3, 13.4, 18, 1.6, 0.6));
        return Freeze(g);
    }

    // Anthropic-style sunburst mark
    private static Geometry Claude()
    {
        const double cx = 12, cy = 12;
        var g = new GeometryGroup();
        int rays = 12;
        for (int i = 0; i < rays; i++)
        {
            double a = i * (Math.PI * 2 / rays) - Math.PI / 2;
            var p1 = Polar(cx, cy, 2.6, a);
            var p2 = Polar(cx, cy, 10.5, a);
            // widen the ray into a thin wedge
            var n = new Vector(-Math.Sin(a), Math.Cos(a)) * 1.15;
            var wedge = new PathFigure { StartPoint = new Point(p1.X + n.X * 0.4, p1.Y + n.Y * 0.4), IsClosed = true };
            wedge.Segments.Add(new LineSegment(new Point(p2.X + n.X, p2.Y + n.Y), true));
            wedge.Segments.Add(new LineSegment(new Point(p2.X - n.X, p2.Y - n.Y), true));
            wedge.Segments.Add(new LineSegment(new Point(p1.X - n.X * 0.4, p1.Y - n.Y * 0.4), true));
            var wg = new PathGeometry(); wg.Figures.Add(wedge);
            g.Children.Add(wg);
        }
        return Freeze(g);
    }

    // GitHub Octocat silhouette: eared head + body, with small eyes punched out.
    private static Geometry GitHub()
    {
        var silhouette = new GeometryGroup { FillRule = FillRule.Nonzero };
        silhouette.Children.Add(E(12, 11.5, 7.3));                     // head
        silhouette.Children.Add(Poly((6.2, 6.8), (8.6, 3.4), (10.4, 7.2)));  // left ear
        silhouette.Children.Add(Poly((17.8, 6.8), (15.4, 3.4), (13.6, 7.2))); // right ear
        silhouette.Children.Add(R(6.5, 14.5, 11, 6.5, 3));            // body/tentacles

        var eyes = new GeometryGroup();
        eyes.Children.Add(E(9.6, 11, 1.25));
        eyes.Children.Add(E(14.4, 11, 1.25));

        return Freeze(new CombinedGeometry(GeometryCombineMode.Exclude, silhouette, eyes));
    }

    private static Geometry Poly(params (double x, double y)[] pts)
    {
        var fig = new PathFigure { StartPoint = new Point(pts[0].x, pts[0].y), IsClosed = true };
        for (int i = 1; i < pts.Length; i++) fig.Segments.Add(new LineSegment(new Point(pts[i].x, pts[i].y), true));
        var pg = new PathGeometry(); pg.Figures.Add(fig);
        return pg;
    }

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
