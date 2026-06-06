using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lintel.Controls;

/// <summary>
/// A thin-bar audio visualizer. When <see cref="Provider"/> is set (real loopback audio) it
/// reacts to actual sound; otherwise it animates an ambient pattern while <see cref="Active"/>.
/// </summary>
public sealed class Visualizer : FrameworkElement
{
    /// <summary>Supplies <paramref name="bars"/> live magnitudes (0..1) from real audio, or null.</summary>
    public static Func<int, float[]?>? Provider;

    private readonly DispatcherTimer _timer;
    private double[] _cur = Array.Empty<double>();
    private double[] _target = Array.Empty<double>();
    private readonly Random _rng = new();
    private int _bars = 9;

    public Color BarColor { get; set; } = Color.FromRgb(0x0A, 0x84, 0xFF);

    private bool _active;
    public bool Active
    {
        get => _active;
        set { _active = value; if (IsLoaded) _timer.Start(); }
    }

    public int Bars
    {
        get => _bars;
        set { _bars = Math.Max(1, value); _cur = new double[_bars]; _target = new double[_bars]; InvalidateVisual(); }
    }

    public Visualizer()
    {
        _cur = new double[_bars];
        _target = new double[_bars];
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var live = _active ? Provider?.Invoke(_bars) : null;
        bool moving = false;

        for (int i = 0; i < _bars; i++)
        {
            if (live != null) _target[i] = Math.Clamp(live[i], 0, 1);
            else if (_active)
            {
                // ambient fallback: a soft spectrum shape with jitter
                if (_rng.NextDouble() < 0.6)
                {
                    double mid = 1.0 - Math.Abs(i - _bars / 2.0) / (_bars / 1.4);
                    _target[i] = Math.Clamp(mid * (0.25 + _rng.NextDouble()), 0.05, 1);
                }
            }
            else _target[i] = 0.06;

            // snappy attack, slower decay — less "fake smooth"
            double d = _target[i] - _cur[i];
            _cur[i] += d * (d > 0 ? 0.7 : 0.24);
            if (Math.Abs(d) > 0.01) moving = true;
        }

        InvalidateVisual();
        if (!_active && !moving && live == null) _timer.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _bars == 0) return;

        double gap = w / _bars * 0.5;                 // thinner bars, more gap
        double bw = Math.Max(1.2, (w - gap * (_bars - 1)) / _bars);
        var brush = new SolidColorBrush(BarColor); brush.Freeze();

        for (int i = 0; i < _bars; i++)
        {
            double bh = Math.Max(1.5, _cur[i] * h);
            double x = i * (bw + gap);
            double y = (h - bh) / 2.0;
            double r = Math.Min(bw / 2, 1.5);
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, bw, bh), r, r);
        }
    }
}
