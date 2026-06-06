using System.Windows;
using System.Windows.Media;
using Lintel.Models;

namespace Lintel.Widgets;

/// <summary>Visual parameters that distinguish a theme.</summary>
public sealed class ThemeDef
{
    public Color BubbleIdle;       // per-widget background
    public Color BubbleHover;
    public double CornerRadius;
    public Thickness Padding;
    public double Spacing;          // gap between widgets
    public double IconSaturation;   // 1 = full colour, 0 = grayscale

    public bool Acrylic;            // blur what's behind the bar
    public Color AcrylicTint;       // tint over the blur (A = tint strength)
    public bool BottomHighlight;    // light hairline along the bottom edge

    public bool SeparatedZones;     // each zone is its own floating bar
    public Color ZoneBackground;    // background of each floating zone (Islands)
    public bool FluidDropdowns;     // springy Dynamic-Island-style menus
}

public static class Themes
{
    private static ThemeDef PowerLike(bool fluid) => new()
    {
        BubbleIdle = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
        CornerRadius = 5,
        Padding = new Thickness(7, 0, 7, 0),
        Spacing = 8,
        IconSaturation = 0.5,
        Acrylic = true,
        AcrylicTint = Color.FromArgb(0xB0, 0x20, 0x20, 0x24),
        BottomHighlight = true,
        FluidDropdowns = fluid
    };

    private static ThemeDef SquirclesLike(double corner, bool fluid) => new()
    {
        BubbleIdle = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF),
        CornerRadius = corner,
        Padding = new Thickness(8, 0, 8, 0),
        Spacing = 4,
        IconSaturation = 1.0,
        Acrylic = false,
        BottomHighlight = false,
        FluidDropdowns = fluid
    };

    public static ThemeDef For(LintelTheme theme, double squircleCorner) => theme switch
    {
        LintelTheme.Power => PowerLike(false),
        LintelTheme.Resin => PowerLike(false),                       // fluid dropdowns removed for now
        LintelTheme.Mond => SquirclesLike(squircleCorner, false),    // fluid dropdowns removed for now
        LintelTheme.Islands => new ThemeDef
        {
            BubbleIdle = Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
            BubbleHover = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
            CornerRadius = 8,
            Padding = new Thickness(8, 0, 8, 0),
            Spacing = 4,
            IconSaturation = 1.0,
            Acrylic = false,
            BottomHighlight = false,
            SeparatedZones = true,
            ZoneBackground = Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E),
            FluidDropdowns = false   // connected dropdown clips against the floating pills
        },
        _ => SquirclesLike(squircleCorner, false)
    };

    /// <summary>Themes offered in the UI (fluid-dropdown themes are hidden for now).</summary>
    public static readonly LintelTheme[] Selectable =
        { LintelTheme.Squircles, LintelTheme.Power, LintelTheme.Islands };

    public static string DisplayName(LintelTheme t) => t switch
    {
        LintelTheme.Power => "Power",
        LintelTheme.Islands => "Islands",
        LintelTheme.Resin => "Resin",
        LintelTheme.Mond => "Mond",
        _ => "Squircles"
    };
}

public static class ColorUtil
{
    /// <summary>Blend a colour toward its own luminance. sat=1 keeps it, sat=0 is grayscale.</summary>
    public static Color Desaturate(Color c, double sat)
    {
        sat = Math.Clamp(sat, 0, 1);
        double gray = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        byte Mix(byte v) => (byte)Math.Clamp(v + (gray - v) * (1 - sat), 0, 255);
        return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }
}
