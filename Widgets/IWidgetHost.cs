using System.Windows.Input;
using Lintel.Models;
using Lintel.Services;

namespace Lintel.Widgets;

/// <summary>What a <see cref="WidgetView"/> needs from the bar that owns it.</summary>
public interface IWidgetHost
{
    AppSettings Settings { get; }
    /// <summary>Effective bar height in px (a theme may override the user's setting).</summary>
    double BarHeight { get; }
    bool Customizing { get; }

    /// <summary>The active app name shown by the ActiveApp widget.</summary>
    string ActiveAppName { get; }

    Metric GetMetric(string key);

    /// <summary>Current bar label for a "stat" widget (volume, weather, todo, pomodoro, …).</summary>
    string WidgetStat(WidgetView view);

    void OnModeClicked();
    void OnSettingsClicked();
    void RemoveWidget(WidgetView view);

    void BeginWidgetDrag(WidgetView view, MouseButtonEventArgs e);

    // Dropdowns
    /// <summary>True if dropdowns open on hover (vs click).</summary>
    bool OpenOnHover { get; }
    /// <summary>True for widget kinds that show an info dropdown (gauge / load / media).</summary>
    bool HasDropdown(WidgetView view);
    /// <summary>Open the widget's dropdown (graph / resource panel / media player).</summary>
    void OpenWidgetDropdown(WidgetView view, bool hover);
    /// <summary>Cursor left a hover-opened widget.</summary>
    void WidgetHoverLeft(WidgetView view);

    // Interactive widgets
    void ShowNote(WidgetView view);
    void ShowWindowSwitcher(WidgetView view);
    int OpenWindowCount();
    void SwitchWorkspace(int direction);

    MediaService Media { get; }
}
