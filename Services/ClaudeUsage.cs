using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Lintel.Services;

/// <summary>Locates the installed Claude desktop app (Windows).</summary>
public static class ClaudeApp
{
    public static bool Installed => ExePath != null;

    /// <summary>Full path to the Claude desktop launcher, or null if not found.</summary>
    public static string? ExePath
    {
        get
        {
            foreach (var path in Candidates())
            {
                try { if (File.Exists(path)) return path; } catch { /* ignore */ }
            }
            return null;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        // Registered launch path (set by the installer).
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\claude.exe", null, null) is string r1 && r1.Length > 0)
            yield return r1.Trim('"');
        if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\claude.exe", null, null) is string r2 && r2.Length > 0)
            yield return r2.Trim('"');

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "AnthropicClaude", "claude.exe");
        yield return Path.Combine(local, "Programs", "claude", "Claude.exe");
        yield return Path.Combine(local, "Programs", "Claude", "Claude.exe");

        // Squirrel-style versioned installs: %LOCALAPPDATA%\AnthropicClaude\app-1.2.3\claude.exe (newest first).
        string baseDir = Path.Combine(local, "AnthropicClaude");
        string[] versions = Array.Empty<string>();
        if (Directory.Exists(baseDir))
            try { versions = Directory.GetDirectories(baseDir, "app-*"); } catch { /* ignore */ }
        Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
        for (int i = versions.Length - 1; i >= 0; i--)
            yield return Path.Combine(versions[i], "claude.exe");
    }
}

/// <summary>Token usage parsed from Claude Code's local transcripts (~/.claude/projects/**/*.jsonl).</summary>
public sealed class ClaudeStats
{
    public double[] Daily = Array.Empty<double>();   // oldest .. today (tokens/day)
    public long WindowUsed;                          // tokens used in the rolling window
    public long Today;                               // tokens used since local midnight
    public long Total;                               // tokens over the whole loaded range
    public DateTime? WindowReset;                    // when the rolling window frees up
    public bool HasData;
}

public static class ClaudeUsage
{
    public static string ProjectsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static bool Available => Directory.Exists(ProjectsDir);

    /// <summary>Read and aggregate usage. Runs file IO/parse off the UI thread.</summary>
    public static Task<ClaudeStats> LoadAsync(int days = 119, int windowHours = 5) =>
        Task.Run(() => Load(days, windowHours));

    private static ClaudeStats Load(int days, int windowHours)
    {
        var stats = new ClaudeStats { Daily = new double[days] };
        var dir = ProjectsDir;
        if (!Directory.Exists(dir)) return stats;

        DateTime today = DateTime.Now.Date;
        DateTime oldest = today.AddDays(-(days - 1));
        DateTime windowStart = DateTime.Now.AddHours(-windowHours);
        DateTime windowOldestSeen = DateTime.MaxValue;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories); }
        catch { return stats; }

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTime(file) < oldest.AddDays(-1)) continue;  // skip clearly-old files
            }
            catch { /* keep going */ }

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length < 30 || line.IndexOf("usage", StringComparison.Ordinal) < 0) continue;

                    DateTime ts;
                    long tokens;
                    if (!TryParse(line, out ts, out tokens) || tokens <= 0) continue;

                    stats.HasData = true;
                    stats.Total += tokens;

                    var local = ts.ToLocalTime();
                    int idx = (int)(local.Date - oldest).TotalDays;
                    if (idx >= 0 && idx < days) stats.Daily[idx] += tokens;
                    if (local.Date == today) stats.Today += tokens;
                    if (local >= windowStart)
                    {
                        stats.WindowUsed += tokens;
                        if (local < windowOldestSeen) windowOldestSeen = local;
                    }
                }
            }
            catch { /* unreadable file — skip */ }
        }

        if (windowOldestSeen != DateTime.MaxValue)
            stats.WindowReset = windowOldestSeen.AddHours(windowHours);

        return stats;
    }

    private static bool TryParse(string line, out DateTime ts, out long tokens)
    {
        ts = default; tokens = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
                && DateTime.TryParse(t.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                ts = parsed;
            else
                ts = DateTime.UtcNow;

            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var u))
                return false;

            tokens = Field(u, "input_tokens") + Field(u, "output_tokens")
                   + Field(u, "cache_creation_input_tokens") + Field(u, "cache_read_input_tokens");
            return true;
        }
        catch { return false; }
    }

    private static long Field(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
