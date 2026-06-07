using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Lintel.Models;
using Lintel.Widgets;

namespace Lintel.Services;

/// <summary>
/// Loads and imports user themes and widgets from <c>%AppData%\Lintel\{themes,widgets}\*.json</c>.
/// Files may contain <c>//</c> comments and trailing commas (so the downloadable templates stay readable).
/// </summary>
public static class Customization
{
    public static string Root => AppSettings.ConfigDirectory;
    public static string ThemesDir => Path.Combine(Root, "themes");
    public static string WidgetsDir => Path.Combine(Root, "widgets");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(ThemesDir);
        Directory.CreateDirectory(WidgetsDir);
        SeedReadme(ThemesDir, "themes");
        SeedReadme(WidgetsDir, "widgets");
    }

    private static void SeedReadme(string dir, string kind)
    {
        var path = Path.Combine(dir, "_README.txt");
        if (File.Exists(path)) return;
        try
        {
            File.WriteAllText(path,
                $"Drop your Lintel {kind} here as .json files — they're picked up automatically.\r\n" +
                "You can also use the in-app \"Import\" buttons (Add-widget menu or Advanced settings).\r\n" +
                "Comments (//) and trailing commas are allowed.\r\n\r\n" +
                "Templates with explanations:\r\n" +
                "https://github.com/voxelocity/Lintel/tree/main/docs/templates\r\n");
        }
        catch { /* best-effort */ }
    }

    // ---- loading ----

    public static List<ThemeSpec> LoadThemes() => Load<ThemeSpec>(ThemesDir, (s, f) =>
    {
        if (string.IsNullOrWhiteSpace(s.Name)) s.Name = Path.GetFileNameWithoutExtension(f);
    });

    public static List<CustomWidgetSpec> LoadWidgets() => Load<CustomWidgetSpec>(WidgetsDir, (s, f) =>
    {
        if (string.IsNullOrWhiteSpace(s.Key)) s.Key = "custom." + Sanitize(Path.GetFileNameWithoutExtension(f));
        if (string.IsNullOrWhiteSpace(s.Name)) s.Name = Path.GetFileNameWithoutExtension(f);
    });

    private static List<T> Load<T>(string dir, Action<T, string> fixup)
    {
        var list = new List<T>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var spec = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json);
                if (spec == null) continue;
                fixup(spec, file);
                list.Add(spec);
            }
            catch { /* skip a malformed file rather than crash the bar */ }
        }
        return list;
    }

    // ---- importing (copy a chosen file into the right folder) ----

    /// <summary>Validate + copy a theme file in. Returns the theme's name, or null on failure.</summary>
    public static string? ImportTheme(string srcPath)
    {
        EnsureDirs();
        var spec = JsonSerializer.Deserialize<ThemeSpec>(File.ReadAllText(srcPath), Json);
        if (spec == null) return null;
        var name = string.IsNullOrWhiteSpace(spec.Name) ? Path.GetFileNameWithoutExtension(srcPath) : spec.Name;
        File.Copy(srcPath, Path.Combine(ThemesDir, Sanitize(name) + ".json"), overwrite: true);
        return name;
    }

    /// <summary>Validate + copy a widget file in. Returns the widget's key, or null on failure.</summary>
    public static string? ImportWidget(string srcPath)
    {
        EnsureDirs();
        var spec = JsonSerializer.Deserialize<CustomWidgetSpec>(File.ReadAllText(srcPath), Json);
        if (spec == null) return null;
        var key = string.IsNullOrWhiteSpace(spec.Key) ? "custom." + Sanitize(Path.GetFileNameWithoutExtension(srcPath)) : spec.Key;
        File.Copy(srcPath, Path.Combine(WidgetsDir, Sanitize(key) + ".json"), overwrite: true);
        return key;
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "item" : s.Trim();
    }

    public static void OpenFolder()
    {
        EnsureDirs();
        try { Process.Start(new ProcessStartInfo(Root) { UseShellExecute = true }); } catch { }
    }

    // ---- command widgets ----

    /// <summary>Run a command widget's shell command and return its first non-empty output line.</summary>
    public static Task<string> RunCommandAsync(string command) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return "…"; }

            foreach (var line in outp.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t.Length > 60 ? t[..60] : t;
            }
            return "";
        }
        catch { return "!"; }
    });
}
