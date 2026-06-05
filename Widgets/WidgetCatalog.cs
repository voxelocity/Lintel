namespace Lintel.Widgets;

public enum WidgetKind { Gauge, Clock, Date, ActiveApp, Mode, Settings }

public sealed record WidgetDescriptor(string Key, string Name, WidgetKind Kind, string Glyph = "");

/// <summary>The full set of widgets the user can place on the bar.</summary>
public static class WidgetCatalog
{
    public static readonly IReadOnlyList<WidgetDescriptor> All = new List<WidgetDescriptor>
    {
        new("activeapp", "Active App", WidgetKind.ActiveApp, ""),
        new("clock",     "Clock",      WidgetKind.Clock,     ""),
        new("date",      "Date",       WidgetKind.Date,      ""),
        new("mode",      "Mode",       WidgetKind.Mode,      ""),
        new("settings",  "Settings",   WidgetKind.Settings,  ""),
        new("cpu",       "CPU",        WidgetKind.Gauge,     ""),
        new("ram",       "Memory",     WidgetKind.Gauge,     ""),
        new("gpu",       "GPU",        WidgetKind.Gauge,     ""),
        new("disk",      "Disk",       WidgetKind.Gauge,     ""),
        new("net",       "Network",    WidgetKind.Gauge,     ""),
        new("battery",   "Battery",    WidgetKind.Gauge,     ""),
    };

    public static WidgetDescriptor? Find(string key) => All.FirstOrDefault(w => w.Key == key);
}
