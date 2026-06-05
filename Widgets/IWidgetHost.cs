using System.Windows.Input;
using Lintel.Models;
using Lintel.Services;

namespace Lintel.Widgets;

/// <summary>What a <see cref="WidgetView"/> needs from the bar that owns it.</summary>
public interface IWidgetHost
{
    AppSettings Settings { get; }
    bool Customizing { get; }

    /// <summary>The active app name shown by the ActiveApp widget.</summary>
    string ActiveAppName { get; }

    Metric GetMetric(string key);

    void OnModeClicked();
    void OnSettingsClicked();
    void RemoveWidget(WidgetView view);

    void ShowGraph(WidgetView view, Metric metric);
    void HideGraph(WidgetView view);

    void BeginWidgetDrag(WidgetView view, MouseButtonEventArgs e);
}
