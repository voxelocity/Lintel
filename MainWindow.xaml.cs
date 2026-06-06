using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lintel.Controls;
using Lintel.Interop;
using Lintel.Models;
using Lintel.Services;
using Lintel.Widgets;
using static Lintel.Interop.NativeMethods;

namespace Lintel;

public partial class MainWindow : Window, IWidgetHost
{
    public static readonly DependencyProperty BarBackgroundProperty =
        DependencyProperty.Register(nameof(BarBackground), typeof(Brush), typeof(MainWindow),
            new PropertyMetadata(Brushes.Black));

    public Brush BarBackground
    {
        get => (Brush)GetValue(BarBackgroundProperty);
        set => SetValue(BarBackgroundProperty, value);
    }

    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private AppBarManager? _appBar;
    private PerfMonitor? _perf;
    private MediaService? _media;
    private AudioCapture? _audio;

    private readonly DispatcherTimer _tick;     // visibility + foreground polling
    private readonly DispatcherTimer _clock;    // 1s content refresh
    private readonly DispatcherTimer _overlayHideTimer;

    private double _scaleX = 1, _scaleY = 1;
    private RECT _monitorBounds;
    private int _barHeightPx;
    private bool _shown = true;
    private bool _animating;
    private bool _forceOpen;
    private int _tickCount;

    private DateTime? _hotSince;
    private DateTime? _hideAt;

    private readonly List<WidgetView> _allWidgets = new();
    private string _activeAppName = "Desktop";

    // drag state
    private bool _dragActive;
    private WidgetView? _drag;
    private double _dragGrabX;        // cursor offset within the grabbed widget
    private Size _dragSize;           // grabbed widget size
    private System.Windows.Shapes.Rectangle? _dropIndicator; // predictive landing outline

    // dropdown / hover state
    private WidgetView? _overlayOwner;   // widget that opened the current dropdown
    private bool _overlayHover;          // opened via hover (so it closes on leave)
    private bool _overlayClosing;
    private Action? _overlayUpdate;      // live-refresh callback for the open dropdown

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _tick = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(80) };
        _tick.Tick += OnTick;

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => RefreshDynamicWidgets();

        _overlayHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _overlayHideTimer.Tick += (_, _) => { _overlayHideTimer.Stop(); if (_overlayHover) CloseOverlay(); };

        MouseMove += OnWindowMouseMove;
        PreviewMouseLeftButtonUp += OnWindowMouseUp;
        MouseRightButtonUp += OnBarRightClick;

        // Keep a hover-opened dropdown alive while the cursor is over it.
        OverlayHost.MouseEnter += (_, _) => _overlayHideTimer.Stop();
        OverlayHost.MouseLeave += (_, _) => { if (_overlayHover) { _overlayHideTimer.Stop(); _overlayHideTimer.Start(); } };
    }

    // =========================================================== IWidgetHost

    public AppSettings Settings => _settings;
    public bool Customizing { get; private set; }
    public string ActiveAppName => _activeAppName;
    public Metric GetMetric(string key) => _perf!.Get(key);
    public MediaService Media => _media!;

    public void OnModeClicked() => CycleMode();
    public void OnSettingsClicked() => OpenSettings();

    public bool OpenOnHover => _settings.OpenOnHover;

    public bool HasDropdown(WidgetView view) => view.Descriptor.Kind
        is WidgetKind.Gauge or WidgetKind.Load or WidgetKind.Media or WidgetKind.Claude or WidgetKind.GitHub;

    public void OpenWidgetDropdown(WidgetView view, bool hover)
    {
        // Don't let a hover steal a click-opened (modal) dropdown or menu.
        if (hover && OverlayPopup.IsOpen && !_overlayHover && !_overlayClosing) return;
        _overlayHideTimer.Stop();
        if (ReferenceEquals(_overlayOwner, view) && OverlayPopup.IsOpen && !_overlayClosing) return;
        _overlayOwner = view;
        _pendingHover = hover;
        switch (view.Descriptor.Kind)
        {
            case WidgetKind.Gauge: ShowGraph(view, GetMetric(view.Key)); break;
            case WidgetKind.Load: ShowResourcePanel(view); break;
            case WidgetKind.Media: ShowMediaPanel(view); break;
            case WidgetKind.Claude: ShowClaudePanel(view); break;
            case WidgetKind.GitHub: ShowGitHubPanel(view); break;
        }
    }

    public void WidgetHoverLeft(WidgetView view)
    {
        if (_overlayHover && ReferenceEquals(_overlayOwner, view))
        {
            _overlayHideTimer.Stop();
            _overlayHideTimer.Start();
        }
    }

    // =========================================================== lifecycle

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        _appBar = new AppBarManager(_hwnd);
        _perf = new PerfMonitor(Dispatcher);

        _media = new MediaService(Dispatcher);
        _media.Changed += () => { foreach (var w in _allWidgets) if (w.Descriptor.Kind == WidgetKind.Media) w.UpdateMedia(); };
        _media.Start();
        _audio = new AudioCapture();

        ApplySettings();
        ApplyMode(initial: true);

        _clock.Start();
        _tick.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        _clock.Stop();
        _perf?.Dispose();
        _audio?.Dispose();
        _appBar?.Release();
        base.OnClosed(e);
    }

    // =========================================================== settings/layout

    private bool _backdrop;
    private Color _backdropTint;

    private bool _fluid;
    private bool _shoulder;
    private Color _dropMaterial = Color.FromArgb(0xF5, 0x1F, 0x1F, 0x23);
    private Color? _dropOutline;

    private static Color ParseColor(string hex, Color fallback, byte minAlpha = 0)
    {
        try { var c = (Color)ColorConverter.ConvertFromString(hex); if (c.A < minAlpha) c.A = minAlpha; return c; }
        catch { return fallback; }
    }

    public void ApplySettings()
    {
        var theme = Themes.For(_settings.Theme, _settings.WidgetCornerRadius);
        _backdrop = theme.Acrylic;
        _backdropTint = theme.AcrylicTint;
        _fluid = theme.FluidDropdowns;
        _shoulder = _fluid;   // all fluid themes get the connected shoulder shape

        // Dropdowns share the bar's material. Acrylic themes use a translucent tint so the
        // dropdown reads like the bar (vector-drawn, so corners stay clean).
        _dropMaterial = theme.SeparatedZones ? theme.ZoneBackground
            : theme.Acrylic ? Color.FromArgb(0xDC, theme.AcrylicTint.R, theme.AcrylicTint.G, theme.AcrylicTint.B)
            : ParseColor(_settings.BackgroundColor, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E), 0xF6);
        _dropOutline = theme.BottomHighlight ? Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)
            : theme.SeparatedZones ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)
            : (Color?)null;

        // Islands: transparent bar with floating zone pills (gaps show desktop).
        // Acrylic: near-transparent (hit-testable) so the blur shows. Else: solid colour.
        BarBackground = theme.SeparatedZones
            ? new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0))
            : _backdrop
                ? new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0))
                : BrushFrom(_settings.BackgroundColor, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E));
        Foreground = BrushFrom(_settings.ForegroundColor, Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF7));

        BottomLine.Visibility = theme.BottomHighlight ? Visibility.Visible : Visibility.Collapsed;

        RebuildWidgets();
        ApplyZoneStyle(theme);
        ApplyLayout();
        ApplyBackdrop(_shown && _backdrop);
    }

    private void ApplyZoneStyle(ThemeDef theme)
    {
        var zones = new[] { (ZoneLeft, PanelLeft), (ZoneCenter, PanelCenter), (ZoneRight, PanelRight) };
        if (theme.SeparatedZones)
        {
            var bg = new SolidColorBrush(theme.ZoneBackground); bg.Freeze();
            foreach (var (zone, panel) in zones)
            {
                zone.Background = bg;
                zone.CornerRadius = new CornerRadius(10);
                zone.Padding = new Thickness(16, 0, 16, 0);
                zone.Margin = new Thickness(4, 3, 4, 3);
                zone.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Opacity = 0.35, Color = Colors.Black };
                zone.Visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        else
        {
            foreach (var (zone, panel) in zones)
            {
                zone.Background = Brushes.Transparent;
                zone.CornerRadius = new CornerRadius(0);
                zone.Padding = new Thickness(0);
                zone.Margin = new Thickness(0);
                zone.Effect = null;
                zone.Visibility = Visibility.Visible;
            }
        }
    }

    private void ApplyBackdrop(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        uint abgr = (uint)((_backdropTint.A << 24) | (_backdropTint.B << 16) | (_backdropTint.G << 8) | _backdropTint.R);
        var accent = new ACCENT_POLICY
        {
            AccentState = enabled ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_DISABLED,
            GradientColor = abgr
        };
        int size = System.Runtime.InteropServices.Marshal.SizeOf(accent);
        IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINCOMPATTRDATA { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
    }

    private void ApplyLayout()
    {
        if (_hwnd == IntPtr.Zero) return;
        _monitorBounds = Monitors.Pick(_settings.MonitorIndex).Bounds;

        uint dpi = GetDpiForWindow(_hwnd);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        _scaleX = _scaleY = scale;
        _barHeightPx = (int)Math.Round(_settings.BarHeight * scale);

        Left = _monitorBounds.Left / scale;
        Top = _monitorBounds.Top / scale;
        Width = _monitorBounds.Width / scale;
        Height = _settings.BarHeight;

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (!_shown) SlideTransform.Y = -_settings.BarHeight;
    }

    public void ApplyMode(bool initial = false)
    {
        _hotSince = null;
        _hideAt = null;

        if (_settings.Mode == VisibilityMode.AlwaysOn)
        {
            _appBar?.Reserve(_monitorBounds, _barHeightPx);
            SetShown(true, animate: !initial);
        }
        else
        {
            _appBar?.Release();
            SetShown(_settings.Mode == VisibilityMode.Dynamic, animate: false);
        }
        RefreshDynamicWidgets();
    }

    // =========================================================== widgets

    private void RebuildWidgets()
    {
        PanelLeft.Children.Clear();
        PanelCenter.Children.Clear();
        PanelRight.Children.Clear();
        _allWidgets.Clear();

        var t = Themes.For(_settings.Theme, _settings.WidgetCornerRadius);
        PanelLeft.Spacing = PanelCenter.Spacing = PanelRight.Spacing = t.Spacing;

        Brush? divider = null;
        if (t.WidgetDividers) { var d = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)); d.Freeze(); divider = d; }
        PanelLeft.DividerBrush = PanelCenter.DividerBrush = PanelRight.DividerBrush = divider;

        AddZone(PanelLeft, _settings.LeftWidgets);
        AddZone(PanelCenter, _settings.CenterWidgets);
        AddZone(PanelRight, _settings.RightWidgets);

        UpdatePerfActive();
        foreach (var w in _allWidgets) w.SetCustomizing(Customizing);
        RefreshDynamicWidgets();
    }

    private void AddZone(AnimatedBarPanel panel, List<string> keys)
    {
        foreach (var key in keys)
        {
            var desc = WidgetCatalog.Find(key);
            if (desc == null) continue;
            var view = new WidgetView(this, desc);
            panel.Children.Add(view);
            _allWidgets.Add(view);
        }
    }

    private void UpdatePerfActive()
    {
        var keys = _allWidgets.Where(w => w.Descriptor.Kind == WidgetKind.Gauge).Select(w => w.Key).ToHashSet();
        // The System Load widget (and its drop-down) needs the core metrics sampled too.
        if (_allWidgets.Any(w => w.Descriptor.Kind == WidgetKind.Load))
            foreach (var k in new[] { "cpu", "ram", "gpu", "disk", "net" }) keys.Add(k);
        _perf?.SetActive(keys);

        // Only capture audio while a media widget is on the bar.
        if (_allWidgets.Any(w => w.Descriptor.Kind == WidgetKind.Media)) _audio?.Start();
        else _audio?.Stop();
    }

    private void RefreshDynamicWidgets()
    {
        foreach (var w in _allWidgets) w.RefreshDynamic();
        RefreshDevWidgets();
    }

    private void PersistLayout()
    {
        _settings.LeftWidgets = KeysOf(PanelLeft);
        _settings.CenterWidgets = KeysOf(PanelCenter);
        _settings.RightWidgets = KeysOf(PanelRight);
        _settings.Save();
    }

    private static List<string> KeysOf(AnimatedBarPanel panel) =>
        panel.Children.OfType<WidgetView>().Select(w => w.Key).ToList();

    public void RemoveWidget(WidgetView view)
    {
        ParentPanel(view)?.Children.Remove(view);
        _allWidgets.Remove(view);
        if (ReferenceEquals(view, _overlayOwner)) CloseOverlay();
        UpdatePerfActive();
        PersistLayout();
    }

    private void AddWidget(string key)
    {
        var desc = WidgetCatalog.Find(key);
        if (desc == null) return;
        var view = new WidgetView(this, desc);
        view.SetCustomizing(Customizing);
        PanelCenter.Children.Add(view);
        _allWidgets.Add(view);
        UpdatePerfActive();
        view.RefreshDynamic();
        PersistLayout();
    }

    // =========================================================== the loop

    private void OnTick(object? sender, EventArgs e)
    {
        _tickCount++;
        var now = DateTime.UtcNow;

        var barRect = new RECT
        {
            Left = _monitorBounds.Left,
            Top = _monitorBounds.Top,
            Right = _monitorBounds.Right,
            Bottom = _monitorBounds.Top + _barHeightPx
        };

        var fg = ForegroundProbe.Inspect(barRect, _monitorBounds);
        if (fg.AppName != _activeAppName)
        {
            _activeAppName = fg.AppName;
            foreach (var w in _allWidgets)
                if (w.Descriptor.Kind == WidgetKind.ActiveApp) w.RefreshDynamic();
        }

        bool wantShow;
        switch (_settings.Mode)
        {
            case VisibilityMode.AlwaysOn:
                wantShow = true;
                if (_tickCount % 12 == 0) ReassertTopmost();
                break;
            case VisibilityMode.AutoHide:
                wantShow = EvaluateAutoHide(now);
                break;
            default:
                wantShow = EvaluateDynamic(now, fg);
                if (wantShow && _tickCount % 12 == 0) ReassertTopmost();
                break;
        }

        if (_forceOpen || Customizing) wantShow = true;

        if (wantShow != _shown && !_animating)
            SetShown(wantShow, animate: true);
    }

    private bool EvaluateAutoHide(DateTime now)
    {
        if (!GetCursorPos(out var p)) return _shown;
        bool xInRange = p.X >= _monitorBounds.Left && p.X < _monitorBounds.Right;
        bool atTopEdge = xInRange && p.Y <= _monitorBounds.Top + _settings.TriggerZonePx;
        bool overBar = xInRange && p.Y >= _monitorBounds.Top && p.Y <= _monitorBounds.Top + _barHeightPx;

        if (atTopEdge) _hotSince ??= now;
        else if (!overBar) _hotSince = null;

        if (_shown)
        {
            if (overBar || atTopEdge) { _hideAt = null; return true; }
            _hideAt ??= now.AddMilliseconds(_settings.HideDelayMs);
            return now < _hideAt;
        }
        if (_hotSince != null && (now - _hotSince.Value).TotalMilliseconds >= _settings.RevealHoldMs)
        {
            _hideAt = null;
            return true;
        }
        return false;
    }

    private bool EvaluateDynamic(DateTime now, ForegroundState fg)
    {
        bool obstructed = fg.IsFullscreen || fg.OverlapsBar;
        if (!obstructed) { _hideAt = null; _hotSince = null; return true; }

        // Obstructed: stay hidden, but let the user peek the bar by holding the cursor at the
        // very top edge (like Auto-Hide), then re-hide once they leave.
        if (GetCursorPos(out var p))
        {
            bool xInRange = p.X >= _monitorBounds.Left && p.X < _monitorBounds.Right;
            bool atTopEdge = xInRange && p.Y <= _monitorBounds.Top + _settings.TriggerZonePx;
            bool overBar = xInRange && p.Y >= _monitorBounds.Top && p.Y <= _monitorBounds.Top + _barHeightPx;

            if (atTopEdge) _hotSince ??= now;
            else if (!overBar) _hotSince = null;

            if (_shown)
            {
                if (overBar || atTopEdge) { _hideAt = null; return true; }
                _hideAt ??= now.AddMilliseconds(_settings.HideDelayMs);
                return now < _hideAt;
            }
            if (_hotSince != null && (now - _hotSince.Value).TotalMilliseconds >= _settings.DynamicRevealHoldMs)
            {
                _hideAt = null;
                return true;
            }
        }
        return false;
    }

    private void ReassertTopmost() =>
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    // =========================================================== show/hide

    private void SetShown(bool show, bool animate)
    {
        _shown = show;
        SetClickThrough(!show);
        if (_backdrop) ApplyBackdrop(show);

        double target = show ? 0 : -_settings.BarHeight;
        int ms = animate ? _settings.AnimationMs : 0;

        if (ms <= 0)
        {
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            SlideTransform.Y = target;
            return;
        }

        _animating = true;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        anim.Completed += (_, _) => _animating = false;
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        int updated = enabled ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (updated != ex) SetWindowLong(_hwnd, GWL_EXSTYLE, updated);
    }

    // =========================================================== gauge graph dropdown

    private void OnOverlayPerfUpdate() => _overlayUpdate?.Invoke();

    private static TextBlock StatTb() => new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 10.5 };

    public void ShowGraph(WidgetView view, Metric metric)
    {
        var panel = new StackPanel { Width = 234 };

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock { Text = metric.Name.ToUpperInvariant(), Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB5)), FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var valueTb = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Dock.Left); DockPanel.SetDock(valueTb, Dock.Right);
        head.Children.Add(title); head.Children.Add(valueTb);
        panel.Children.Add(head);

        var graph = new Controls.HistoryGraph { Height = 92, Kind = Widgets.MetricStyle.For(metric.Key).Graph };
        panel.Children.Add(graph);

        var stats = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        var minTb = StatTb(); var avgTb = StatTb(); var maxTb = StatTb();
        avgTb.Margin = new Thickness(14, 0, 0, 0);
        DockPanel.SetDock(minTb, Dock.Left); DockPanel.SetDock(avgTb, Dock.Left); DockPanel.SetDock(maxTb, Dock.Right);
        stats.Children.Add(minTb); stats.Children.Add(avgTb); stats.Children.Add(maxTb);
        panel.Children.Add(stats);

        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 10, 0, 8) });
        var procTitle = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(procTitle);
        var procs = new StackPanel();
        panel.Children.Add(procs);

        void Upd()
        {
            var d = metric.Snapshot();
            graph.SetData(d);
            valueTb.Text = metric.Text;
            valueTb.Foreground = new SolidColorBrush(RingGauge.ColorFor(metric.Percent));
            if (d.Length > 0) { minTb.Text = $"min {d.Min():0}%"; avgTb.Text = $"avg {d.Average():0}%"; maxTb.Text = $"max {d.Max():0}%"; }
        }
        Upd();
        _overlayUpdate = Upd;
        _perf!.Updated += OnOverlayPerfUpdate;
        _overlayClosed = () => { _perf!.Updated -= OnOverlayPerfUpdate; _overlayUpdate = null; };

        LoadTopProcessesInto(metric.Key, procTitle, procs);

        var card = Card(panel, new Thickness(14, 11, 14, 11));
        double off = view.TranslatePoint(new Point(0, 0), BarRoot).X - 6;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - 280));
        OpenOverlay(card, BarRoot, off);
    }

    private void LoadTopProcessesInto(string key, TextBlock titleTb, StackPanel procs)
    {
        titleTb.Text = key == "ram" ? "TOP MEMORY USERS"
            : key == "battery" ? "TOP POWER USERS"
            : key == "net" ? "TOP I/O USERS"
            : $"TOP {WidgetCatalog.Find(key)?.Name.ToUpperInvariant()} USERS";
        procs.Children.Clear();
        procs.Children.Add(new TextBlock { Text = "…", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 11.5 });

        var owner = _overlayOwner;
        _ = ProcessUsage.TopAsync(key, 4).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!OverlayPopup.IsOpen || !ReferenceEquals(owner, _overlayOwner)) return;
                procs.Children.Clear();
                var list = t.IsCompletedSuccessfully ? t.Result : new List<ProcUsage>();
                if (list.Count == 0) { procs.Children.Add(new TextBlock { Text = "No data", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 11.5 }); return; }
                foreach (var p in list)
                {
                    var dp = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 1.5, 0, 1.5) };
                    var name = new TextBlock { Text = p.Name, Foreground = Brushes.White, FontSize = 12 };
                    var val = new TextBlock { Text = p.Value, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB5)), FontSize = 12, FontWeight = FontWeights.SemiBold };
                    DockPanel.SetDock(name, Dock.Left);
                    DockPanel.SetDock(val, Dock.Right);
                    dp.Children.Add(val);
                    dp.Children.Add(name);
                    procs.Children.Add(dp);
                }
            }));
        });
    }

    // =========================================================== customize mode

    public void ToggleCustomize() => SetCustomize(!Customizing);

    private void SetCustomize(bool on)
    {
        Customizing = on;
        foreach (var w in _allWidgets) w.SetCustomizing(on);

        if (on)
        {
            CloseOverlay();
            PlusPopup.PlacementTarget = BarGrid;
            PlusPopup.HorizontalOffset = (BarGrid.ActualWidth / 2.0) - 110; // centre the pill under the bar
            PlusPopup.IsOpen = true;
            PopScale(PlusScale);
        }
        else
        {
            AddPopup.IsOpen = false;
            PlusPopup.IsOpen = false;
            CloseOverlay();
            PersistLayout();
        }
    }

    private void PlusButton_Click(object sender, RoutedEventArgs e) => OpenAddMenu();

    private void LayoutButton_Click(object sender, RoutedEventArgs e) => ShowLayoutMenu((UIElement)sender);

    private void ExitButton_Click(object sender, RoutedEventArgs e) => SetCustomize(false);

    public void OpenAddMenu()
    {
        if (AddPopup.IsOpen) { AddPopup.IsOpen = false; return; }
        BuildAddList();
        AddPopup.PlacementTarget = PlusButton;
        AddPopup.IsOpen = true;
        GrowFromTop(AddScale, AddCard);
    }

    private void BuildAddList()
    {
        AddList.Children.Clear();
        var present = new HashSet<string>(_allWidgets.Select(w => w.Key));
        int total = 0;

        foreach (var category in WidgetCatalog.Categories)
        {
            var items = WidgetCatalog.All.Where(d => d.Category == category && !present.Contains(d.Key)).ToList();
            if (items.Count == 0) continue;

            AddList.Children.Add(new TextBlock
            {
                Text = category.ToUpperInvariant(),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x80)),
                FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, total == 0 ? 0 : 10, 0, 5)
            });

            var wrap = new System.Windows.Controls.WrapPanel();
            foreach (var desc in items)
            {
                var key = desc.Key;
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(4),
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand,
                    ToolTip = desc.Name,
                    MaxWidth = 150,
                    ClipToBounds = true,
                    Child = new WidgetView(this, desc, preview: true) { IsHitTestVisible = false }
                };
                chip.MouseEnter += (_, _) => chip.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                chip.MouseLeave += (_, _) => chip.Background = Brushes.Transparent;
                chip.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; AddWidget(key); AddPopup.IsOpen = false; };
                wrap.Children.Add(chip);
                total++;
            }
            AddList.Children.Add(wrap);
        }

        if (total == 0)
            AddList.Children.Add(new TextBlock { Text = "All widgets added", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(4) });
    }

    // =========================================================== drag reorder

    public void BeginWidgetDrag(WidgetView view, MouseButtonEventArgs e)
    {
        _drag = view;
        _dragActive = true;
        _dragGrabX = e.GetPosition(view).X;
        _dragSize = new Size(view.ActualWidth, view.ActualHeight);

        // Where would it land right now?
        var pt = e.GetPosition(BarGrid);
        var target = ZoneFor(pt.X);
        int idx = InsertionIndex(target, pt.X);

        // Lift the widget out of the flow and onto the drag layer.
        ParentPanel(view)?.Children.Remove(view);
        view.BeginAnimation(OpacityProperty, null);
        view.Opacity = 0.92;
        view.RenderTransformOrigin = new Point(0.5, 0.5);
        view.RenderTransform = new ScaleTransform(1.06, 1.06);
        view.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.6, Color = Colors.Black };
        DragLayer.Children.Add(view);
        Canvas.SetTop(view, Math.Max(0, (BarGrid.ActualHeight - _dragSize.Height) / 2.0));
        Canvas.SetLeft(view, pt.X - _dragGrabX);

        // Dashed outline that previews the landing slot.
        var accent = (Color)ColorConverter.ConvertFromString(_settings.AccentColor);
        _dropIndicator = new System.Windows.Shapes.Rectangle
        {
            Width = _dragSize.Width,
            Height = _dragSize.Height,
            RadiusX = _settings.WidgetCornerRadius,
            RadiusY = _settings.WidgetCornerRadius,
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B)),
            StrokeThickness = 1.6,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (idx > target.Children.Count) idx = target.Children.Count;
        target.Children.Insert(idx, _dropIndicator);

        CaptureMouse();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragActive || _drag == null) return;
        var pt = e.GetPosition(BarGrid);
        Canvas.SetLeft(_drag, Math.Clamp(pt.X - _dragGrabX, 0, Math.Max(0, BarGrid.ActualWidth - _dragSize.Width)));

        UpdateDropIndicator(pt.X);
    }

    private void UpdateDropIndicator(double x)
    {
        if (_dropIndicator == null) return;
        var target = ZoneFor(x);
        int idx = InsertionIndex(target, x);

        var cur = VisualTreeHelper.GetParent(_dropIndicator) as AnimatedBarPanel;
        int curIdx = cur?.Children.IndexOf(_dropIndicator) ?? -1;
        if (ReferenceEquals(cur, target) && idx == curIdx) return;

        cur?.Children.Remove(_dropIndicator);
        if (idx > target.Children.Count) idx = target.Children.Count;
        target.Children.Insert(idx, _dropIndicator);
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive || _drag == null) return;
        _dragActive = false;
        ReleaseMouseCapture();

        // Drop where the indicator is.
        var panel = VisualTreeHelper.GetParent(_dropIndicator) as AnimatedBarPanel ?? PanelCenter;
        int idx = _dropIndicator != null ? panel.Children.IndexOf(_dropIndicator) : panel.Children.Count;
        if (_dropIndicator != null) panel.Children.Remove(_dropIndicator);
        _dropIndicator = null;

        DragLayer.Children.Remove(_drag);
        _drag.RenderTransform = null;
        _drag.Effect = null;
        _drag.Opacity = 1;
        _drag.ResetBackground();

        if (idx < 0 || idx > panel.Children.Count) idx = panel.Children.Count;
        panel.Children.Insert(idx, _drag);

        _drag = null;
        PersistLayout();
    }

    private AnimatedBarPanel ZoneFor(double x)
    {
        double w = BarGrid.ActualWidth;
        if (x < w / 3.0) return PanelLeft;
        if (x < 2.0 * w / 3.0) return PanelCenter;
        return PanelRight;
    }

    /// <summary>Index among a panel's widgets where the cursor currently points.</summary>
    private int InsertionIndex(AnimatedBarPanel panel, double x)
    {
        int idx = 0;
        foreach (var c in panel.Children.OfType<WidgetView>())
        {
            try
            {
                double center = c.TranslatePoint(new Point(c.ActualWidth / 2.0, 0), BarGrid).X;
                if (x > center) idx++;
            }
            catch { }
        }
        return idx;
    }

    private static AnimatedBarPanel? ParentPanel(WidgetView v) =>
        VisualTreeHelper.GetParent(v) as AnimatedBarPanel;

    // =========================================================== animations

    private void GrowFromTop(ScaleTransform scale, FrameworkElement card)
    {
        card.RenderTransformOrigin = new Point(0.5, 0);
        if (_fluid)
        {
            // Springy Dynamic-Island-style emergence.
            var ease = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.0, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
        else
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
    }

    private static void PopScale(ScaleTransform scale)
    {
        var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // =========================================================== context menu / mode

    private void ShowScrim()
    {
        Scrim.Width = SystemParameters.VirtualScreenWidth;
        Scrim.Height = SystemParameters.VirtualScreenHeight;
        ScrimPopup.Placement = PlacementMode.Absolute;
        ScrimPopup.HorizontalOffset = SystemParameters.VirtualScreenLeft;
        ScrimPopup.VerticalOffset = SystemParameters.VirtualScreenTop;
        ScrimPopup.IsOpen = true;
    }

    private bool _overlayFocusable;
    private bool _pendingHover;

    private void OpenOverlay(UIElement content, UIElement target, double horizontalOffset, bool focusable = false)
    {
        if (_overlayClosing) FinishCloseOverlay();   // snap any in-flight close

        _overlayHover = _pendingHover;
        if (!_pendingHover) _overlayOwner = null;
        _pendingHover = false;
        _overlayHideTimer.Stop();

        OverlayHost.Content = content;
        OverlayPopup.PlacementTarget = target;
        OverlayPopup.Placement = PlacementMode.Bottom;
        OverlayPopup.HorizontalOffset = horizontalOffset;
        OverlayPopup.VerticalOffset = _fluid ? -2 : 4;   // overlap the bar slightly so it's seamless

        if (!_overlayHover) ShowScrim();   // hover dropdowns are non-modal (no click-catcher)
        OverlayPopup.IsOpen = true;
        _forceOpen = true;

        // Text-editing overlays (settings, note) need the window to accept keyboard focus.
        _overlayFocusable = focusable;
        if (focusable && _hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE & ~WS_EX_TRANSPARENT);
            Dispatcher.BeginInvoke(new Action(() => { SetForegroundWindow(_hwnd); Activate(); }), DispatcherPriority.Input);
        }

        OverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        OverlayHost.BeginAnimation(OpacityProperty, null);
        OverlayScale.ScaleX = OverlayScale.ScaleY = 1;
        OverlayHost.Opacity = 1;

        if (content is Controls.FluidCard fc)
        {
            // Fluid grow: the body extends downward, shoulders stay a fixed size; longer + eased.
            fc.BeginAnimation(Controls.FluidCard.RevealProperty, null);
            fc.Reveal = 0;
            fc.BeginAnimation(Controls.FluidCard.RevealProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(440)) { EasingFunction = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut } });
            OverlayHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }
        else
        {
            GrowFromTop(OverlayScale, OverlayHost);
        }
    }

    private void OpenOverlayCentered(FrameworkElement content, double width, bool focusable = false)
    {
        double extra = content is Controls.FluidCard ? 44 : 0; // fluid card flares wider than its content
        double off = (BarRoot.ActualWidth / 2.0) - ((width + extra) / 2.0);
        OpenOverlay(content, BarRoot, off, focusable);
    }

    /// <summary>Builds a dropdown card — a fluid shape that extends from the bar (fluid themes)
    /// or a plain rounded card otherwise.</summary>
    private FrameworkElement Card(UIElement content, Thickness padding)
    {
        // Vector-drawn translucent material that matches the theme (clean anti-aliased corners).
        var fill = new SolidColorBrush(_dropMaterial); fill.Freeze();
        Brush? stroke = _dropOutline is Color oc ? new SolidColorBrush(oc) : null;
        stroke?.Freeze();
        var shadow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black };

        if (_shoulder)
            return new Controls.FluidCard
            {
                Fill = fill, Stroke = stroke, StrokeThickness = 1.2, BodyRadius = 16, Shoulder = 22,
                ContentPadding = padding, Child = content, Effect = shadow
            };

        return new Border
        {
            Background = fill,
            BorderBrush = stroke,
            BorderThickness = stroke != null ? new Thickness(1) : new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Padding = padding,
            Child = content,
            Effect = shadow
        };
    }

    private double ShoulderExtra => _shoulder ? 44 : 0;

    private void RecenterOverlay(double width) =>
        OverlayPopup.HorizontalOffset = (BarRoot.ActualWidth / 2.0) - ((width + ShoulderExtra) / 2.0);

    private Action? _overlayClosed;

    private void CloseOverlay()
    {
        if (!OverlayPopup.IsOpen) { FinishCloseOverlay(); return; }
        _overlayClosing = true;
        _overlayHideTimer.Stop();
        ScrimPopup.IsOpen = false;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        if (OverlayHost.Content is Controls.FluidCard fc)
        {
            // Shrink the body back into the bar (bevels stay fixed).
            var ra = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            ra.Completed += (_, _) => { if (_overlayClosing) FinishCloseOverlay(); };
            fc.BeginAnimation(Controls.FluidCard.RevealProperty, ra);
            OverlayHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
        }
        else
        {
            var sy = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
            sy.Completed += (_, _) => { if (_overlayClosing) FinishCloseOverlay(); };
            OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
            OverlayHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        }
    }

    private void FinishCloseOverlay()
    {
        _overlayClosing = false;
        OverlayPopup.IsOpen = false;
        ScrimPopup.IsOpen = false;
        OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        OverlayHost.BeginAnimation(OpacityProperty, null);
        OverlayScale.ScaleY = 1; OverlayHost.Opacity = 1;
        OverlayHost.Content = null;
        _overlayOwner = null; _overlayHover = false;
        var cb = _overlayClosed; _overlayClosed = null;
        cb?.Invoke();
        if (_overlayFocusable && _hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
            _overlayFocusable = false;
        }
        if (!Customizing) _forceOpen = false;
    }

    private void Scrim_Click(object sender, MouseButtonEventArgs e) => CloseOverlay();

    // ---- themed menu construction (no OS menus / windows) ----

    private sealed record MenuRow(string Label, Action? OnClick, bool Checked = false, bool Separator = false, bool Accent = false);

    private static MenuRow Sep() => new("", null, Separator: true);

    private FrameworkElement BuildMenuCard(IEnumerable<MenuRow> rows, double width = 230)
    {
        var stack = new StackPanel { Width = width };
        foreach (var row in rows)
        {
            if (row.Separator)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                    Margin = new Thickness(8, 5, 8, 5)
                });
                continue;
            }

            var rowBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            var content = new DockPanel { LastChildFill = true };
            var check = new TextBlock
            {
                Text = row.Checked ? "✓" : "",
                Width = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(check, Dock.Left);
            content.Children.Add(check);
            content.Children.Add(new TextBlock
            {
                Text = row.Label,
                Foreground = row.Accent ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) : Brushes.White,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            rowBorder.Child = content;

            var capt = row;
            rowBorder.MouseEnter += (_, _) => rowBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            rowBorder.MouseLeave += (_, _) => rowBorder.Background = Brushes.Transparent;
            rowBorder.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                CloseOverlay();
                capt.OnClick?.Invoke();
            };
            stack.Children.Add(rowBorder);
        }

        return Card(stack, new Thickness(6));
    }

    private void OnBarRightClick(object sender, MouseButtonEventArgs e)
    {
        double cursorX = e.GetPosition(BarRoot).X;
        var rows = new List<MenuRow>
        {
            new("Settings…", () => OpenSettings()),
            new(Customizing ? "Exit Customize" : "Customize Widgets", ToggleCustomize, Checked: Customizing),
            Sep(),
            new("Always On", () => ChangeMode(VisibilityMode.AlwaysOn), Checked: _settings.Mode == VisibilityMode.AlwaysOn),
            new("Auto-Hide", () => ChangeMode(VisibilityMode.AutoHide), Checked: _settings.Mode == VisibilityMode.AutoHide),
            new("Dynamic",   () => ChangeMode(VisibilityMode.Dynamic),  Checked: _settings.Mode == VisibilityMode.Dynamic),
            Sep(),
            new("About Lintel", ShowAbout),
            new("Quit Lintel", () => Application.Current.Shutdown(), Accent: true)
        };
        double off = Math.Clamp(cursorX - 20, 8, Math.Max(8, BarRoot.ActualWidth - 250));
        OpenOverlay(BuildMenuCard(rows), BarRoot, off);
    }

    // ---- quick layout presets ----

    private static readonly (string Name, string[] L, string[] C, string[] R)[] Presets =
    {
        ("Balanced",    new[]{"activeapp"}, new[]{"cpu","ram","gpu"},                  new[]{"mode","date","clock","settings"}),
        ("Minimal",     new[]{"activeapp"}, System.Array.Empty<string>(),             new[]{"clock","settings"}),
        ("Performance", new[]{"activeapp"}, new[]{"cpu","ram","gpu","disk","net"},     new[]{"clock","settings"}),
        ("Centered",    System.Array.Empty<string>(), new[]{"activeapp","cpu","ram","clock"}, new[]{"settings"}),
        ("Everything",  new[]{"activeapp"}, new[]{"cpu","ram","gpu","disk","net","battery"}, new[]{"mode","date","clock","settings"}),
    };

    private void ShowLayoutMenu(UIElement target)
    {
        var rows = Presets.Select(p => new MenuRow(p.Name, () => ApplyPreset(p.Name))).ToList();
        double off = target.TranslatePoint(new Point(0, 0), BarRoot).X - 20;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - 200));
        OpenOverlay(BuildMenuCard(rows, 190), BarRoot, off);
    }

    private void ApplyPreset(string name)
    {
        var p = System.Array.Find(Presets, x => x.Name == name);
        if (p.Name == null) return;
        _settings.LeftWidgets = p.L.ToList();
        _settings.CenterWidgets = p.C.ToList();
        _settings.RightWidgets = p.R.ToList();
        _settings.Save();
        RebuildWidgets();
    }

    // ---- themes ----

    private void ThemeButton_Click(object sender, RoutedEventArgs e) => ShowThemeMenu((UIElement)sender);

    private void ShowThemeMenu(UIElement target)
    {
        var rows = new List<MenuRow>();
        foreach (LintelTheme t in Themes.Selectable)
        {
            var captured = t;
            rows.Add(new MenuRow(Themes.DisplayName(t), () => ChangeTheme(captured), Checked: _settings.Theme == t));
        }
        double off = target.TranslatePoint(new Point(0, 0), BarRoot).X - 20;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - 200));
        OpenOverlay(BuildMenuCard(rows, 190), BarRoot, off);
    }

    public void ChangeTheme(LintelTheme theme)
    {
        _settings.Theme = theme;
        _settings.Save();
        ApplySettings();
    }

    // ---- about ----

    private void ShowAbout()
    {
        var stack = new StackPanel { Width = 300 };
        stack.Children.Add(new TextBlock { Text = "Lintel", FontWeight = FontWeights.Bold, FontSize = 20, Foreground = Brushes.White });
        stack.Children.Add(new TextBlock { Text = "A macOS / Linux-style top bar for Windows 11.", Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC5)), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = "Version 1.1  ·  Dynamic widget edition", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) });

        var card = Card(stack, new Thickness(18, 16, 18, 16));
        OpenOverlayCentered(card, 336);
    }

    // ---- interactive widgets: note / app tabs / workspaces ----

    public void ShowNote(WidgetView view)
    {
        var tb = new TextBox
        {
            Text = _settings.NoteText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 280, Height = 130,
            Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x30)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        tb.TextChanged += (_, _) => _settings.NoteText = tb.Text;

        var panel = new StackPanel { Width = 296 };
        panel.Children.Add(new TextBlock { Text = "QUICK NOTE", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) });
        panel.Children.Add(tb);

        var card = Card(panel, new Thickness(12));

        _overlayClosed = () => { _settings.Save(); RefreshDynamicWidgets(); };
        OpenOverlay(card, view, 0, focusable: true);
        Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); System.Windows.Input.Keyboard.Focus(tb); tb.CaretIndex = tb.Text.Length; }), DispatcherPriority.Input);
    }

    public void ShowWindowSwitcher(WidgetView view)
    {
        var windows = WindowList.Enumerate().Take(14).ToList();
        var rows = new List<MenuRow>();
        if (windows.Count == 0)
            rows.Add(new MenuRow("No open windows", null));
        else
            foreach (var w in windows)
            {
                var handle = w.Handle;
                string label = string.IsNullOrEmpty(w.Process) ? w.Title : $"{w.Title}";
                rows.Add(new MenuRow(label, () => WindowList.Activate(handle)));
            }
        double off = view.TranslatePoint(new Point(0, 0), BarRoot).X - 10;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - 280));
        OpenOverlay(BuildMenuCard(rows, 270), BarRoot, off);
    }

    private int _winCount;
    private DateTime _winCountAt;

    public int OpenWindowCount()
    {
        if ((DateTime.UtcNow - _winCountAt).TotalMilliseconds > 1500)
        {
            _winCount = WindowList.Count();
            _winCountAt = DateTime.UtcNow;
        }
        return _winCount;
    }

    public void SwitchWorkspace(int direction) => WindowList.SwitchDesktop(direction);

    public void ShowResourcePanel(WidgetView view)
    {
        string[] keys = { "cpu", "ram", "gpu", "disk", "net" };
        var panel = new StackPanel { Width = 268 };
        panel.Children.Add(new TextBlock { Text = "SYSTEM LOAD", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) });

        var graphs = new List<(Metric m, Controls.HistoryGraph g, TextBlock v)>();
        for (int ki = 0; ki < keys.Length; ki++)
        {
            var key = keys[ki];
            if (ki > 0)
                panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 0, 0, 9) });

            var metric = _perf!.Get(key);
            var sig = Widgets.MetricStyle.For(key).Signature;

            var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(new System.Windows.Shapes.Path { Data = Widgets.Icons.Get(key), Fill = new SolidColorBrush(sig), Stretch = Stretch.Uniform, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
            var nameVal = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            nameVal.Children.Add(new TextBlock { Text = metric.Name, Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeights.SemiBold });
            var valTb = new TextBlock { Text = metric.Text, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB5)), FontSize = 10.5 };
            nameVal.Children.Add(valTb);
            head.Children.Add(nameVal);
            Grid.SetColumn(head, 0);
            rowGrid.Children.Add(head);

            var g = new Controls.HistoryGraph { Height = 34, Kind = Widgets.MetricStyle.For(key).Graph, VerticalAlignment = VerticalAlignment.Center };
            g.SetData(metric.Snapshot());
            Grid.SetColumn(g, 1);
            rowGrid.Children.Add(g);

            panel.Children.Add(rowGrid);
            graphs.Add((metric, g, valTb));
        }

        var card = Card(panel, new Thickness(14, 12, 14, 8));

        void Updater()
        {
            foreach (var (m, g, v) in graphs) { g.SetData(m.Snapshot()); v.Text = m.Text; }
        }
        _perf!.Updated += Updater;
        _overlayClosed = () => _perf!.Updated -= Updater;

        double off = view.TranslatePoint(new Point(0, 0), BarRoot).X - 10;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - 300));
        OpenOverlay(card, BarRoot, off);
    }

    // ---- expanded media player ----

    public void ShowMediaPanel(WidgetView view)
    {
        var accent = (Color)ColorConverter.ConvertFromString(_settings.AccentColor);
        const double W = 320;
        var panel = new StackPanel { Width = W };

        // header: cover + title/artist
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var coverBorder = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Top };
        var coverImg = new Image { Stretch = Stretch.UniformToFill };
        var coverPh = new System.Windows.Shapes.Path { Data = Widgets.Icons.Get("media"), Fill = new SolidColorBrush(accent), Stretch = Stretch.Uniform, Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var coverInner = new Grid(); coverInner.Children.Add(coverPh); coverInner.Children.Add(coverImg);
        coverBorder.Child = coverInner;
        Grid.SetColumn(coverBorder, 0);
        header.Children.Add(coverBorder);

        var titleStack = new StackPanel { Margin = new Thickness(12, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = W - 90 };
        var artistTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA6)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = W - 90, Margin = new Thickness(0, 2, 0, 0) };
        titleStack.Children.Add(titleTb);
        titleStack.Children.Add(artistTb);
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);
        panel.Children.Add(header);

        // visualizer
        var viz = new Controls.Visualizer { Height = 38, Bars = 34, BarColor = accent, Margin = new Thickness(0, 12, 0, 10) };
        panel.Children.Add(viz);

        // progress bar
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var trackGrid = new Grid(); trackGrid.Children.Add(track); trackGrid.Children.Add(fill);
        panel.Children.Add(trackGrid);

        var times = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 0) };
        var posTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 10.5 };
        var durTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 10.5 };
        DockPanel.SetDock(posTb, Dock.Left); DockPanel.SetDock(durTb, Dock.Right);
        times.Children.Add(posTb); times.Children.Add(durTb);
        panel.Children.Add(times);

        // controls
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2) };
        var playPath = new System.Windows.Shapes.Path { Fill = Brushes.White, Stretch = Stretch.Uniform };
        controls.Children.Add(MediaCtrl(Geometry.Parse("M11,2 L4,7 L11,12 Z M3,2 L1,2 L1,12 L3,12 Z"), () => _media!.Previous(), 16));
        controls.Children.Add(MediaCtrlElem(playPath, () => _media!.TogglePlay(), 22));
        controls.Children.Add(MediaCtrl(Geometry.Parse("M2,2 L9,7 L2,12 Z M11,2 L13,2 L13,12 L11,12 Z"), () => _media!.Next(), 16));
        panel.Children.Add(controls);

        var card = Card(panel, new Thickness(16, 14, 16, 12));

        void Update()
        {
            var m = _media!.Current;
            titleTb.Text = m.HasMedia ? m.Title : "Nothing playing";
            artistTb.Text = m.Artist;
            coverImg.Source = m.Cover;
            coverPh.Visibility = m.Cover == null ? Visibility.Visible : Visibility.Collapsed;
            coverPh.Fill = new SolidColorBrush(m.Accent);
            viz.BarColor = m.Accent;
            viz.Active = m.IsPlaying;
            fill.Background = new SolidColorBrush(m.Accent);
            playPath.Data = m.IsPlaying
                ? Geometry.Parse("M2,1 L5,1 L5,13 L2,13 Z M9,1 L12,1 L12,13 L9,13 Z")   // pause
                : Geometry.Parse("M3,1 L13,7 L3,13 Z");                                   // play
            double frac = m.Duration.TotalSeconds > 0 ? Math.Clamp(m.Position.TotalSeconds / m.Duration.TotalSeconds, 0, 1) : 0;
            fill.Width = (W - 32) * frac;
            posTb.Text = Fmt(m.Position);
            durTb.Text = Fmt(m.Duration);
        }
        Update();
        _media!.Changed += Update;
        _overlayClosed = () => _media!.Changed -= Update;

        double off = view.TranslatePoint(new Point(0, 0), BarRoot).X - 20;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - W - 20));
        OpenOverlay(card, BarRoot, off);
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // =========================================================== Claude usage widget

    private ClaudeStats? _claudeStats;
    private DateTime _claudeAt;
    private GitHubStats? _githubStats;
    private DateTime _githubAt;

    private static readonly Color ClaudeAccent = Color.FromRgb(0xD9, 0x77, 0x57);
    private static readonly Color GitHubAccent = Color.FromRgb(0x39, 0xD3, 0x53);

    private static string Compact(double n)
    {
        if (n >= 1_000_000_000) return (n / 1_000_000_000).ToString("0.#") + "B";
        if (n >= 1_000_000) return (n / 1_000_000).ToString("0.#") + "M";
        if (n >= 1_000) return (n / 1_000).ToString("0.#") + "k";
        return ((long)n).ToString();
    }

    private static string Countdown(DateTime? reset)
    {
        if (reset is not DateTime r) return "—";
        var span = r - DateTime.Now;
        if (span <= TimeSpan.Zero) return "now";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";
    }

    public void ShowClaudePanel(WidgetView view)
    {
        const double W = 300;
        var panel = new StackPanel { Width = W };
        panel.Children.Add(SectionLabel("CLAUDE USAGE"));

        if (!ClaudeUsage.Available)
        {
            panel.Children.Add(Hint("Claude Code logs not found.\nUsage appears once you've used Claude Code on this PC."));
            OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);
            return;
        }

        var bigVal = new TextBlock { Text = "…", Foreground = Brushes.White, FontSize = 26, FontWeight = FontWeights.Bold };
        var bigLbl = new TextBlock { Text = "loading", Foreground = Sub(), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 0) };
        var bigRow = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
        bigRow.Children.Add(bigVal);
        bigRow.Children.Add(bigLbl);
        panel.Children.Add(bigRow);

        var resetRow = StatLine("Window frees up", "—");
        var todayRow = StatLine("Today", "—");
        panel.Children.Add(resetRow.row);
        panel.Children.Add(todayRow.row);

        panel.Children.Add(new Border { Height = 1, Background = HairLine(), Margin = new Thickness(0, 10, 0, 10) });
        var heat = new Controls.Heatmap { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(heat);
        var heatCaption = new TextBlock { Text = "last 17 weeks", Foreground = Sub(), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(heatCaption);

        panel.Children.Add(OpenButton(ClaudeApp.Installed ? "Open Claude" : "Open claude.ai", ClaudeAccent, OpenClaude));

        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);

        void Apply(ClaudeStats s)
        {
            long limit = _settings.ClaudeTokenLimit;
            if (limit > 0)
            {
                long left = Math.Max(0, limit - s.WindowUsed);
                bigVal.Text = Compact(left);
                bigLbl.Text = $"tokens left  ·  {Compact(s.WindowUsed)} of {Compact(limit)} used";
            }
            else
            {
                bigVal.Text = Compact(s.WindowUsed);
                bigLbl.Text = "tokens this session (5h window)";
            }
            resetRow.val.Text = Countdown(s.WindowReset);
            todayRow.val.Text = Compact(s.Today) + " tokens";
            heat.SetData(s.Daily, ClaudeAccent);
            if (!s.HasData) { bigVal.Text = "0"; bigLbl.Text = "no usage recorded yet"; }
        }

        if (_claudeStats != null && (DateTime.UtcNow - _claudeAt).TotalSeconds < 120) Apply(_claudeStats);
        ClaudeUsage.LoadAsync().ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            _claudeStats = t.Result; _claudeAt = DateTime.UtcNow;
            Dispatcher.BeginInvoke(new Action(() => Apply(t.Result)));
        });
    }

    // =========================================================== GitHub widget

    public void ShowGitHubPanel(WidgetView view)
    {
        const double W = 320;
        var panel = new StackPanel { Width = W };
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var hLeft = SectionLabel("GITHUB"); hLeft.Margin = new Thickness(2, 0, 0, 0);
        DockPanel.SetDock(hLeft, Dock.Left);
        var loginTb = new TextBlock { Text = "", Foreground = Sub(), FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(loginTb, Dock.Right);
        header.Children.Add(hLeft); header.Children.Add(loginTb);
        panel.Children.Add(header);

        if (!GitHubService.GhAvailable)
            panel.Children.Add(Hint("GitHub CLI (gh) not found.\nInstall it and run `gh auth login` to enable cloning, repo creation and your contribution graph."));

        // contribution heatmap
        var heat = new Controls.Heatmap { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(heat);
        var totalTb = new TextBlock { Text = GitHubService.GhAvailable ? "loading contributions…" : "", Foreground = Sub(), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(totalTb);

        panel.Children.Add(new Border { Height = 1, Background = HairLine(), Margin = new Thickness(0, 12, 0, 10) });

        // clone row
        panel.Children.Add(new TextBlock { Text = "CLONE A REPO", Foreground = Sub(), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) });
        var cloneBox = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x30)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), FontSize = 12.5,
            Height = 30, VerticalContentAlignment = VerticalAlignment.Center
        };
        cloneBox.SetValue(System.Windows.Controls.Primitives.TextBoxBase.AutoWordSelectionProperty, false);
        var cloneGrid = new Grid();
        cloneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cloneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(cloneBox, 0); cloneGrid.Children.Add(cloneBox);
        var placeholder = new TextBlock { Text = "owner/repo or URL", Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x86)), FontSize = 12.5, IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        cloneBox.TextChanged += (_, _) => placeholder.Visibility = string.IsNullOrEmpty(cloneBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(placeholder, 0); cloneGrid.Children.Add(placeholder);
        var cloneBtn = SmallButton("Clone", GitHubAccent);
        Grid.SetColumn(cloneBtn, 1); cloneBtn.Margin = new Thickness(8, 0, 0, 0); cloneGrid.Children.Add(cloneBtn);
        panel.Children.Add(cloneGrid);

        var status = new TextBlock { Text = "", Foreground = Sub(), FontSize = 11, Margin = new Thickness(2, 7, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);

        // create-from-folder row
        var createBtn = OpenButton("New repo from a folder…", GitHubAccent, null);
        panel.Children.Add(createBtn);

        string cloneTarget = string.IsNullOrWhiteSpace(_settings.CloneTargetFolder) ? GitHubService.DesktopDir : _settings.CloneTargetFolder;

        cloneBtn.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            var url = cloneBox.Text.Trim();
            if (url.Length == 0) { status.Text = "Enter a repo URL or owner/repo."; return; }
            status.Foreground = Sub(); status.Text = $"Cloning into {cloneTarget}…";
            var r = await GitHubService.CloneAsync(url, cloneTarget);
            status.Foreground = r.Ok ? new SolidColorBrush(GitHubAccent) : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            status.Text = r.Ok ? "Cloned to " + cloneTarget : FirstLine(r.StdErr, "Clone failed");
            if (r.Ok) cloneBox.Clear();
        };

        createBtn.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to publish as a new repo", InitialDirectory = GitHubService.DesktopDir };
            FocusWindowForDialog();
            if (dlg.ShowDialog(this) != true) return;
            status.Foreground = Sub(); status.Text = $"Creating repo from {System.IO.Path.GetFileName(dlg.FolderName)}…";
            var r = await GitHubService.CreateFromFolderAsync(dlg.FolderName, isPrivate: true);
            status.Foreground = r.Ok ? new SolidColorBrush(GitHubAccent) : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            status.Text = r.Ok ? "Created & pushed to GitHub." : FirstLine(r.StdErr, "Could not create repo");
        };

        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);

        void Apply(GitHubStats s)
        {
            if (s.HasData)
            {
                heat.SetData(s.Daily, GitHubAccent);
                totalTb.Text = $"{s.Total:N0} contributions in the last year";
                loginTb.Text = s.Login.Length > 0 ? "@" + s.Login : "";
            }
            else if (s.Error != null)
            {
                totalTb.Text = s.Error;
            }
        }

        if (GitHubService.GhAvailable)
        {
            if (_githubStats != null && (DateTime.UtcNow - _githubAt).TotalSeconds < 600) Apply(_githubStats);
            GitHubService.LoadContributionsAsync().ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                _githubStats = t.Result; _githubAt = DateTime.UtcNow;
                Dispatcher.BeginInvoke(new Action(() => Apply(t.Result)));
            });
        }
    }

    // ---- dev-widget shared UI helpers ----

    private static SolidColorBrush Sub() => new(Color.FromRgb(0x8E, 0x8E, 0x93));
    private static SolidColorBrush HairLine() => new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    private static TextBlock SectionLabel(string text) =>
        new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) };

    private static TextBlock Hint(string text) =>
        new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB5)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 4) };

    private (Grid row, TextBlock val) StatLine(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, Foreground = Sub(), FontSize = 12 };
        var v = new TextBlock { Text = value, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(l, 0); Grid.SetColumn(v, 1);
        row.Children.Add(l); row.Children.Add(v);
        return (row, v);
    }

    private Border OpenButton(string text, Color accent, Action? onClick)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)),
            Padding = new Thickness(0, 9, 0, 9),
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }
        };
        if (onClick != null) b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private Border SmallButton(string text, Color accent)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Height = 30,
            Background = new SolidColorBrush(accent),
            Padding = new Thickness(14, 0, 14, 0),
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    private void OpenDevOverlay(FrameworkElement card, WidgetView view, double width)
    {
        double off = view.TranslatePoint(new Point(0, 0), BarRoot).X - 20;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - width - 28));
        // dev panels have inputs/buttons → make the overlay focusable so clicks register
        OpenOverlay(card, BarRoot, off, focusable: true);
    }

    private void FocusWindowForDialog()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE & ~WS_EX_TRANSPARENT);
        SetForegroundWindow(_hwnd);
        Activate();
    }

    private static string FirstLine(string s, string fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var line = s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        return string.IsNullOrEmpty(line) ? fallback : line;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>Launch the Claude desktop app if it's installed, otherwise open claude.ai.</summary>
    private static void OpenClaude()
    {
        var exe = ClaudeApp.ExePath;
        if (exe != null)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true }); return; }
            catch { /* fall through to web */ }
        }
        OpenUrl("https://claude.ai");
    }

    // ---- compact bar labels, refreshed on a slow cadence ----

    private void RefreshDevWidgets()
    {
        var claudeViews = _allWidgets.Where(w => w.Descriptor.Kind == WidgetKind.Claude).ToList();
        var githubViews = _allWidgets.Where(w => w.Descriptor.Kind == WidgetKind.GitHub).ToList();

        if (claudeViews.Count > 0 && ClaudeUsage.Available && (DateTime.UtcNow - _claudeAt).TotalSeconds > 90)
        {
            _claudeAt = DateTime.UtcNow;
            ClaudeUsage.LoadAsync().ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                _claudeStats = t.Result;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    long limit = _settings.ClaudeTokenLimit;
                    string label = limit > 0 ? Compact(Math.Max(0, limit - t.Result.WindowUsed)) : Compact(t.Result.WindowUsed);
                    foreach (var v in claudeViews) v.SetStat(label);
                }));
            });
        }

        if (githubViews.Count > 0 && GitHubService.GhAvailable && (DateTime.UtcNow - _githubAt).TotalSeconds > 600)
        {
            _githubAt = DateTime.UtcNow;
            GitHubService.LoadContributionsAsync().ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                _githubStats = t.Result;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    double todayCount = t.Result.Daily.Length > 0 ? t.Result.Daily[^1] : 0;
                    string label = t.Result.HasData ? ((long)todayCount).ToString() : "—";
                    foreach (var v in githubViews) v.SetStat(label);
                }));
            });
        }
    }

    private Border MediaCtrl(Geometry geo, Action onClick, double size)
    {
        var path = new System.Windows.Shapes.Path { Data = geo, Fill = Brushes.White, Stretch = Stretch.Uniform, Width = size, Height = size };
        return MediaCtrlElem(path, onClick, size);
    }

    private Border MediaCtrlElem(FrameworkElement content, Action onClick, double size)
    {
        if (content is System.Windows.Shapes.Path p) { p.Width = size; p.Height = size; }
        var b = new Border
        {
            Width = size + 22, Height = size + 16, CornerRadius = new CornerRadius((size + 16) / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(7, 0, 7, 0), Cursor = Cursors.Hand,
            Child = content
        };
        ((FrameworkElement)b.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)b.Child).VerticalAlignment = VerticalAlignment.Center;
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private void CycleMode()
    {
        var next = _settings.Mode switch
        {
            VisibilityMode.AlwaysOn => VisibilityMode.AutoHide,
            VisibilityMode.AutoHide => VisibilityMode.Dynamic,
            _ => VisibilityMode.AlwaysOn
        };
        ChangeMode(next);
    }

    public void ChangeMode(VisibilityMode mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        ApplyMode();
        RefreshDynamicWidgets();
    }

    // =========================================================== settings window

    private void OpenSettings(bool advanced = false)
    {
        var panel = new SettingsPanel(_settings);
        panel.SettingsApplied += () =>
        {
            ApplySettings();
            ApplyMode();
            StartupManager.Apply(_settings.LaunchAtStartup);
        };
        panel.CloseRequested += CloseOverlay;
        panel.WidthChanged += RecenterOverlay;
        var card = Card(panel, new Thickness(0));
        OpenOverlayCentered(card, panel.CurrentWidth, focusable: true);
        if (advanced) panel.ExpandToAdvanced();
    }

    public void OpenSettingsFromTray() => OpenSettings();
    public void OpenSettingsAdvancedFromTray() => OpenSettings(advanced: true);
    public void ToggleCustomizeFromTray() => ToggleCustomize();

    // =========================================================== helpers

    private static SolidColorBrush BrushFrom(string hex, Color fallback)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch { var b = new SolidColorBrush(fallback); b.Freeze(); return b; }
    }
}
