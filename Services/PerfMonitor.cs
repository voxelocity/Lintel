using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Lintel.Services;

/// <summary>One sampled system metric with a rolling history for the hover graph.</summary>
public sealed class Metric : INotifyPropertyChanged
{
    public const int HistoryLength = 60;

    public string Key { get; }
    public string Name { get; }

    private readonly double[] _history = new double[HistoryLength];
    private int _count;

    public Metric(string key, string name)
    {
        Key = key;
        Name = name;
    }

    private double _percent;
    /// <summary>0–100 value that drives the ring gauge.</summary>
    public double Percent
    {
        get => _percent;
        private set { if (Math.Abs(_percent - value) > 0.01) { _percent = value; Raise(); } }
    }

    private string _text = "—";
    /// <summary>Short display value, e.g. "42%" or "12.4 MB/s".</summary>
    public string Text
    {
        get => _text;
        private set { if (_text != value) { _text = value; Raise(); } }
    }

    public void Push(double percent, string text)
    {
        percent = Math.Clamp(percent, 0, 100);
        Percent = percent;
        Text = text;

        // ring buffer, oldest first via Snapshot()
        if (_count < HistoryLength)
        {
            _history[_count++] = percent;
        }
        else
        {
            Array.Copy(_history, 1, _history, 0, HistoryLength - 1);
            _history[HistoryLength - 1] = percent;
        }
        HistoryChanged?.Invoke();
    }

    /// <summary>History oldest→newest.</summary>
    public double[] Snapshot()
    {
        var outArr = new double[_count];
        Array.Copy(_history, outArr, _count);
        return outArr;
    }

    public event Action? HistoryChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Samples CPU / RAM / GPU / disk / network / battery once a second on a background
/// thread, then marshals updates onto the UI dispatcher. Only metrics in the active set
/// are sampled, so widgets you don't use cost nothing.
/// </summary>
public sealed class PerfMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, Metric> _metrics = new();
    private HashSet<string> _active = new();
    private readonly System.Threading.Timer _timer;

    private PerformanceCounter? _cpu;
    private PerformanceCounter? _disk;
    private PerformanceCounter[]? _net;
    private PerformanceCounter[]? _gpu;
    private double _netPeak = 1_000_000; // adaptive ceiling for the network ring

    public event Action? Updated;

    public PerfMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        foreach (var (key, name) in Catalog)
            _metrics[key] = new Metric(key, name);
        _timer = new System.Threading.Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static readonly (string Key, string Name)[] Catalog =
    {
        ("cpu", "CPU"), ("ram", "Memory"), ("gpu", "GPU"),
        ("disk", "Disk"), ("net", "Network"), ("battery", "Battery")
    };

    public static bool IsMetric(string key) => Array.Exists(Catalog, c => c.Key == key);

    public Metric Get(string key) => _metrics[key];

    /// <summary>Tell the monitor which metrics are currently on screen.</summary>
    public void SetActive(IEnumerable<string> keys)
    {
        _active = new HashSet<string>(keys.Where(IsMetric));
        bool any = _active.Count > 0;
        _timer.Change(0, 1000);
        if (!any) _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Sample()
    {
        try
        {
            var results = new List<(string key, double pct, string text)>();

            if (_active.Contains("cpu"))
            {
                _cpu ??= TryCounter("Processor Information", "% Processor Time", "_Total")
                         ?? TryCounter("Processor", "% Processor Time", "_Total");
                double v = _cpu?.NextValue() ?? 0;
                results.Add(("cpu", v, $"{v:0}%"));
            }

            if (_active.Contains("ram"))
            {
                var m = MemoryStatus();
                results.Add(("ram", m.load, $"{m.usedGb:0.0}/{m.totalGb:0.0} GB"));
            }

            if (_active.Contains("gpu"))
            {
                double v = SampleGpu();
                results.Add(("gpu", v, $"{v:0}%"));
            }

            if (_active.Contains("disk"))
            {
                _disk ??= TryCounter("PhysicalDisk", "% Disk Time", "_Total");
                double v = Math.Min(100, _disk?.NextValue() ?? 0);
                results.Add(("disk", v, $"{v:0}%"));
            }

            if (_active.Contains("net"))
            {
                double bytes = SampleNet();
                _netPeak = Math.Max(_netPeak * 0.97, bytes); // decay the ceiling slowly
                double pct = _netPeak > 0 ? bytes / _netPeak * 100.0 : 0;
                results.Add(("net", pct, FormatRate(bytes)));
            }

            if (_active.Contains("battery"))
            {
                var status = System.Windows.Forms.SystemInformation.PowerStatus;
                double pct = status.BatteryLifePercent * 100.0;
                if (status.BatteryLifePercent > 1) pct = 100; // 255 = unknown
                string charging = status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "⚡" : "";
                results.Add(("battery", pct, $"{pct:0}% {charging}".Trim()));
            }

            _dispatcher.BeginInvoke(() =>
            {
                foreach (var r in results)
                    _metrics[r.key].Push(r.pct, r.text);
                Updated?.Invoke();
            });
        }
        catch
        {
            // Sampling is best-effort; never let a counter hiccup crash the bar.
        }
    }

    private static PerformanceCounter? TryCounter(string cat, string counter, string instance)
    {
        try { return new PerformanceCounter(cat, counter, instance, readOnly: true); }
        catch { return null; }
    }

    private double SampleGpu()
    {
        try
        {
            if (_gpu == null)
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                var names = cat.GetInstanceNames();
                _gpu = names.Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                            .Select(n => TryCounter("GPU Engine", "Utilization Percentage", n))
                            .Where(c => c != null).ToArray()!;
            }
            double sum = 0;
            foreach (var c in _gpu) sum += c.NextValue();
            return Math.Min(100, sum);
        }
        catch { return 0; }
    }

    private double SampleNet()
    {
        try
        {
            if (_net == null)
            {
                var cat = new PerformanceCounterCategory("Network Interface");
                _net = cat.GetInstanceNames()
                          .Where(n => !n.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                          .Select(n => TryCounter("Network Interface", "Bytes Total/sec", n))
                          .Where(c => c != null).ToArray()!;
            }
            double sum = 0;
            foreach (var c in _net) sum += c.NextValue();
            return sum;
        }
        catch { return 0; }
    }

    private static string FormatRate(double bytesPerSec)
    {
        double bits = bytesPerSec; // show bytes/s scaled
        if (bytesPerSec >= 1_000_000) return $"{bytesPerSec / 1_000_000:0.0} MB/s";
        if (bytesPerSec >= 1_000) return $"{bytesPerSec / 1_000:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    // --- RAM via GlobalMemoryStatusEx (cheap & instant) ---

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static (double load, double usedGb, double totalGb) MemoryStatus()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s)) return (0, 0, 0);
        double totalGb = s.ullTotalPhys / 1073741824.0;
        double usedGb = (s.ullTotalPhys - s.ullAvailPhys) / 1073741824.0;
        return (s.dwMemoryLoad, usedGb, totalGb);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _cpu?.Dispose();
        _disk?.Dispose();
        if (_net != null) foreach (var c in _net) c.Dispose();
        if (_gpu != null) foreach (var c in _gpu) c.Dispose();
    }
}
