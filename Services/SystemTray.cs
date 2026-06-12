using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lintel.Services;

/// <summary>One icon in the Windows notification area (system tray).</summary>
public sealed class TrayItem
{
    public ImageSource? Icon;
    public string Title = "";
    public IntPtr TargetWindow;
    public uint Id;
    public uint CallbackMessage;

    /// <summary>Replay a click on the real tray icon by posting its callback message to the owner window.</summary>
    public void Invoke(bool rightClick) => SystemTray.Forward(this, rightClick);
}

/// <summary>
/// Best-effort enumeration of the real notification-area icons by reading the shell's
/// tray toolbar across processes. Returns an empty list on Windows builds where the tray
/// is XAML-hosted and not exposed as a classic ToolbarWindow32 (e.g. some Win11 releases),
/// in which case the widget falls back to a chevron + quick-access flyout.
/// </summary>
public static class SystemTray
{
    public static IReadOnlyList<TrayItem> Items()
    {
        var result = new List<TrayItem>();
        try
        {
            foreach (var toolbar in FindToolbars())
                ReadToolbar(toolbar, result);
        }
        catch { /* never let the bar fail because of the tray hack */ }
        return result;
    }

    internal static void Forward(TrayItem item, bool rightClick)
    {
        if (item.TargetWindow == IntPtr.Zero || item.CallbackMessage == 0) return;
        // Classic Shell_NotifyIcon callback convention: wParam = icon id, lParam = mouse message.
        uint down = rightClick ? WM_RBUTTONDOWN : WM_LBUTTONDOWN;
        uint up = rightClick ? WM_RBUTTONUP : WM_LBUTTONUP;
        SetForegroundWindow(item.TargetWindow);
        PostMessage(item.TargetWindow, item.CallbackMessage, (IntPtr)item.Id, (IntPtr)down);
        PostMessage(item.TargetWindow, item.CallbackMessage, (IntPtr)item.Id, (IntPtr)up);
    }

    // ---- locate the tray toolbars ----

    private static IEnumerable<IntPtr> FindToolbars()
    {
        var list = new List<IntPtr>();

        // Visible promoted icons: Shell_TrayWnd > TrayNotifyWnd > SysPager > ToolbarWindow32
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                var pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
                var host = pager != IntPtr.Zero ? pager : notify;
                var tb = FindWindowEx(host, IntPtr.Zero, "ToolbarWindow32", null);
                if (tb != IntPtr.Zero) list.Add(tb);
            }
        }

        // Hidden/overflow icons (classic): NotifyIconOverflowWindow > ToolbarWindow32
        var overflow = FindWindow("NotifyIconOverflowWindow", null);
        if (overflow != IntPtr.Zero)
        {
            var tb = FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", null);
            if (tb != IntPtr.Zero) list.Add(tb);
        }

        return list;
    }

    private static void ReadToolbar(IntPtr toolbar, List<TrayItem> result)
    {
        int count = (int)SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return;

        GetWindowThreadProcessId(toolbar, out uint pid);
        IntPtr proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (proc == IntPtr.Zero) return;

        int tbbSize = Marshal.SizeOf<TBBUTTON>();
        int trSize = Marshal.SizeOf<TRAYDATA>();
        IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, (uint)Math.Max(tbbSize, 1024), MEM_COMMIT, PAGE_READWRITE);
        if (remote == IntPtr.Zero) { CloseHandle(proc); return; }

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, remote) == IntPtr.Zero) continue;
                if (!ReadProcessMemory(proc, remote, out TBBUTTON btn, tbbSize, out _)) continue;
                if ((btn.fsState & TBSTATE_HIDDEN) != 0) continue;
                if (btn.dwData == IntPtr.Zero) continue;

                if (!ReadProcessMemory(proc, btn.dwData, out TRAYDATA tray, trSize, out _)) continue;

                var item = new TrayItem
                {
                    TargetWindow = tray.hwnd,
                    Id = tray.uID,
                    CallbackMessage = tray.uCallbackMessage,
                    Icon = IconFrom(tray.hIcon),
                    Title = ReadButtonText(toolbar, proc, remote, btn.idCommand)
                };
                if (item.Icon != null || !string.IsNullOrEmpty(item.Title))
                    result.Add(item);
            }
        }
        finally
        {
            VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
            CloseHandle(proc);
        }
    }

    private static string ReadButtonText(IntPtr toolbar, IntPtr proc, IntPtr remote, int cmd)
    {
        try
        {
            int len = (int)SendMessage(toolbar, TB_GETBUTTONTEXTW, (IntPtr)cmd, remote);
            if (len <= 0 || len > 250) return "";
            byte[] buf = new byte[(len + 1) * 2];
            if (!ReadProcessMemory(proc, remote, buf, buf.Length, out _)) return "";
            return System.Text.Encoding.Unicode.GetString(buf, 0, len * 2);
        }
        catch { return ""; }
    }

    private static ImageSource? IconFrom(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        // Icon handles are shareable across processes; copy it into ours to be safe.
        IntPtr copy = CopyIcon(hIcon);
        IntPtr use = copy != IntPtr.Zero ? copy : hIcon;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(use, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (copy != IntPtr.Zero) DestroyIcon(copy); }
    }

    // ---- interop ----

    private const uint TB_BUTTONCOUNT = 0x0418, TB_GETBUTTON = 0x0417, TB_GETBUTTONTEXTW = 0x044B;
    private const byte TBSTATE_HIDDEN = 0x08;
    private const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;
    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential)]
    private struct TBBUTTON
    {
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        public byte bReserved0;
        public byte bReserved1;
        public byte bReserved2;
        public byte bReserved3;
        public byte bReserved4;
        public byte bReserved5;
        public IntPtr dwData;
        public IntPtr iString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRAYDATA
    {
        public IntPtr hwnd;
        public uint uID;
        public uint uCallbackMessage;
        public uint Reserved0;
        public uint Reserved1;
        public IntPtr hIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, uint size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, uint size, uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, out TBBUTTON buf, int size, out IntPtr read);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, out TRAYDATA buf, int size, out IntPtr read);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, int size, out IntPtr read);
}
