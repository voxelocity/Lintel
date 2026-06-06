namespace Lintel.Widgets;

public enum WidgetKind { Gauge, Clock, Date, ActiveApp, Mode, Settings, Note, Windows, Workspaces, Load, Media }

public sealed record WidgetDescriptor(string Key, string Name, WidgetKind Kind, string Category, string Glyph = "");

/// <summary>The full set of widgets the user can place on the bar.</summary>
public static class WidgetCatalog
{
    public static readonly IReadOnlyList<WidgetDescriptor> All = new List<WidgetDescriptor>
    {
        new("cpu",        "CPU",         WidgetKind.Gauge,      "System"),
        new("ram",        "Memory",      WidgetKind.Gauge,      "System"),
        new("gpu",        "GPU",         WidgetKind.Gauge,      "System"),
        new("disk",       "Disk",        WidgetKind.Gauge,      "System"),
        new("net",        "Network",     WidgetKind.Gauge,      "System"),
        new("battery",    "Battery",     WidgetKind.Gauge,      "System"),
        new("load",       "System Load", WidgetKind.Load,       "System"),

        new("media",      "Media",       WidgetKind.Media,      "Media"),

        new("activeapp",  "Active App",  WidgetKind.ActiveApp,  "Windows"),
        new("windows",    "App Tabs",    WidgetKind.Windows,    "Windows"),
        new("workspaces", "Workspaces",  WidgetKind.Workspaces, "Windows"),

        new("clock",      "Clock",       WidgetKind.Clock,      "Time & Date"),
        new("date",       "Date",        WidgetKind.Date,       "Time & Date"),

        new("mode",       "Mode",        WidgetKind.Mode,       "Lintel"),
        new("note",       "Quick Note",  WidgetKind.Note,       "Lintel"),
        new("settings",   "Settings",    WidgetKind.Settings,   "Lintel"),
    };

    /// <summary>Category display order in the add menu.</summary>
    public static readonly string[] Categories = { "System", "Media", "Windows", "Time & Date", "Lintel" };

    public static WidgetDescriptor? Find(string key) => All.FirstOrDefault(w => w.Key == key);
}
