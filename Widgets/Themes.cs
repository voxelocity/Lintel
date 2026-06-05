using System.Windows;
using System.Windows.Media;
using Lintel.Models;

namespace Lintel.Widgets;

/// <summary>Visual parameters that distinguish a theme.</summary>
public sealed class ThemeDef
{
    public Color BubbleIdle;    // per-widget background
    public Color BubbleHover;
    public double CornerRadius;
    public Thickness Padding;
    public double Spacing;       // gap between widgets
    public bool ColoredIcons;    // true = signature colours, false = monochrome
}

public static class Themes
{
    public static ThemeDef For(LintelTheme theme, double squircleCorner) => theme switch
    {
        LintelTheme.Power => new ThemeDef
        {
            BubbleIdle = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),  // flat: no bubble
            BubbleHover = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
            CornerRadius = 5,
            Padding = new Thickness(7, 0, 7, 0),
            Spacing = 9,
            ColoredIcons = false
        },
        _ => new ThemeDef
        {
            BubbleIdle = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            BubbleHover = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF),
            CornerRadius = squircleCorner,
            Padding = new Thickness(8, 0, 8, 0),
            Spacing = 4,
            ColoredIcons = true
        }
    };

    public static string DisplayName(LintelTheme t) => t switch
    {
        LintelTheme.Power => "Power",
        _ => "Squircles"
    };
}
