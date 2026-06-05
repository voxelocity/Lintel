using System.Diagnostics;
using System.Text;
using Lintel.Interop;
using static Lintel.Interop.NativeMethods;

namespace Lintel.Services;

/// <summary>The result of inspecting whatever window is currently in front.</summary>
public readonly record struct ForegroundState(string AppName, bool IsFullscreen, bool OverlapsBar);

/// <summary>
/// Looks at the active foreground window and decides whether it is fullscreen on the
/// bar's monitor, or whether its rectangle intrudes into the strip the bar occupies.
/// </summary>
internal static class ForegroundProbe
{
    private static int _lastPid = -1;
    private static string _lastAppName = "Desktop";

    /// <param name="barRectDevice">The bar's rectangle in device pixels.</param>
    /// <param name="monitorBounds">Bounds of the monitor the bar lives on, device pixels.</param>
    public static ForegroundState Inspect(RECT barRectDevice, RECT monitorBounds)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return new ForegroundState("Desktop", false, false);

        if (!GetWindowRect(hwnd, out var rect) || IsIconic(hwnd))
            return new ForegroundState(GetAppName(hwnd), false, false);

        // Only consider the window if it is on the same monitor as the bar.
        var winMonitor = Monitors.MonitorBoundsForWindow(hwnd);
        bool sameMonitor = winMonitor.Left == monitorBounds.Left && winMonitor.Top == monitorBounds.Top;

        bool fullscreen = false;
        bool overlaps = false;

        if (sameMonitor)
        {
            // Fullscreen: the window covers (or exceeds) the entire monitor.
            fullscreen =
                rect.Left <= monitorBounds.Left &&
                rect.Top <= monitorBounds.Top &&
                rect.Right >= monitorBounds.Right &&
                rect.Bottom >= monitorBounds.Bottom &&
                !IsShellWindow(hwnd);

            // Overlap: any part of the window pushes up into the bar's strip.
            overlaps = Intersects(rect, barRectDevice) && !IsShellWindow(hwnd);
        }

        return new ForegroundState(GetAppName(hwnd), fullscreen, overlaps);
    }

    private static bool Intersects(RECT a, RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    /// <summary>Treat the desktop / shell itself as "not an app" so the bar never hides behind the wallpaper.</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static string GetAppName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return _lastAppName;
            if ((int)pid == _lastPid) return _lastAppName; // cache: avoid opening the process every tick

            _lastPid = (int)pid;
            using var proc = Process.GetProcessById((int)pid);
            var name = FriendlyName(proc);
            _lastAppName = string.IsNullOrWhiteSpace(name) ? "Desktop" : name;
        }
        catch
        {
            _lastAppName = "Desktop";
        }
        return _lastAppName;
    }

    private static string FriendlyName(Process proc)
    {
        // Prefer the file description ("Visual Studio Code") over the raw exe name ("Code").
        try
        {
            var desc = proc.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc!;
        }
        catch
        {
            // Access denied for elevated processes — fall back to the process name.
        }
        return proc.ProcessName;
    }
}
