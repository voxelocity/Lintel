using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Lintel.Services;

/// <summary>
/// Builds a "frosted glass" backdrop by laying out the desktop wallpaper exactly as Windows does,
/// cropping the strip behind the bar. The caller then blurs it. This is the reliable way to get a
/// frosted look on Win11, where the OS blur APIs no longer work for a non-activating tool window.
/// (It blurs the wallpaper, not live windows behind the bar — a deliberate trade-off.)
/// </summary>
public static class WallpaperFrost
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint uParam, StringBuilder vParam, uint winIni);
    private const uint SPI_GETDESKWALLPAPER = 0x0073;

    public static string? WallpaperPath()
    {
        try
        {
            var sb = new StringBuilder(600);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0 && File.Exists(sb.ToString()))
                return sb.ToString();
        }
        catch { }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string p && File.Exists(p)) return p;
        }
        catch { }
        return null;
    }

    /// <summary>The top strip of the wallpaper as laid out on the given monitor (device pixels), or null.</summary>
    public static BitmapSource? BuildStrip(double monitorWidthPx, double monitorHeightPx, double stripHeightPx)
    {
        var path = WallpaperPath();
        if (path == null || monitorWidthPx < 1 || stripHeightPx < 1) return null;

        BitmapImage img;
        try
        {
            img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
        }
        catch { return null; }

        double iw = img.PixelWidth, ih = img.PixelHeight;
        if (iw < 1 || ih < 1) return null;

        var (style, tile) = ReadStyle();
        var bg = ReadBackgroundColor();

        // Where the wallpaper sits inside the full monitor (we only render the top strip of that).
        Rect dest;
        if (tile)
            dest = new Rect(0, 0, iw, ih);   // handled specially below
        else
        {
            double scale = style switch
            {
                WStyle.Stretch => 0,                                       // independent X/Y
                WStyle.Fit => Math.Min(monitorWidthPx / iw, monitorHeightPx / ih),
                WStyle.Center => 1,
                _ => Math.Max(monitorWidthPx / iw, monitorHeightPx / ih),  // Fill / Span
            };
            if (style == WStyle.Stretch)
                dest = new Rect(0, 0, monitorWidthPx, monitorHeightPx);
            else
            {
                double dw = iw * scale, dh = ih * scale;
                dest = new Rect((monitorWidthPx - dw) / 2, (monitorHeightPx - dh) / 2, dw, dh);
            }
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, monitorWidthPx, stripHeightPx));
            if (tile)
            {
                for (double y = 0; y < stripHeightPx; y += ih)
                    for (double x = 0; x < monitorWidthPx; x += iw)
                        dc.DrawImage(img, new Rect(x, y, iw, ih));
            }
            else
            {
                dc.DrawImage(img, dest);   // RTB clips to the strip height
            }
        }

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(monitorWidthPx), (int)Math.Ceiling(stripHeightPx), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private enum WStyle { Fill, Fit, Stretch, Center, Span }

    private static (WStyle, bool tile) ReadStyle()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string s = key?.GetValue("WallpaperStyle") as string ?? "10";
            bool tile = (key?.GetValue("TileWallpaper") as string ?? "0") == "1";
            var st = s switch
            {
                "0" => WStyle.Center,
                "2" => WStyle.Stretch,
                "6" => WStyle.Fit,
                "22" => WStyle.Span,
                _ => WStyle.Fill,   // "10"
            };
            return (st, tile && s == "0");
        }
        catch { return (WStyle.Fill, false); }
    }

    private static Color ReadBackgroundColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            if (key?.GetValue("Background") is string s)
            {
                var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 3) return Color.FromRgb(byte.Parse(p[0]), byte.Parse(p[1]), byte.Parse(p[2]));
            }
        }
        catch { }
        return Colors.Black;
    }
}
