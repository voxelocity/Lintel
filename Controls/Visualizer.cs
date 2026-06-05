using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lintel.Controls;

/// <summary>
/// A lightweight bar "audio visualizer". It animates while <see cref="Active"/> is true and
/// settles to a flat baseline when paused. (Ambient animation — not tied to real audio.)
/// </summary>
public sealed class Visualizer : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private double[] _cur = Array.Empty<double>();
    private double[] _target = Array.Empty<double>();
    private readonly Random _rng = new();
    private int _bars = 5;

    public Color BarColor { get; set; } = Color.FromRgb(0x0A, 0x84, 0xFF);

    private bool _active;
    public bool Active
    {
        get => _active;
        set { _active = value; if (IsLoaded) { if (value) _timer.Start(); } }
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
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(55) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        bool moving = false;
        for (int i = 0; i < _bars; i++)
        {
            if (_active)
            {
                // occasionally pick a new target height
                if (_rng.NextDouble() < 0.35) _target[i] = 0.18 + _rng.NextDouble() * 0.82;
            }
            else _target[i] = 0.10;

            double d = _target[i] - _cur[i];
            _cur[i] += d * 0.35;
            if (Math.Abs(d) > 0.01) moving = true;
        }
        InvalidateVisual();
        if (!_active && !moving) _timer.Stop(); // idle: stop redrawing once settled
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _bars == 0) return;

        double gap = Math.Max(1.5, w / _bars * 0.34);
        double bw = (w - gap * (_bars - 1)) / _bars;
        var brush = new SolidColorBrush(BarColor); brush.Freeze();

        for (int i = 0; i < _bars; i++)
        {
            double bh = Math.Max(2, _cur[i] * h);
            double x = i * (bw + gap);
            double y = (h - bh) / 2.0; // centred bars
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, bw, bh), bw / 2.5, bw / 2.5);
        }
    }
}
