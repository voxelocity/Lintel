using Microsoft.Win32;

namespace Lintel.Services;

/// <summary>
/// Reads the current virtual-desktop name/index from the registry (where Explorer stores them).
/// Reliable enough for a label, and needs no per-build COM interfaces.
/// </summary>
public static class DesktopInfo
{
    private const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <summary>e.g. "Desktop 2". Falls back gracefully if the keys aren't present.</summary>
    public static string CurrentName()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root);
            if (key?.GetValue("CurrentVirtualDesktop") is not byte[] cur || cur.Length != 16)
                return "Desktop";

            var ids = key.GetValue("VirtualDesktopIDs") as byte[];
            int index = 0, count = 1;
            Guid currentGuid = new(cur);

            if (ids != null && ids.Length % 16 == 0)
            {
                count = ids.Length / 16;
                for (int i = 0; i < count; i++)
                {
                    var g = new Guid(ids.AsSpan(i * 16, 16).ToArray());
                    if (g == currentGuid) { index = i; break; }
                }
            }

            // Custom name, if the user renamed the desktop.
            using var desks = Registry.CurrentUser.OpenSubKey(Root + @"\Desktops\{" + currentGuid + "}");
            if (desks?.GetValue("Name") is string name && !string.IsNullOrWhiteSpace(name))
                return name;

            return $"Desktop {index + 1}";
        }
        catch
        {
            return "Desktop";
        }
    }
}
