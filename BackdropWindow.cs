using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lintel;

/// <summary>
/// A separate, non-layered, click-through window that sits directly behind the bar and carries a
/// real DWM system-backdrop (acrylic) — i.e. true real-time blur of whatever is behind it. The bar
/// (a layered, per-pixel-transparent window) rides on top and shows this through its transparent areas.
/// </summary>
internal sealed class BackdropWindow : Window
{
    public BackdropWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = false;        // required: DWM backdrops don't work on layered windows
        Background = Brushes.Transparent;  // transparent client → the DWM backdrop fills it
        Topmost = true;
        Title = "LintelBackdrop";
    }

    public IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;

        // Make WPF's own composition surface transparent, otherwise it paints opaque black over
        // the DWM backdrop and we never see the acrylic.
        if (PresentationSource.FromVisual(this) is HwndSource src && src.CompositionTarget != null)
            src.CompositionTarget.BackgroundColor = Colors.Transparent;

        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);

        // Sheet-of-glass: extend the frame across the whole client so the backdrop shows everywhere.
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref m);

        int backdrop = 3;  // DWMSBT_TRANSIENTWINDOW = acrylic
        DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        int dark = 1;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    public void SetBackdropType(int type)
    {
        if (Handle != IntPtr.Zero)
            DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
    }

    // ---- interop ----
    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TRANSPARENT = 0x00000020;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
