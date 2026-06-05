using System.Windows.Media;
using Lintel.Controls;

namespace Lintel.Widgets;

/// <summary>Per-metric identity: a signature colour and a graph style so each gauge looks distinct.</summary>
public static class MetricStyle
{
    public readonly record struct Info(Color Signature, GraphStyle Graph);

    private static readonly Dictionary<string, Info> Map = new()
    {
        ["cpu"] = new(Color.FromRgb(0x0A, 0x84, 0xFF), GraphStyle.Area),   // blue
        ["ram"] = new(Color.FromRgb(0xBF, 0x5A, 0xF2), GraphStyle.Area),   // purple
        ["gpu"] = new(Color.FromRgb(0x30, 0xD1, 0x58), GraphStyle.Area),   // green
        ["disk"] = new(Color.FromRgb(0xFF, 0x9F, 0x0A), GraphStyle.Bars),  // orange
        ["net"] = new(Color.FromRgb(0x64, 0xD2, 0xFF), GraphStyle.Bars),   // teal
        ["battery"] = new(Color.FromRgb(0xFF, 0xD6, 0x0A), GraphStyle.Line) // yellow
    };

    public static Info For(string key) =>
        Map.TryGetValue(key, out var i) ? i : new Info(Color.FromRgb(0x0A, 0x84, 0xFF), GraphStyle.Area);
}
