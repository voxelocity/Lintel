using System.Diagnostics;

namespace Lintel.Services;

public readonly record struct ProcUsage(string Name, string Value);

/// <summary>
/// Computes the top processes consuming a given resource, for the hover graph's
/// "what's using the most" list. Runs off the UI thread; values are best-effort.
/// </summary>
internal static class ProcessUsage
{
    public static async Task<List<ProcUsage>> TopAsync(string metricKey, int n = 4)
    {
        return await Task.Run(() => metricKey switch
        {
            "ram" => TopRam(n),
            "cpu" or "battery" => TopCpu(n),
            "gpu" => TopGpu(n),
            "disk" or "net" => TopIo(n),
            _ => new List<ProcUsage>()
        }).ConfigureAwait(false);
    }

    private static List<ProcUsage> TopRam(int n)
    {
        var groups = new Dictionary<string, long>();
        foreach (var p in Process.GetProcesses())
        {
            try { groups[p.ProcessName] = groups.GetValueOrDefault(p.ProcessName) + p.WorkingSet64; }
            catch { }
            finally { p.Dispose(); }
        }
        return groups.OrderByDescending(kv => kv.Value).Take(n)
            .Select(kv => new ProcUsage(kv.Key, FormatBytes(kv.Value))).ToList();
    }

    private static List<ProcUsage> TopCpu(int n)
    {
        var first = new Dictionary<int, (string name, TimeSpan cpu)>();
        foreach (var p in Process.GetProcesses())
        {
            try { first[p.Id] = (p.ProcessName, p.TotalProcessorTime); } catch { }
            finally { p.Dispose(); }
        }

        Thread.Sleep(350);
        var deltas = new Dictionary<string, double>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (first.TryGetValue(p.Id, out var prev))
                {
                    double ms = (p.TotalProcessorTime - prev.cpu).TotalMilliseconds;
                    if (ms > 0) deltas[p.ProcessName] = deltas.GetValueOrDefault(p.ProcessName) + ms;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        double total = 350.0 * Environment.ProcessorCount;
        return deltas.OrderByDescending(kv => kv.Value).Take(n)
            .Select(kv => new ProcUsage(kv.Key, $"{Math.Min(100, kv.Value / total * 100):0}%")).ToList();
    }

    private static List<ProcUsage> TopGpu(int n)
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var names = cat.GetInstanceNames().Where(i => i.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)).ToArray();
            var counters = names.Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i, true)).ToList();
            foreach (var c in counters) { try { c.NextValue(); } catch { } }
            Thread.Sleep(350);

            var byPid = new Dictionary<int, double>();
            foreach (var c in counters)
            {
                try
                {
                    double v = c.NextValue();
                    int pid = ParsePid(c.InstanceName);
                    if (pid > 0 && v > 0) byPid[pid] = byPid.GetValueOrDefault(pid) + v;
                }
                catch { }
                finally { c.Dispose(); }
            }

            return byPid.OrderByDescending(kv => kv.Value).Take(n)
                .Select(kv => new ProcUsage(NameOf(kv.Key), $"{Math.Min(100, kv.Value):0}%")).ToList();
        }
        catch { return new List<ProcUsage>(); }
    }

    private static List<ProcUsage> TopIo(int n)
    {
        try
        {
            var cat = new PerformanceCounterCategory("Process");
            var names = cat.GetInstanceNames().Where(i => i != "_Total" && i != "Idle").ToArray();
            var result = new List<(string name, double v)>();
            foreach (var i in names)
            {
                try
                {
                    using var c = new PerformanceCounter("Process", "IO Data Bytes/sec", i, true);
                    c.NextValue();
                    result.Add((i, 0)); // primed; refined below
                }
                catch { }
            }
            Thread.Sleep(300);
            var final = new List<(string name, double v)>();
            foreach (var i in names)
            {
                try
                {
                    using var c = new PerformanceCounter("Process", "IO Data Bytes/sec", i, true);
                    final.Add((StripSuffix(i), c.NextValue()));
                }
                catch { }
            }
            return final.GroupBy(x => x.name).Select(g => (g.Key, g.Sum(x => x.v)))
                .OrderByDescending(x => x.Item2).Take(n)
                .Select(x => new ProcUsage(x.Key, FormatRate(x.Item2))).ToList();
        }
        catch { return new List<ProcUsage>(); }
    }

    private static int ParsePid(string instance)
    {
        // e.g. "pid_1234_luid_..._engtype_3D"
        int i = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        i += 4;
        int j = i;
        while (j < instance.Length && char.IsDigit(instance[j])) j++;
        return int.TryParse(instance[i..j], out var pid) ? pid : -1;
    }

    private static string NameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return $"pid {pid}"; }
    }

    private static string StripSuffix(string name)
    {
        int h = name.IndexOf('#');
        return h > 0 ? name[..h] : name;
    }

    private static string FormatBytes(long b)
    {
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):0.0} GB";
        return $"{b / (double)(1L << 20):0} MB";
    }

    private static string FormatRate(double bps)
    {
        if (bps >= 1_000_000) return $"{bps / 1_000_000:0.0} MB/s";
        if (bps >= 1_000) return $"{bps / 1_000:0} KB/s";
        return $"{bps:0} B/s";
    }
}
