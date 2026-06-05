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

    private readonly DispatcherTimer _tick;     // visibility + foreground polling
    private readonly DispatcherTimer _clock;    // 1s content refresh
    private readonly DispatcherTimer _graphHideTimer;

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

    // graph hover state
    private Metric? _graphMetric;
    private WidgetView? _graphOwner;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _tick = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(80) };
        _tick.Tick += OnTick;

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => RefreshDynamicWidgets();

        _graphHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _graphHideTimer.Tick += (_, _) => CloseGraph();

        MouseMove += OnWindowMouseMove;
        PreviewMouseLeftButtonUp += OnWindowMouseUp;
        MouseRightButtonUp += OnBarRightClick;
    }

    // =========================================================== IWidgetHost

    public AppSettings Settings => _settings;
    public bool Customizing { get; private set; }
    public string ActiveAppName => _activeAppName;
    public Metric GetMetric(string key) => _perf!.Get(key);

    public void OnModeClicked() => CycleMode();
    public void OnSettingsClicked() => OpenSettings();

    // =========================================================== lifecycle

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        _appBar = new AppBarManager(_hwnd);
        _perf = new PerfMonitor(Dispatcher);
        _perf.Updated += () => { if (GraphPopup.IsOpen && _graphMetric != null) UpdateGraph(_graphMetric); };

        BuildContextMenu();
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
        _appBar?.Release();
        base.OnClosed(e);
    }

    // =========================================================== settings/layout

    public void ApplySettings()
    {
        BarBackground = BrushFrom(_settings.BackgroundColor, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E));
        Foreground = BrushFrom(_settings.ForegroundColor, Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF7));
        RebuildWidgets();
        ApplyLayout();
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
        var keys = _allWidgets.Where(w => w.Descriptor.Kind == WidgetKind.Gauge).Select(w => w.Key);
        _perf?.SetActive(keys);
    }

    private void RefreshDynamicWidgets()
    {
        foreach (var w in _allWidgets) w.RefreshDynamic();
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
        if (ReferenceEquals(view, _graphOwner)) CloseGraph();
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
        if (obstructed)
        {
            _hideAt ??= now.AddMilliseconds(_settings.DynamicHideDelayMs);
            return now < _hideAt;
        }
        _hideAt = null;
        return true;
    }

    private void ReassertTopmost() =>
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    // =========================================================== show/hide

    private void SetShown(bool show, bool animate)
    {
        _shown = show;
        SetClickThrough(!show);

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

    // =========================================================== graph hover

    public void ShowGraph(WidgetView view, Metric metric)
    {
        _graphHideTimer.Stop();
        if (ReferenceEquals(_graphOwner, view) && GraphPopup.IsOpen) return;

        _graphOwner = view;
        _graphMetric = metric;
        GraphTitle.Text = metric.Name.ToUpperInvariant();
        GraphPopup.PlacementTarget = view;
        UpdateGraph(metric);

        GraphPopup.IsOpen = true;
        GrowFromTop(GraphScale, GraphCard);
    }

    public void HideGraph(WidgetView view)
    {
        if (!ReferenceEquals(_graphOwner, view)) return;
        _graphHideTimer.Stop();
        _graphHideTimer.Start();
    }

    private void CloseGraph()
    {
        _graphHideTimer.Stop();
        GraphPopup.IsOpen = false;
        _graphOwner = null;
        _graphMetric = null;
    }

    private void UpdateGraph(Metric metric)
    {
        var data = metric.Snapshot();
        GraphView.SetData(data);
        GraphValue.Text = metric.Text;
        GraphValue.Foreground = new SolidColorBrush(RingGauge.ColorFor(metric.Percent));
        if (data.Length > 0)
        {
            GraphMin.Text = $"min {data.Min():0}%";
            GraphAvg.Text = $"avg {data.Average():0}%";
            GraphMax.Text = $"max {data.Max():0}%";
        }
    }

    // =========================================================== customize mode

    public void ToggleCustomize() => SetCustomize(!Customizing);

    private void SetCustomize(bool on)
    {
        Customizing = on;
        foreach (var w in _allWidgets) w.SetCustomizing(on);

        if (on)
        {
            CloseGraph();
            PlusPopup.PlacementTarget = BarGrid;
            PlusPopup.HorizontalOffset = (BarGrid.ActualWidth / 2.0) - 38; // centre the 36px button incl. margin
            PlusPopup.IsOpen = true;
            PopScale(PlusScale);
        }
        else
        {
            AddPopup.IsOpen = false;
            PlusPopup.IsOpen = false;
            PersistLayout();
        }
        UpdateContextMenuChecks();
    }

    private void PlusButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenAddMenu();
    }

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
        foreach (var desc in WidgetCatalog.All)
        {
            if (present.Contains(desc.Key)) continue;
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = desc.Name, Foreground = Brushes.White, FontSize = 12 }
            };
            var key = desc.Key;
            chip.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                AddWidget(key);
                AddPopup.IsOpen = false;
            };
            AddList.Children.Add(chip);
        }
        if (AddList.Children.Count == 0)
            AddList.Children.Add(new TextBlock { Text = "All widgets added", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(4) });
    }

    // =========================================================== drag reorder

    public void BeginWidgetDrag(WidgetView view, MouseButtonEventArgs e)
    {
        _drag = view;
        _dragActive = true;
        Panel.SetZIndex(view, 99);
        view.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        view.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.6, Color = Colors.Black };
        CaptureMouse();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragActive || _drag == null) return;
        var pt = e.GetPosition(BarGrid);
        var target = ZoneFor(pt.X);
        var cur = ParentPanel(_drag);
        if (cur == null) return;

        int curIdx = cur.Children.IndexOf(_drag);
        int idx = InsertionIndexExcluding(target, pt.X, _drag);

        if (ReferenceEquals(target, cur) && idx == curIdx) return;

        cur.Children.Remove(_drag);
        if (idx > target.Children.Count) idx = target.Children.Count;
        target.Children.Insert(idx, _drag);
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive || _drag == null) return;
        _dragActive = false;
        ReleaseMouseCapture();
        Panel.SetZIndex(_drag, 0);
        _drag.Effect = null;
        _drag.Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
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

    private int InsertionIndexExcluding(AnimatedBarPanel panel, double x, WidgetView exclude)
    {
        int idx = 0;
        foreach (WidgetView c in panel.Children.OfType<WidgetView>())
        {
            if (ReferenceEquals(c, exclude)) continue;
            try
            {
                double center = c.TranslatePoint(new Point(c.ActualWidth / 2.0, 0), BarGrid).X;
                if (x > center) idx++;
            }
            catch { /* not yet arranged */ }
        }
        return idx;
    }

    private static AnimatedBarPanel? ParentPanel(WidgetView v) =>
        VisualTreeHelper.GetParent(v) as AnimatedBarPanel;

    // =========================================================== animations

    private static void GrowFromTop(ScaleTransform scale, FrameworkElement card)
    {
        card.RenderTransformOrigin = new Point(0.5, 0);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private static void PopScale(ScaleTransform scale)
    {
        var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // =========================================================== context menu / mode

    private ContextMenu? _ctx;
    private MenuItem? _ctxCustomize;

    private void BuildContextMenu()
    {
        _ctx = new ContextMenu();

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => OpenSettings();
        _ctx.Items.Add(settings);

        _ctxCustomize = new MenuItem { Header = "Customize Widgets", IsCheckable = true };
        _ctxCustomize.Click += (_, _) => ToggleCustomize();
        _ctx.Items.Add(_ctxCustomize);

        var visibility = new MenuItem { Header = "Visibility" };
        foreach (VisibilityMode m in Enum.GetValues<VisibilityMode>())
        {
            var item = new MenuItem
            {
                Header = m switch { VisibilityMode.AlwaysOn => "Always On", VisibilityMode.AutoHide => "Auto-Hide", _ => "Dynamic" },
                IsCheckable = true
            };
            var captured = m;
            item.Tag = m;
            item.Click += (_, _) => ChangeMode(captured);
            visibility.Items.Add(item);
        }
        visibility.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem mi in visibility.Items)
                mi.IsChecked = (VisibilityMode)mi.Tag! == _settings.Mode;
        };
        _ctx.Items.Add(visibility);

        _ctx.Items.Add(new Separator());

        var quit = new MenuItem { Header = "Quit Lintel" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        _ctx.Items.Add(quit);

        _ctx.Opened += (_, _) => { _forceOpen = true; UpdateContextMenuChecks(); };
        _ctx.Closed += (_, _) => _forceOpen = false;
    }

    private void UpdateContextMenuChecks()
    {
        if (_ctxCustomize != null) _ctxCustomize.IsChecked = Customizing;
    }

    private void OnBarRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_ctx == null) return;
        _ctx.PlacementTarget = this;
        _ctx.IsOpen = true;
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

    private void OpenSettings()
    {
        _forceOpen = true;
        var win = new SettingsWindow(_settings);
        win.SettingsApplied += () =>
        {
            ApplySettings();
            ApplyMode();
            StartupManager.Apply(_settings.LaunchAtStartup);
        };
        win.Closed += (_, _) => _forceOpen = false;
        win.Show();
        win.Activate();
    }

    public void OpenSettingsFromTray() => OpenSettings();
    public void ToggleCustomizeFromTray() => ToggleCustomize();

    // =========================================================== helpers

    private static SolidColorBrush BrushFrom(string hex, Color fallback)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch { var b = new SolidColorBrush(fallback); b.Freeze(); return b; }
    }
}
