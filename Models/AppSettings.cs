using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lintel.Models;

public enum LintelTheme
{
    /// <summary>Default: each widget on a rounded, filled bubble with colour-coded icons.</summary>
    Squircles,
    /// <summary>Flat PowerToys-style bar: acrylic blur, monochrome-ish icons, tighter spacing.</summary>
    Power,
    /// <summary>Each zone is its own floating rounded bar with gaps between them; fluid dropdowns.</summary>
    Islands,
    /// <summary>Like Power, but with fluid Dynamic-Island-style dropdowns.</summary>
    Resin,
    /// <summary>Squircles layout with fluid dropdowns that stretch out of the bar.</summary>
    Mond
}

public enum VisibilityMode
{
    /// <summary>Bar is always visible and reserves desktop space (like the macOS menu bar).</summary>
    AlwaysOn,

    /// <summary>Bar stays hidden until the cursor is pushed to the very top of the screen.</summary>
    AutoHide,

    /// <summary>Bar floats on top, but hides itself whenever a window is fullscreen or would sit under it.</summary>
    Dynamic
}

/// <summary>
/// User-editable configuration for Lintel. Persisted as JSON in %AppData%\Lintel\settings.json.
/// </summary>
public sealed class AppSettings
{
    public VisibilityMode Mode { get; set; } = VisibilityMode.Dynamic;

    /// <summary>Height of the bar, in device-independent pixels.</summary>
    public double BarHeight { get; set; } = 32;

    // --- Auto-hide timing (all milliseconds) ---

    /// <summary>How long the cursor must rest at the top edge before the bar reveals.</summary>
    public int RevealHoldMs { get; set; } = 120;

    /// <summary>How long the bar stays open after the cursor leaves it.</summary>
    public int HideDelayMs { get; set; } = 700;

    /// <summary>Thickness (px) of the hot zone at the top of the screen that triggers a reveal.</summary>
    public int TriggerZonePx { get; set; } = 2;

    // --- Dynamic mode timing ---

    /// <summary>Grace period before the bar hides once something obstructs it, to avoid flicker.</summary>
    public int DynamicHideDelayMs { get; set; } = 250;

    // --- Animation ---

    /// <summary>Duration of the slide/fade in &amp; out, in milliseconds. 0 = instant.</summary>
    public int AnimationMs { get; set; } = 160;

    // --- Appearance ---

    /// <summary>Bar background as #AARRGGBB.</summary>
    public string BackgroundColor { get; set; } = "#F01C1C1E";

    /// <summary>Primary text/foreground as #AARRGGBB.</summary>
    public string ForegroundColor { get; set; } = "#FFF2F2F7";

    /// <summary>Accent color (separators, highlights) as #AARRGGBB.</summary>
    public string AccentColor { get; set; } = "#FF0A84FF";

    public bool ShowActiveApp { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool Use24HourClock { get; set; } = false;

    /// <summary>Start Lintel automatically when you log in to Windows.</summary>
    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>Which monitor to dock to (0 = primary). Out-of-range falls back to primary.</summary>
    public int MonitorIndex { get; set; } = 0;

    // --- Widget layout (ordered widget keys per zone) ---

    public List<string> LeftWidgets { get; set; } = new() { "activeapp" };
    public List<string> CenterWidgets { get; set; } = new() { "cpu", "ram", "gpu" };
    public List<string> RightWidgets { get; set; } = new() { "mode", "date", "clock", "settings" };

    /// <summary>Corner radius of the widget bubbles (lower = more squarish).</summary>
    public double WidgetCornerRadius { get; set; } = 8;

    /// <summary>Persisted text for the Quick Note widget.</summary>
    public string NoteText { get; set; } = "";

    /// <summary>App Tabs widget: show only the focused window (collapsed) vs all tabs.</summary>
    public bool AppTabsCompressed { get; set; } = false;

    /// <summary>Visual theme for the widgets (built-in enum; kept for back-compat).</summary>
    public LintelTheme Theme { get; set; } = LintelTheme.Squircles;

    /// <summary>Selected theme by name — covers built-ins *and* custom JSON themes. Wins over <see cref="Theme"/>.</summary>
    public string ThemeName { get; set; } = "";

    /// <summary>Allow "command" custom widgets to run their shell command. On by default (your machine, your call).</summary>
    public bool EnableCommandWidgets { get; set; } = true;

    /// <summary>Open widget dropdowns on hover (true) or on click (false).</summary>
    public bool OpenOnHover { get; set; } = true;

    /// <summary>In Dynamic mode, hold the cursor at the top edge this long to reveal the bar over a fullscreen app.</summary>
    public int DynamicRevealHoldMs { get; set; } = 350;

    /// <summary>Optional token budget for the Claude widget's rolling 5-hour window. 0 = unknown (show usage only).</summary>
    public long ClaudeTokenLimit { get; set; } = 0;

    /// <summary>Where the GitHub widget clones repositories. Empty = Desktop.</summary>
    public string CloneTargetFolder { get; set; } = "";

    // ----------------------------------------------------------------------

    [JsonIgnore]
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lintel");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                    return loaded.Clamped();
            }
        }
        catch
        {
            // Corrupt or unreadable config — fall back to defaults rather than crashing.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(Clamped(), JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Best-effort persistence; never let a failed write take the app down.
        }
    }

    /// <summary>Keep user-entered values inside sane bounds.</summary>
    public AppSettings Clamped()
    {
        BarHeight = Math.Clamp(BarHeight, 18, 80);
        RevealHoldMs = Math.Clamp(RevealHoldMs, 0, 5000);
        HideDelayMs = Math.Clamp(HideDelayMs, 0, 10000);
        TriggerZonePx = Math.Clamp(TriggerZonePx, 1, 20);
        DynamicHideDelayMs = Math.Clamp(DynamicHideDelayMs, 0, 5000);
        AnimationMs = Math.Clamp(AnimationMs, 0, 2000);
        MonitorIndex = Math.Max(0, MonitorIndex);
        // Migrate older configs that only stored the enum theme.
        if (string.IsNullOrWhiteSpace(ThemeName)) ThemeName = Theme.ToString();
        return this;
    }

    public AppSettings Clone()
    {
        return (AppSettings)MemberwiseClone();
    }
}
