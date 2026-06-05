using static Lintel.Interop.NativeMethods;

namespace Lintel.Interop;

/// <summary>A physical display, in raw device pixels.</summary>
internal readonly record struct MonitorInfo(RECT Bounds, RECT WorkArea, bool IsPrimary);

internal static class Monitors
{
    /// <summary>Enumerate all monitors in device-pixel coordinates, primary first.</summary>
    public static List<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
            {
                bool primary = (mi.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
                result.Add(new MonitorInfo(mi.rcMonitor, mi.rcWork, primary));
            }
            return true;
        }, IntPtr.Zero);

        // Primary first so MonitorIndex 0 is always the primary display.
        result.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));
        return result;
    }

    /// <summary>Pick the requested monitor by index, falling back to the primary.</summary>
    public static MonitorInfo Pick(int index)
    {
        var all = All();
        if (all.Count == 0)
            return new MonitorInfo(default, default, true);
        if (index >= 0 && index < all.Count)
            return all[index];
        return all[0];
    }

    /// <summary>Bounds of the monitor a given window currently lives on (device pixels).</summary>
    public static RECT MonitorBoundsForWindow(IntPtr hwnd)
    {
        var h = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(h, ref mi) ? mi.rcMonitor : default;
    }
}
