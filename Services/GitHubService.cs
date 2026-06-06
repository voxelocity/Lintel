using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lintel.Services;

public sealed class GitHubStats
{
    public double[] Daily = Array.Empty<double>();   // oldest .. today (contributions/day)
    public int Total;
    public string Login = "";
    public bool HasData;
    public string? Error;
}

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Thin wrapper over the <c>gh</c> and <c>git</c> CLIs for the GitHub widget.</summary>
public static class GitHubService
{
    public static string DesktopDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static bool? _ghCached;
    public static bool GhAvailable => _ghCached ??= Which("gh");
    public static bool GitAvailable => Which("git");

    private static bool Which(string exe)
    {
        try
        {
            var r = Run(exe, "--version", null, 4000);
            return r.Ok;
        }
        catch { return false; }
    }

    // ---- contribution calendar (the "commit graph array") ----

    public static Task<GitHubStats> LoadContributionsAsync() => Task.Run(LoadContributions);

    private static GitHubStats LoadContributions()
    {
        var stats = new GitHubStats { Daily = new double[119] };
        if (!GhAvailable) { stats.Error = "GitHub CLI (gh) not found"; return stats; }

        const string query =
            "query{viewer{login contributionsCollection{contributionCalendar{totalContributions weeks{contributionDays{date contributionCount}}}}}}";

        ProcResult r;
        try { r = Run("gh", $"api graphql -f query={Quote(query)}", null, 15000); }
        catch (Exception ex) { stats.Error = ex.Message; return stats; }

        if (!r.Ok) { stats.Error = string.IsNullOrWhiteSpace(r.StdErr) ? "gh not authenticated" : r.StdErr.Trim(); return stats; }

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var cal = doc.RootElement.GetProperty("data").GetProperty("viewer");
            stats.Login = cal.GetProperty("login").GetString() ?? "";
            var calendar = cal.GetProperty("contributionsCollection").GetProperty("contributionCalendar");
            stats.Total = calendar.GetProperty("totalContributions").GetInt32();

            DateTime oldest = DateTime.Now.Date.AddDays(-(stats.Daily.Length - 1));
            foreach (var week in calendar.GetProperty("weeks").EnumerateArray())
                foreach (var day in week.GetProperty("contributionDays").EnumerateArray())
                {
                    if (!DateTime.TryParse(day.GetProperty("date").GetString(), out var d)) continue;
                    int idx = (int)(d.Date - oldest).TotalDays;
                    if (idx >= 0 && idx < stats.Daily.Length)
                        stats.Daily[idx] = day.GetProperty("contributionCount").GetInt32();
                }
            stats.HasData = true;
        }
        catch (Exception ex) { stats.Error = "Parse error: " + ex.Message; }

        return stats;
    }

    // ---- clone ----

    public static Task<ProcResult> CloneAsync(string url, string targetDir) => Task.Run(() =>
    {
        url = url.Trim();
        if (string.IsNullOrEmpty(url)) return new ProcResult(1, "", "No URL");
        Directory.CreateDirectory(targetDir);
        // Prefer gh for shorthand like "owner/repo"; fall back to git for full URLs.
        bool shorthand = !url.Contains("://") && !url.Contains('@') && url.Count(c => c == '/') == 1;
        return shorthand && GhAvailable
            ? Run("gh", $"repo clone {url}", targetDir, 120000)
            : Run("git", $"clone {url}", targetDir, 120000);
    });

    // ---- create repo from a local folder ----

    public static Task<ProcResult> CreateFromFolderAsync(string folder, bool isPrivate) => Task.Run(() =>
    {
        if (!Directory.Exists(folder)) return new ProcResult(1, "", "Folder not found");
        if (!GhAvailable) return new ProcResult(1, "", "GitHub CLI (gh) not found");

        string name = new DirectoryInfo(folder).Name;

        // Make sure the folder is a git repo with at least one commit so gh can push it.
        if (!Directory.Exists(Path.Combine(folder, ".git")))
        {
            var init = Run("git", "init", folder, 15000);
            if (!init.Ok) return init;
        }
        Run("git", "add -A", folder, 60000);
        // Commit only if there's something staged / no commits yet (ignore "nothing to commit").
        Run("git", "commit -m \"Initial commit\"", folder, 60000);

        string vis = isPrivate ? "--private" : "--public";
        return Run("gh", $"repo create {name} --source=. {vis} --push", folder, 120000);
    });

    // ---- process helper ----

    private static ProcResult Run(string exe, string args, string? workDir, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return new ProcResult(-1, so.ToString(), "Timed out");
        }
        p.WaitForExit();
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString());
    }

    // gh accepts -f query=<value>; wrap value so spaces survive argument splitting.
    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
