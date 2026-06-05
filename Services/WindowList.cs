using System.Diagnostics;
using System.Text;
using static Lintel.Interop.NativeMethods;

namespace Lintel.Services;

public readonly record struct AppWindow(IntPtr Handle, string Title, string Process);

/// <summary>Enumerates and activates real top-level application windows (the "App Tabs" widget).</summary>
internal static class WindowList
{
    public static List<AppWindow> Enumerate()
    {
        var result = new List<AppWindow>();
        var self = Process.GetCurrentProcess().Id;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;

            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((style & WS_EX_TOOLWINDOW) != 0) return true; // skip tool windows (like our bar)

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)self) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            string proc = "";
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }

            result.Add(new AppWindow(hwnd, title, proc));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static int Count() => Enumerate().Count;

    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Switch virtual desktop using the built-in Ctrl+Win+Arrow shortcut (reliable across builds).</summary>
    public static void SwitchDesktop(int direction)
    {
        byte arrow = direction < 0 ? VK_LEFT : VK_RIGHT;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(arrow, 0, 0, UIntPtr.Zero);
        keybd_event(arrow, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
