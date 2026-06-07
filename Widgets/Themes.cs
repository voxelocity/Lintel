using System.Windows;
using System.Windows.Media;
using Lintel.Models;

namespace Lintel.Widgets;

/// <summary>OS-window styling applied to dropdown cards.</summary>
public enum DropdownChrome { None, Luna, Aero }

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
    public bool AeroBlur;           // use the classic clearer Aero blur instead of frosted acrylic
    public bool FrostedGlass;       // real blur via the (blurred) desktop wallpaper behind the bar
    public bool BottomHighlight;    // light hairline along the bottom edge

    public bool SeparatedZones;     // each zone is its own floating bar
    public Color ZoneBackground;    // background of each floating zone (Islands)
    public bool FluidDropdowns;     // springy Dynamic-Island-style menus
    public bool WidgetDividers;     // thin separators drawn between widgets (Mond)

    public Color? BarTop;           // optional vertical bar gradient, top colour …
    public Color? BarBottom;        // … and bottom colour (e.g. the Windows XP Luna bar)
    public Color? DropdownColor;    // optional explicit dropdown material (overrides the default)

    // --- richer cosmetics (themes can change size, material, and add glossy detailing) ---
    public double? BarHeight;       // override the bar height in px (null = use the user's setting)
    public double GlossStrength;    // 0 = none; a glossy reflection across the top half (Aero / Luna)
    public Color? TopEdge;          // bright hairline along the very top edge
    public Color? BottomEdge;       // hairline along the bottom edge (overrides BottomHighlight colour)
    public string? FontFamily;      // theme font, e.g. "Tahoma" for XP
    public Color? BubbleBorder;     // raised-button outline drawn around each widget
    public double BubbleBorderThickness = 1;
    public double BubbleGloss;      // 0 = none; glossy sheen on each widget bubble

    public DropdownChrome Chrome;   // OS-window styling for dropdowns (XP Luna / Vista Aero)
    public Color? LeftIslandTop;    // coloured island over the LEFT zone (e.g. XP's green Start area)
    public Color? LeftIslandBottom;
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
    public bool AeroBlur { get; set; } = false;             // classic clearer Aero blur (vs frosted acrylic)
    public bool FrostedGlass { get; set; } = false;         // real frosted glass via the blurred wallpaper
    public bool BottomHighlight { get; set; } = false;      // hairline along the bottom edge
    public bool SeparatedZones { get; set; } = false;       // left/center/right become floating pills
    public string ZoneBackground { get; set; } = "#E61C1C1E";
    public bool WidgetDividers { get; set; } = false;       // draw a divider between every widget
    public string BarTop { get; set; } = "";                // optional bar gradient top colour ("" = none)
    public string BarBottom { get; set; } = "";             // optional bar gradient bottom colour
    public string DropdownColor { get; set; } = "";         // optional explicit dropdown background ("" = auto)
    public double BarHeight { get; set; } = 0;              // override bar height in px (0 = use global setting)
    public double GlossStrength { get; set; } = 0;          // 0..1 glossy top-half reflection
    public string TopEdge { get; set; } = "";               // bright hairline along the top edge
    public string BottomEdge { get; set; } = "";            // hairline along the bottom edge
    public string FontFamily { get; set; } = "";            // theme font, e.g. "Tahoma"
    public string BubbleBorder { get; set; } = "";          // outline around each widget bubble
    public double BubbleBorderThickness { get; set; } = 1;
    public double BubbleGloss { get; set; } = 0;            // 0..1 glossy sheen on each widget

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
        AeroBlur = AeroBlur,
        FrostedGlass = FrostedGlass,
        BottomHighlight = BottomHighlight,
        SeparatedZones = SeparatedZones,
        ZoneBackground = Col(ZoneBackground, Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)),
        WidgetDividers = WidgetDividers,
        BarTop = NullCol(BarTop),
        BarBottom = NullCol(BarBottom),
        DropdownColor = NullCol(DropdownColor),
        BarHeight = BarHeight > 0 ? BarHeight : (double?)null,
        GlossStrength = Math.Clamp(GlossStrength, 0, 1),
        TopEdge = NullCol(TopEdge),
        BottomEdge = NullCol(BottomEdge),
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? null : FontFamily,
        BubbleBorder = NullCol(BubbleBorder),
        BubbleBorderThickness = BubbleBorderThickness,
        BubbleGloss = Math.Clamp(BubbleGloss, 0, 1)
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
        AcrylicTint = Color.FromArgb(0x8E, 0x18, 0x18, 0x1E),   // dark, ~half-transparent over the live blur
        FrostedGlass = true,
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

    // Windows XP "Luna Blue": solid glossy blue gradient, bright icons, raised glassy buttons, Tahoma.
    private static ThemeDef WinXP() => new()
    {
        BubbleIdle = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
        CornerRadius = 4,
        Padding = new Thickness(9, 0, 9, 0),
        Spacing = 6,
        IconSaturation = 1.0,
        BarTop = Color.FromRgb(0x3E, 0x86, 0xF0),       // light Luna blue
        BarBottom = Color.FromRgb(0x10, 0x3C, 0xBE),    // deep Luna blue
        DropdownColor = Color.FromArgb(0xF4, 0x1B, 0x4F, 0xB0),
        BarHeight = 30,
        GlossStrength = 0.55,
        TopEdge = Color.FromArgb(0xCC, 0xBF, 0xD8, 0xFF),   // bright Luna shine line
        BottomEdge = Color.FromArgb(0x70, 0x06, 0x1E, 0x6E),
        FontFamily = "Tahoma",
        BubbleBorder = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF),
        BubbleGloss = 0.5,
        Chrome = DropdownChrome.Luna,
        LeftIslandTop = Color.FromRgb(0x7A, 0xB8, 0x4A),     // Luna green (XP Start area)
        LeftIslandBottom = Color.FromRgb(0x4E, 0x8A, 0x1F),
    };

    // Windows Vista Aero: smoky translucent black glass (clear Aero blur) with a glossy reflection.
    private static ThemeDef WinVista() => new()
    {
        BubbleIdle = Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF),
        CornerRadius = 6,
        Padding = new Thickness(9, 0, 9, 0),
        Spacing = 5,
        IconSaturation = 1.0,
        Acrylic = true,
        AeroBlur = true,
        FrostedGlass = true,
        AcrylicTint = Color.FromArgb(0x6E, 0x0C, 0x12, 0x1E),   // translucent dark glass — desktop shows through
        DropdownColor = Color.FromArgb(0xF0, 0x12, 0x17, 0x22), // keep dropdowns readable
        BarHeight = 30,
        GlossStrength = 0.34,
        TopEdge = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
        FontFamily = "Segoe UI",
        BubbleBorder = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
        BubbleGloss = 0.30,
        Chrome = DropdownChrome.Aero,
    };

    // Windows 7 Aero: thin, very see-through dark glass with a soft blur and minimal gloss.
    private static ThemeDef Win7() => new()
    {
        BubbleIdle = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF),
        BubbleHover = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
        CornerRadius = 6,
        Padding = new Thickness(10, 0, 10, 0),
        Spacing = 6,
        IconSaturation = 1.0,
        Acrylic = true,
        AeroBlur = true,
        FrostedGlass = true,
        AcrylicTint = Color.FromArgb(0x34, 0x12, 0x1C, 0x32),   // dark blue, very translucent — wallpaper shows through
        DropdownColor = Color.FromArgb(0xEC, 0x14, 0x1E, 0x30), // readable dropdown panel
        BarHeight = 38,
        GlossStrength = 0.12,                                   // just a hint of sheen, not a gradient
        TopEdge = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
        BottomEdge = Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF),    // glassy highlight line along the bottom
        FontFamily = "Segoe UI",
        BubbleBorder = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
        BubbleGloss = 0.16,
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
