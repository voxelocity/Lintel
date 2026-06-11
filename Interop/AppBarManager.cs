using System.Runtime.InteropServices;
using static Lintel.Interop.NativeMethods;

namespace Lintel.Interop;

/// <summary>
/// Registers the bar window as a top-docked desktop AppBar so that maximized windows
/// leave room for it — the same mechanism the Windows taskbar uses. All coordinates
/// passed to the shell are in raw device pixels.
/// </summary>
internal sealed class AppBarManager
{
    private readonly IntPtr _hwnd;
    private bool _registered;

    public AppBarManager(IntPtr hwnd) => _hwnd = hwnd;

    public bool IsRegistered => _registered;

    /// <summary>Reserve a strip of height <paramref name="heightPx"/> at the top (or bottom) of the monitor.</summary>
    public void Reserve(RECT monitorBounds, int heightPx, bool bottom = false)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = bottom ? ABE_BOTTOM : ABE_TOP
        };

        if (!_registered)
        {
            SHAppBarMessage(ABM_NEW, ref data);
            _registered = true;
        }

        // Propose the rectangle, let the shell adjust it, then commit.
        data.rc = bottom
            ? new RECT { Left = monitorBounds.Left, Top = monitorBounds.Bottom - heightPx, Right = monitorBounds.Right, Bottom = monitorBounds.Bottom }
            : new RECT { Left = monitorBounds.Left, Top = monitorBounds.Top, Right = monitorBounds.Right, Bottom = monitorBounds.Top + heightPx };

        SHAppBarMessage(ABM_QUERYPOS, ref data);
        // Honour the position the shell handed back (it stacks us above the taskbar), keep our height.
        if (bottom) data.rc.Top = data.rc.Bottom - heightPx;
        else { data.rc.Top = monitorBounds.Top; data.rc.Bottom = monitorBounds.Top + heightPx; }
        SHAppBarMessage(ABM_SETPOS, ref data);
    }

    /// <summary>Release the reserved space so maximized windows reclaim the strip.</summary>
    public void Release()
    {
        if (!_registered) return;
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd
        };
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }
}
