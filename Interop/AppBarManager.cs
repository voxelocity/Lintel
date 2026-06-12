using System.Runtime.InteropServices;
using Lintel.Models;
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

    /// <summary>Reserve the strip described by <paramref name="barRect"/> along the given screen edge.</summary>
    public void Reserve(RECT barRect, BarEdge edge)
    {
        uint uEdge = edge switch { BarEdge.Bottom => ABE_BOTTOM, BarEdge.Left => ABE_LEFT, BarEdge.Right => ABE_RIGHT, _ => ABE_TOP };
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = uEdge
        };

        if (!_registered)
        {
            SHAppBarMessage(ABM_NEW, ref data);
            _registered = true;
        }

        // Propose the rectangle, let the shell adjust it (it stacks us beside the taskbar), then keep our thickness.
        data.rc = barRect;
        int w = barRect.Right - barRect.Left, h = barRect.Bottom - barRect.Top;
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        switch (edge)
        {
            case BarEdge.Bottom: data.rc.Top = data.rc.Bottom - h; break;
            case BarEdge.Top: data.rc.Bottom = data.rc.Top + h; break;
            case BarEdge.Left: data.rc.Right = data.rc.Left + w; break;
            case BarEdge.Right: data.rc.Left = data.rc.Right - w; break;
        }
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
