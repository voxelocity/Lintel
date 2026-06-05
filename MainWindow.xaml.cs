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
            PlusPopup.HorizontalOffset = (BarGrid.ActualWidth / 2.0) - 90; // centre the pill under the bar
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

    private void ShowScrim()
    {
        Scrim.Width = SystemParameters.VirtualScreenWidth;
        Scrim.Height = SystemParameters.VirtualScreenHeight;
        ScrimPopup.Placement = PlacementMode.Absolute;
        ScrimPopup.HorizontalOffset = SystemParameters.VirtualScreenLeft;
        ScrimPopup.VerticalOffset = SystemParameters.VirtualScreenTop;
        ScrimPopup.IsOpen = true;
    }

    private void OpenOverlay(UIElement content, UIElement target, double horizontalOffset)
    {
        OverlayHost.Content = content;
        OverlayPopup.PlacementTarget = target;
        OverlayPopup.Placement = PlacementMode.Bottom;
        OverlayPopup.HorizontalOffset = horizontalOffset;
        ShowScrim();
        OverlayPopup.IsOpen = true;
        _forceOpen = true;
        GrowFromTop(OverlayScale, OverlayHost);
    }

    private void OpenOverlayCentered(FrameworkElement content, double width)
    {
        double off = (BarRoot.ActualWidth / 2.0) - (width / 2.0);
        OpenOverlay(content, BarRoot, off);
    }

    private void CloseOverlay()
    {
        OverlayPopup.IsOpen = false;
        ScrimPopup.IsOpen = false;
        OverlayHost.Content = null;
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
            new("Settings…", OpenSettings),
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
        var panel = new SettingsPanel(_settings);
        panel.SettingsApplied += () =>
        {
            ApplySettings();
            ApplyMode();
            StartupManager.Apply(_settings.LaunchAtStartup);
        };
        panel.CloseRequested += CloseOverlay;
        OpenOverlayCentered(panel, 452);
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
