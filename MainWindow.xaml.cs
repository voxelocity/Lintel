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
    private double _dragGrabX;        // cursor offset within the grabbed widget
    private Size _dragSize;           // grabbed widget size
    private System.Windows.Shapes.Rectangle? _dropIndicator; // predictive landing outline

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

    private bool _backdrop;
    private Color _backdropTint;

    public void ApplySettings()
    {
        var theme = Themes.For(_settings.Theme, _settings.WidgetCornerRadius);
        _backdrop = theme.Acrylic;
        _backdropTint = theme.AcrylicTint;

        // With acrylic the bar background is near-transparent (still hit-testable) so the
        // blur shows; otherwise it's the user's solid translucent colour.
        BarBackground = _backdrop
            ? new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0))
            : BrushFrom(_settings.BackgroundColor, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E));
        Foreground = BrushFrom(_settings.ForegroundColor, Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF7));

        BottomLine.Visibility = theme.BottomHighlight ? Visibility.Visible : Visibility.Collapsed;

        RebuildWidgets();
        ApplyLayout();
        ApplyBackdrop(_shown && _backdrop);
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

        double spacing = Themes.For(_settings.Theme, _settings.WidgetCornerRadius).Spacing;
        PanelLeft.Spacing = PanelCenter.Spacing = PanelRight.Spacing = spacing;

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

    // =========================================================== graph hover

    public void ShowGraph(WidgetView view, Metric metric)
    {
        _graphHideTimer.Stop();
        if (ReferenceEquals(_graphOwner, view) && GraphPopup.IsOpen) return;

        _graphOwner = view;
        _graphMetric = metric;
        GraphTitle.Text = metric.Name.ToUpperInvariant();
        GraphView.Kind = Widgets.MetricStyle.For(metric.Key).Graph;
        GraphPopup.PlacementTarget = view;
        UpdateGraph(metric);
        LoadTopProcesses(metric.Key);

        GraphPopup.IsOpen = true;
        GrowFromTop(GraphScale, GraphCard);
    }

    private void LoadTopProcesses(string key)
    {
        GraphProcTitle.Text = key == "ram" ? "TOP MEMORY USERS"
            : key == "battery" ? "TOP POWER USERS"
            : key == "net" ? "TOP I/O USERS"
            : $"TOP {WidgetCatalog.Find(key)?.Name.ToUpperInvariant()} USERS";
        GraphProcs.Children.Clear();
        GraphProcs.Children.Add(new TextBlock { Text = "…", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 11.5 });

        var owner = _graphOwner;
        _ = ProcessUsage.TopAsync(key, 4).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!GraphPopup.IsOpen || !ReferenceEquals(owner, _graphOwner)) return;
                GraphProcs.Children.Clear();
                var list = t.IsCompletedSuccessfully ? t.Result : new List<ProcUsage>();
                if (list.Count == 0)
                {
                    GraphProcs.Children.Add(new TextBlock { Text = "No data", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 11.5 });
                    return;
                }
                foreach (var p in list)
                {
                    var dp = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 1.5, 0, 1.5) };
                    var name = new TextBlock { Text = p.Name, Foreground = Brushes.White, FontSize = 12 };
                    var val = new TextBlock { Text = p.Value, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB5)), FontSize = 12, FontWeight = FontWeights.SemiBold };
                    DockPanel.SetDock(name, Dock.Left);
                    DockPanel.SetDock(val, Dock.Right);
                    dp.Children.Add(val);
                    dp.Children.Add(name);
                    GraphProcs.Children.Add(dp);
                }
            }));
        });
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

    private void OpenOverlay(UIElement content, UIElement target, double horizontalOffset, bool focusable = false)
    {
        OverlayHost.Content = content;
        OverlayPopup.PlacementTarget = target;
        OverlayPopup.Placement = PlacementMode.Bottom;
        OverlayPopup.HorizontalOffset = horizontalOffset;
        ShowScrim();
        OverlayPopup.IsOpen = true;
        _forceOpen = true;

        // Text-editing overlays (settings, note) need the window to accept keyboard focus.
        _overlayFocusable = focusable;
        if (focusable && _hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE & ~WS_EX_TRANSPARENT);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetForegroundWindow(_hwnd);
                Activate();
            }), DispatcherPriority.Input);
        }

        GrowFromTop(OverlayScale, OverlayHost);
    }

    private void OpenOverlayCentered(FrameworkElement content, double width, bool focusable = false)
    {
        double off = (BarRoot.ActualWidth / 2.0) - (width / 2.0);
        OpenOverlay(content, BarRoot, off, focusable);
    }

    private void RecenterOverlay(double width) =>
        OverlayPopup.HorizontalOffset = (BarRoot.ActualWidth / 2.0) - (width / 2.0);

    private Action? _overlayClosed;

    private void CloseOverlay()
    {
        OverlayPopup.IsOpen = false;
        ScrimPopup.IsOpen = false;
        OverlayHost.Content = null;
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

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1F, 0x1F, 0x23)),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(6),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black },
            Child = stack
        };
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
        foreach (LintelTheme t in Enum.GetValues<LintelTheme>())
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

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1F, 0x1F, 0x23)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 16, 18, 16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black },
            Child = stack
        };
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

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1F, 0x1F, 0x23)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black },
            Child = panel
        };

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

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1F, 0x1F, 0x23)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black },
            Child = panel
        };

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
        OpenOverlayCentered(panel, panel.CurrentWidth, focusable: true);
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
