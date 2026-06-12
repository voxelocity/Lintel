using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lintel.Services;

/// <summary>One launchable application discovered from the Start Menu.</summary>
public sealed record LaunchableApp(string Name, string Path)
{
    private ImageSource? _icon;
    public ImageSource? Icon => _icon ??= AppLauncher.IconFor(Path);
}

/// <summary>
/// Enumerates installed applications from the user's and the machine's Start Menu
/// (the same .lnk shortcuts the real Start menu lists) and resolves their icons.
/// The list is built once and cached.
/// </summary>
public static class AppLauncher
{
    private static List<LaunchableApp>? _cache;

    /// <summary>All Start-Menu apps, alphabetised and de-duplicated by name. Cached after the first call.</summary>
    public static IReadOnlyList<LaunchableApp> Apps()
    {
        if (_cache != null) return _cache;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        var byName = new Dictionary<string, LaunchableApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var programs = Path.Combine(root, "Programs");
            if (!Directory.Exists(programs)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var lnk in links)
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Skip uninstallers / help / readme clutter so the grid stays useful.
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("readme", StringComparison.OrdinalIgnoreCase)) continue;
                byName.TryAdd(name, new LaunchableApp(name, lnk));
            }
        }

        _cache = byName.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return _cache;
    }

    /// <summary>Launch a shortcut (or any path) through the shell.</summary>
    public static void Launch(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* ignore bad targets */ }
    }

    // ---- icon resolution (SHGetFileInfo resolves a .lnk to its target's icon) ----

    internal static ImageSource? IconFor(string path)
    {
        var info = new SHFILEINFO();
        IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { DestroyIcon(info.hIcon); }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
