using System.Diagnostics;
using System.Text.Json;

// LintelUpdater — downloads the latest Lintel.exe from the GitHub releases and swaps it in place.
//
// Usage:  LintelUpdater.exe [path-to-Lintel.exe]
// If no path is given it targets the default install location
// (%LOCALAPPDATA%\Programs\Lintel\Lintel.exe), falling back to a Lintel.exe next to this updater.

const string Repo = "voxelocity/Lintel";
const string AssetName = "Lintel.exe";

Console.Title = "Lintel Updater";
Log($"Lintel Updater {typeof(Program).Assembly.GetName().Version?.ToString(3)}");

try
{
    string target = ResolveTarget(args);
    Log($"Target: {target}");

    var (tag, url) = await LatestAssetAsync();
    if (url == null)
    {
        Fail($"Couldn't find a '{AssetName}' asset in the latest {Repo} release. " +
             "Make sure a release has been published with that asset.");
        return;
    }
    Log($"Latest release: {tag}");

    string temp = Path.Combine(Path.GetTempPath(), $"Lintel_{Guid.NewGuid():N}.exe");
    Log("Downloading…");
    await DownloadAsync(url, temp);
    Log($"Downloaded {new FileInfo(temp).Length / (1024 * 1024)} MB.");

    StopRunningLintel();

    Log("Installing…");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    ReplaceWithRetry(temp, target);
    Log("Updated.");

    Log("Relaunching Lintel…");
    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    Log("Done. This window will close shortly.");
    await Task.Delay(2500);
}
catch (Exception ex)
{
    Fail(ex.Message);
}

// ---- steps ----

static string ResolveTarget(string[] args)
{
    if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        return Path.GetFullPath(args[0]);

    string installed = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Lintel", "Lintel.exe");
    if (File.Exists(installed)) return installed;

    string beside = Path.Combine(AppContext.BaseDirectory, "Lintel.exe");
    return beside;
}

static async Task<(string? tag, string? url)> LatestAssetAsync()
{
    using var http = NewClient();
    string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
    if (root.TryGetProperty("assets", out var assets))
        foreach (var a in assets.EnumerateArray())
            if (a.TryGetProperty("name", out var n) && string.Equals(n.GetString(), AssetName, StringComparison.OrdinalIgnoreCase)
                && a.TryGetProperty("browser_download_url", out var u))
                return (tag, u.GetString());
    return (tag, null);
}

static async Task DownloadAsync(string url, string dest)
{
    using var http = NewClient();
    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();
    await using var src = await resp.Content.ReadAsStreamAsync();
    await using var fs = File.Create(dest);
    await src.CopyToAsync(fs);
}

static void StopRunningLintel()
{
    foreach (var p in Process.GetProcessesByName("Lintel"))
    {
        try { Log($"Closing running Lintel (pid {p.Id})…"); p.Kill(); p.WaitForExit(5000); }
        catch { /* already gone / access denied */ }
    }
}

static void ReplaceWithRetry(string source, string target)
{
    for (int attempt = 1; attempt <= 10; attempt++)
    {
        try { File.Copy(source, target, overwrite: true); try { File.Delete(source); } catch { } return; }
        catch (IOException) when (attempt < 10) { Thread.Sleep(500); }   // file still locked — give it a moment
        catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(500); }
    }
    throw new IOException($"Couldn't replace '{target}'. Close Lintel and try again.");
}

static HttpClient NewClient()
{
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("LintelUpdater");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    return http;
}

static void Log(string msg) => Console.WriteLine($"  {msg}");

static void Fail(string msg)
{
    Console.WriteLine();
    Console.WriteLine($"  Update failed: {msg}");
    Console.WriteLine("  Press any key to close.");
    try { Console.ReadKey(); } catch { }
}

// Marker type so Assembly lookup above has something to anchor to.
internal partial class Program { }
