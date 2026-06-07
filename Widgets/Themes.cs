using System.Windows;
using System.Windows.Media;
using Lintel.Models;

namespace Lintel.Widgets;

/// <summary>Visual parameters that distinguish a theme (the resolved, ready-to-use form).</summary>
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
    public bool WidgetDividers;     // thin separators drawn between widgets (Mond)

    public Color? BarTop;           // optional vertical bar gradient, top colour …
    public Color? BarBottom;        // … and bottom colour (e.g. the Windows XP Luna bar)
    public Color? DropdownColor;    // optional explicit dropdown material (overrides the default)
}

/// <summary>
/// The JSON-serializable form of a theme. This is exactly what a user writes in a
/// <c>%AppData%\Lintel\themes\*.json</c> file — all colours are <c>#AARRGGBB</c> strings.
/// </summary>
public sealed class ThemeSpec
{
    public string Name { get; set; } = "Custom";
    public string BubbleIdle { get; set; } = "#14FFFFFF";   // widget background, normal
    public string BubbleHover { get; set; } = "#28FFFFFF";  // widget background, hovered
    public double CornerRadius { get; set; } = 8;           // widget bubble roundness
    public double Padding { get; set; } = 8;                // horizontal padding inside a widget
    public double Spacing { get; set; } = 4;                // gap between widgets
    public double IconSaturation { get; set; } = 1.0;       // 1 = full colour, 0 = grayscale
    public bool Acrylic { get; set; } = false;              // blur the desktop behind the bar
    public string AcrylicTint { get; set; } = "#B0202024";  // tint over the blur
    public bool BottomHighlight { get; set; } = false;      // hairline along the bottom edge
    public bool SeparatedZones { get; set; } = false;       // left/center/right become floating pills
    public string ZoneBackground { get; set; } = "#E61C1C1E";
    public bool WidgetDividers { get; set; } = false;       // draw a divider between every widget
    public string BarTop { get; set; } = "";                // optional bar gradient top colour ("" = none)
    public string BarBottom { get; set; } = "";             // optional bar gradient bottom colour
    public string DropdownColor { get; set; } = "";         // optional explicit dropdown background ("" = auto)

    public ThemeDef ToDef() => new()
    {
        BubbleIdle = Col(BubbleIdle, Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        BubbleHover = Col(BubbleHover, Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
        CornerRadius = CornerRadius,
        Padding = new Thickness(Padding, 0, Padding, 0),
        Spacing = Spacing,
        IconSaturation = Math.Clamp(IconSaturation, 0, 1),
        Acrylic = Acrylic,
        AcrylicTint = Col(AcrylicTint, Color.FromArgb(0xB0, 0x20, 0x20, 0x24)),
        BottomHighlight = BottomHighlight,
        SeparatedZones = SeparatedZones,
        ZoneBackground = Col(ZoneBackground, Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)),
        WidgetDividers = WidgetDividers,
        BarTop = NullCol(BarTop),
        BarBottom = NullCol(BarBottom),
        DropdownColor = NullCol(DropdownColor)
    };

    private static Color Col(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    private static Color? NullCol(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return null; }
    }
}

/// <summary>
/// Theme registry: built-in themes plus any user themes loaded from JSON. Themes are looked up
/// by name so custom themes slot in next to the built-ins everywhere (the picker, settings, etc.).
/// </summary>
public static class Themes
{
    private static ThemeDef PowerLike(bool fluid, bool dividers = false) => new()
    {
        BubbleIdle = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
        CornerRadius = 5,
        Padding = new Thickness(7, 0, 7, 0),
        Spacing = dividers ? 12 : 8,
        IconSaturation = 0.5,
        Acrylic = true,
        AcrylicTint = Color.FromArgb(0xB0, 0x20, 0x20, 0x24),
        BottomHighlight = true,
        FluidDropdowns = fluid,
        WidgetDividers = dividers
    };

    private static ThemeDef SquirclesLike(double corner) => new()
    {
        BubbleIdle = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF),
        CornerRadius = corner,
        Padding = new Thickness(8, 0, 8, 0),
        Spacing = 4,
        IconSaturation = 1.0,
    };

    private static ThemeDef IslandsDef() => new()
    {
        BubbleIdle = Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
        CornerRadius = 8,
        Padding = new Thickness(8, 0, 8, 0),
        Spacing = 4,
        IconSaturation = 1.0,
        SeparatedZones = true,
        ZoneBackground = Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E),
    };

    // Windows XP "Luna Blue": a solid glossy blue gradient bar, bright icons.
    private static ThemeDef WinXP() => new()
    {
        BubbleIdle = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x46, 0xFF, 0xFF, 0xFF),
        CornerRadius = 4,
        Padding = new Thickness(8, 0, 8, 0),
        Spacing = 6,
        IconSaturation = 1.0,
        BarTop = Color.FromRgb(0x3C, 0x81, 0xF3),       // light Luna blue
        BarBottom = Color.FromRgb(0x16, 0x46, 0xC4),    // deep Luna blue
        DropdownColor = Color.FromArgb(0xF2, 0x1E, 0x52, 0xB0),
        BottomHighlight = true,
    };

    // Windows Vista Aero: dark translucent glass (heavy blur), glossy highlight.
    private static ThemeDef WinVista() => new()
    {
        BubbleIdle = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
        CornerRadius = 6,
        Padding = new Thickness(8, 0, 8, 0),
        Spacing = 5,
        IconSaturation = 1.0,
        Acrylic = true,
        AcrylicTint = Color.FromArgb(0xC2, 0x0C, 0x12, 0x1E),
        BottomHighlight = true,
    };

    // Windows 7 Aero: lighter blue-tinted glass.
    private static ThemeDef Win7() => new()
    {
        BubbleIdle = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF),
        CornerRadius = 6,
        Padding = new Thickness(8, 0, 8, 0),
        Spacing = 5,
        IconSaturation = 1.0,
        Acrylic = true,
        AcrylicTint = Color.FromArgb(0xAE, 0x29, 0x4A, 0x78),
        BottomHighlight = true,
    };

    // Built-in themes, resolved as a function of the user's squircle-corner setting.
    private static readonly Dictionary<string, Func<double, ThemeDef>> BuiltIns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Squircles"]     = SquirclesLike,
            ["Power"]         = _ => PowerLike(false),
            ["Islands"]       = _ => IslandsDef(),
            ["Mond"]          = _ => PowerLike(false, dividers: true),
            ["Windows XP"]    = _ => WinXP(),
            ["Windows Vista"] = _ => WinVista(),
            ["Windows 7"]     = _ => Win7(),
            ["Resin"]         = _ => PowerLike(false),   // hidden alias kept for back-compat
        };

    /// <summary>Built-in themes shown in the picker, in order.</summary>
    public static readonly string[] BuiltInOrder =
        { "Squircles", "Power", "Islands", "Mond", "Windows XP", "Windows Vista", "Windows 7" };

    private static Dictionary<string, ThemeSpec> _custom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replace the set of user themes (called after loading/importing JSON files).</summary>
    public static void SetCustom(IEnumerable<ThemeSpec> specs)
    {
        _custom = new Dictionary<string, ThemeSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || BuiltIns.ContainsKey(s.Name)) continue;
            _custom[s.Name] = s;   // last one wins on name clash
        }
    }

    public static bool IsBuiltIn(string name) => name != null && BuiltIns.ContainsKey(name);

    /// <summary>All selectable theme names — built-ins first, then custom ones.</summary>
    public static IEnumerable<string> Names() => BuiltInOrder.Concat(_custom.Keys);

    public static string DisplayName(string name) => name;

    /// <summary>Resolve a theme name into its concrete <see cref="ThemeDef"/>.</summary>
    public static ThemeDef Get(string? name, double squircleCorner)
    {
        if (name != null && _custom.TryGetValue(name, out var spec)) return spec.ToDef();
        if (name != null && BuiltIns.TryGetValue(name, out var make)) return make(squircleCorner);
        return SquirclesLike(squircleCorner);
    }

    /// <summary>The theme name a settings object currently selects.</summary>
    public static string NameOf(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.ThemeName) ? s.Theme.ToString() : s.ThemeName;

    /// <summary>Resolve the theme for the given settings.</summary>
    public static ThemeDef Resolve(AppSettings s) => Get(NameOf(s), s.WidgetCornerRadius);
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
