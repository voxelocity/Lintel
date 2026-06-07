namespace Lintel.Widgets;

public enum WidgetKind { Gauge, Clock, Date, ActiveApp, Mode, Settings, Note, Windows, Workspaces, Load, Media, Claude, GitHub, Custom }

/// <summary>
/// The JSON-serializable form of a user widget. Drop one of these in
/// <c>%AppData%\Lintel\widgets\*.json</c> (or import it from the UI) and it shows up in the Add menu.
/// </summary>
public sealed class CustomWidgetSpec
{
    /// <summary>Unique id used to save the widget in your layout. Defaults to the file name.</summary>
    public string Key { get; set; } = "";
    /// <summary>Display name (shown in the Add menu + tooltip).</summary>
    public string Name { get; set; } = "Custom";
    /// <summary>"command" = run a command on a timer and show its output; "launcher" = static button.</summary>
    public string Type { get; set; } = "launcher";
    /// <summary>Icon: a built-in key (e.g. "cpu"), a single emoji/character, or raw SVG-style path data. Optional.</summary>
    public string Icon { get; set; } = "";
    /// <summary>Accent colour (#AARRGGBB) for the icon.</summary>
    public string Accent { get; set; } = "#FF0A84FF";
    /// <summary>Static text shown by a launcher (and the initial text for a command widget).</summary>
    public string Label { get; set; } = "";
    /// <summary>command widgets: the shell command to run (executed via <c>cmd /c</c>). Its first line of output is shown.</summary>
    public string Command { get; set; } = "";
    /// <summary>command widgets: how often to re-run, in milliseconds.</summary>
    public int IntervalMs { get; set; } = 5000;
    /// <summary>What clicking opens: a URL, a file, or an application path. Optional.</summary>
    public string OnClick { get; set; } = "";
}

public sealed record WidgetDescriptor(
    string Key, string Name, WidgetKind Kind, string Category, string Glyph = "", CustomWidgetSpec? Custom = null);

/// <summary>The full set of widgets the user can place on the bar — built-ins plus custom JSON widgets.</summary>
public static class WidgetCatalog
{
    private static readonly IReadOnlyList<WidgetDescriptor> BuiltIns = new List<WidgetDescriptor>
    {
        new("cpu",        "CPU",         WidgetKind.Gauge,      "System"),
        new("ram",        "Memory",      WidgetKind.Gauge,      "System"),
        new("gpu",        "GPU",         WidgetKind.Gauge,      "System"),
        new("disk",       "Disk",        WidgetKind.Gauge,      "System"),
        new("net",        "Network",     WidgetKind.Gauge,      "System"),
        new("battery",    "Battery",     WidgetKind.Gauge,      "System"),
        new("load",       "System Load", WidgetKind.Load,       "System"),

        new("media",      "Media",       WidgetKind.Media,      "Media"),

        new("claude",     "Claude",      WidgetKind.Claude,     "Developer"),
        new("github",     "GitHub",      WidgetKind.GitHub,     "Developer"),

        new("activeapp",  "Active App",  WidgetKind.ActiveApp,  "Windows"),
        new("windows",    "App Tabs",    WidgetKind.Windows,    "Windows"),
        new("workspaces", "Workspaces",  WidgetKind.Workspaces, "Windows"),

        new("clock",      "Clock",       WidgetKind.Clock,      "Time & Date"),
        new("date",       "Date",        WidgetKind.Date,       "Time & Date"),

        new("mode",       "Mode",        WidgetKind.Mode,       "Lintel"),
        new("note",       "Quick Note",  WidgetKind.Note,       "Lintel"),
        new("settings",   "Settings",    WidgetKind.Settings,   "Lintel"),
    };

    private static List<WidgetDescriptor> _custom = new();

    /// <summary>Replace the set of custom widgets (called after loading/importing JSON files).</summary>
    public static void SetCustom(IEnumerable<CustomWidgetSpec> specs)
    {
        var list = new List<WidgetDescriptor>();
        var seen = new HashSet<string>(BuiltIns.Select(b => b.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
        {
            if (string.IsNullOrWhiteSpace(s.Key) || !seen.Add(s.Key)) continue;   // skip blank/duplicate keys
            list.Add(new WidgetDescriptor(s.Key, s.Name, WidgetKind.Custom, "Custom", "", s));
        }
        _custom = list;
    }

    public static IReadOnlyList<WidgetDescriptor> All => BuiltIns.Concat(_custom).ToList();

    /// <summary>Category display order in the add menu.</summary>
    public static readonly string[] Categories =
        { "System", "Media", "Developer", "Windows", "Time & Date", "Lintel", "Custom" };

    public static WidgetDescriptor? Find(string key) =>
        BuiltIns.Concat(_custom).FirstOrDefault(w => w.Key == key);
}
