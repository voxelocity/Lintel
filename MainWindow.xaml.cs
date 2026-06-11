using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    public double BarHeight => _effectiveBarHeight;   // effective height (theme override or setting)
    public bool Customizing { get; private set; }
    public string ActiveAppName => _activeAppName;
    public Metric GetMetric(string key) => _perf!.Get(key);
    public MediaService Media => _media!;

    public void OnModeClicked() => CycleMode();
    public void OnSettingsClicked() => OpenSettings();

    public bool OpenOnHover => _settings.OpenOnHover;

    public bool HasDropdown(WidgetView view) => view.Descriptor.Kind
        is WidgetKind.Gauge or WidgetKind.Load or WidgetKind.Media or WidgetKind.Claude or WidgetKind.GitHub
        or WidgetKind.Volume or WidgetKind.Brightness or WidgetKind.Weather or WidgetKind.Stocks
        or WidgetKind.Todo or WidgetKind.Pomodoro or WidgetKind.TicTacToe;

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
            case WidgetKind.Volume: ShowVolumePanel(view); break;
            case WidgetKind.Brightness: ShowBrightnessPanel(view); break;
            case WidgetKind.Weather: ShowWeatherPanel(view); break;
            case WidgetKind.Stocks: ShowStocksPanel(view); break;
            case WidgetKind.Todo: ShowTodoPanel(view); break;
            case WidgetKind.Pomodoro: ShowPomodoroPanel(view); break;
            case WidgetKind.TicTacToe: ShowTicTacToePanel(view); break;
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

        LoadCustomization();
        ApplySettings();
        ApplyMode(initial: true);

        // Keep the frosted-glass backdrop in sync when the wallpaper / theme colours change.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _clock.Start();
        _tick.Start();
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop or Microsoft.Win32.UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(new Action(() => ApplyFrost(Themes.Resolve(_settings))));
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _blurTimer?.Stop();
        _tick.Stop();
        _clock.Stop();
        _perf?.Dispose();
        _audio?.Dispose();
        _appBar?.Release();
        base.OnClosed(e);
    }

    // =========================================================== settings/layout

    private bool _backdrop;
    private bool _frosted;                     // theme uses the live-blur frosted glass
    private bool _potato;                      // low-end mode: no blur, reduced animation
    private bool _lite;                         // lite mode: bar blur kept, dropdowns half-opacity (no blur)
    private bool _dropHalf;                     // dropdowns render at half opacity (Lite)
    private DropdownChrome _chrome;            // OS-window styling for dropdowns
    private bool _dropShine;                    // glassy bevel highlight on dropdowns
    private bool _barBottom;                     // bar docked to the bottom edge
    private RECT _workArea;                       // monitor work area (excludes the OS taskbar)
    private int _barTopPx;                        // bar's top edge in device pixels
    private double HiddenOffset => _barBottom ? _effectiveBarHeight : -_effectiveBarHeight;
    private bool _backdropAero;                // classic Aero blur vs frosted acrylic
    private bool _useOsBlur = false;           // OS blur is unreliable on Win11 → use translucency
    private Color _backdropTint;
    private double _effectiveBarHeight = 32;   // theme height override, or the user's setting

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
        var theme = Themes.Resolve(_settings);
        _potato = _settings.PotatoMode;
        _lite = _settings.LiteMode;
        _barBottom = _settings.BarPosition == BarEdge.Bottom;
        _chrome = theme.Chrome;
        _dropShine = theme.DropShine;
        _effectiveBarHeight = theme.BarHeight ?? _settings.BarHeight;
        bool glass = theme.FrostedGlass && !theme.SeparatedZones && _settings.LiveBlur;
        _frosted = glass && !_potato && !_lite;     // live-blur dropdowns (off in Lite/Potato)
        _dropHalf = glass && _lite && !_potato;     // Lite: dropdowns are half-opacity instead of blurred
        Controls.AnimatedBarPanel.AnimationsEnabled = !_potato;
        _backdrop = theme.Acrylic;
        _backdropAero = theme.AeroBlur;
        _backdropTint = theme.AcrylicTint;
        _fluid = theme.FluidDropdowns;
        _shoulder = _fluid;   // all fluid themes get the connected shoulder shape

        // Dropdowns share the bar's material. Acrylic themes use a translucent tint so the
        // dropdown reads like the bar (vector-drawn, so corners stay clean).
        _dropMaterial = theme.DropdownColor is Color dropCol ? dropCol
            : theme.SeparatedZones ? theme.ZoneBackground
            : theme.Acrylic ? Color.FromArgb(0xDC, theme.AcrylicTint.R, theme.AcrylicTint.G, theme.AcrylicTint.B)
            : ParseColor(_settings.BackgroundColor, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E), 0xF6);
        _dropOutline = theme.BottomHighlight ? Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)
            : theme.SeparatedZones ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)
            : (Color?)null;

        // Islands: transparent bar with floating zone pills (gaps show desktop).
        // Acrylic/glass: a genuinely translucent tint so the desktop shows through. (We don't rely
        // on the OS blur — SetWindowCompositionAttribute renders as a flat opaque tint for a
        // non-activating tool window on Windows 11, so per-pixel translucency is the reliable path.)
        // Gradient (e.g. Windows XP): a solid vertical gradient. Else: solid colour.
        Brush barBg;
        if (theme.SeparatedZones)
            barBg = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        else if (_backdrop)
            barBg = new SolidColorBrush(_backdropTint);             // translucent glass
        else if (theme is { BarTop: Color top, BarBottom: Color bottom })
            barBg = VerticalGradient(top, bottom);
        else
            barBg = BrushFrom(_settings.BackgroundColor, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E));
        if (barBg is SolidColorBrush scb) scb.Freeze();
        BarBackground = barBg;
        Foreground = BrushFrom(_settings.ForegroundColor, Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF7));

        // Glossy reflection across the top half (Aero / Luna).
        if (theme.GlossStrength > 0 && !_potato)
        {
            GlossOverlay.Fill = GlossBrush(theme.GlossStrength);
            GlossOverlay.Visibility = Visibility.Visible;
        }
        else GlossOverlay.Visibility = Visibility.Collapsed;

        // Bright top edge.
        if (theme.TopEdge is Color te)
        {
            TopEdgeLine.Background = new SolidColorBrush(te);
            TopEdgeLine.Visibility = Visibility.Visible;
        }
        else TopEdgeLine.Visibility = Visibility.Collapsed;

        // Bottom edge: explicit theme colour, else the Power-style highlight.
        Color? bottomEdge = theme.BottomEdge ?? (theme.BottomHighlight ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : (Color?)null);
        if (bottomEdge is Color be)
        {
            BottomLine.Background = new SolidColorBrush(be);
            BottomLine.Visibility = Visibility.Visible;
        }
        else BottomLine.Visibility = Visibility.Collapsed;

        // Theme font (e.g. Tahoma for XP).
        FontFamily = new System.Windows.Media.FontFamily(theme.FontFamily ?? "Segoe UI");

        RebuildWidgets();
        ApplyZoneStyle(theme);
        ApplyLayout();
        ApplyBackdrop(_shown && _backdrop);
        ApplyFrost(theme);
        ApplyBarPlacement();
    }

    /// <summary>Open dropdowns upward (off the bottom edge) when the bar is docked to the bottom.</summary>
    private void ApplyBarPlacement()
    {
        var place = _barBottom ? PlacementMode.Top : PlacementMode.Bottom;
        double s = _barBottom ? -1 : 1;
        GraphPopup.Placement = place; GraphPopup.VerticalOffset = 6 * s;
        PlusPopup.Placement = place; PlusPopup.VerticalOffset = 8 * s;
        AddPopup.Placement = place; AddPopup.VerticalOffset = 6 * s;
        OverlayPopup.Placement = place;
    }

    /// <summary>
    /// Frosted glass. Preferred: a real-time DWM acrylic backdrop window behind the bar (Win11).
    /// Fallback: a blurred snapshot of the desktop wallpaper.
    /// </summary>
    private void ApplyFrost(ThemeDef theme)
    {
        bool wantGlass = theme.FrostedGlass && !theme.SeparatedZones;

        // Preferred: custom real-time blur — capture the live content behind the bar (the bar excludes
        // itself from capture) and blur it. Tracks live windows; the blur amount is ours to control.
        if (wantGlass && _settings.LiveBlur && !_potato)
        {
            StartLiveBlur(true);
            FrostImage.Effect = LiveBlurEffect();
            var t = _backdropTint;
            byte a = Math.Min(t.A, (byte)0xD0);   // honour the theme tint (cap just shy of opaque)
            FrostTint.Background = new SolidColorBrush(Color.FromArgb(a, t.R, t.G, t.B));
            FrostTint.Visibility = Visibility.Visible;
            BarBackground = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            return;
        }
        // Live blur off → fall back to plain translucency (BarBackground was set to the translucent tint).
        StartLiveBlur(false);
        FrostImage.Visibility = Visibility.Collapsed;
        FrostImage.Source = null;
        FrostTint.Visibility = Visibility.Collapsed;
    }

    private static System.Windows.Media.Effects.BlurEffect LiveBlurEffect() => new()
    {
        Radius = 16,
        KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
        RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
    };

    // ---- custom real-time blur ----

    private DispatcherTimer? _blurTimer;

    private void StartLiveBlur(bool on)
    {
        if (on)
        {
            if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
            FrostImage.Visibility = Visibility.Visible;
            CaptureBlurFrame();
            if (_blurTimer == null)
            {
                _blurTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(45) };
                _blurTimer.Tick += (_, _) => CaptureBlurFrame();
            }
            _blurTimer.Start();
        }
        else
        {
            _blurTimer?.Stop();
            if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WDA_NONE);
        }
    }

    private void CaptureBlurFrame()
    {
        if (!_shown || _hwnd == IntPtr.Zero) return;
        var src = ScreenCapture.Capture(_monitorBounds.Left, _monitorBounds.Top, _monitorBounds.Width, _barHeightPx);
        if (src != null) FrostImage.Source = src;
    }

    private void ApplyZoneStyle(ThemeDef theme)
    {
        var zones = new[] { (ZoneLeft, PanelLeft), (ZoneCenter, PanelCenter), (ZoneRight, PanelRight) };
        if (theme.SeparatedZones)
        {
            var z = theme.ZoneBackground;
            Color Lift(int d) => Color.FromArgb(z.A, (byte)Math.Clamp(z.R + d, 0, 255), (byte)Math.Clamp(z.G + d, 0, 255), (byte)Math.Clamp(z.B + d, 0, 255));
            var grad = VGrad((Lift(0x14), 0), (z, 0.5), (Lift(-0x08), 1));   // glassy depth
            var border = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)); border.Freeze();
            foreach (var (zone, panel) in zones)
            {
                zone.Background = grad;
                zone.BorderBrush = border;
                zone.BorderThickness = new Thickness(1);
                zone.CornerRadius = new CornerRadius(13);
                zone.Padding = new Thickness(14, 0, 14, 0);
                zone.Margin = new Thickness(5, 4, 5, 4);
                zone.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black };
                zone.Visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        else
        {
            foreach (var (zone, panel) in zones)
            {
                zone.Background = Brushes.Transparent;
                zone.BorderThickness = new Thickness(0);
                zone.CornerRadius = new CornerRadius(0);
                zone.Padding = new Thickness(0);
                zone.Margin = new Thickness(0);
                zone.Effect = null;
                zone.Visibility = Visibility.Visible;
            }

            // XP: a green "Start area" island layered over the left zone.
            if (theme.LeftIslandTop is Color gtop)
            {
                var gbot = theme.LeftIslandBottom ?? gtop;
                var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                grad.GradientStops.Add(new GradientStop(gtop, 0));
                grad.GradientStops.Add(new GradientStop(gbot, 1));
                grad.Freeze();
                ZoneLeft.Background = grad;
                ZoneLeft.CornerRadius = new CornerRadius(0, 9, 9, 0);   // rounded right edge, like the Start bump
                ZoneLeft.Padding = new Thickness(12, 0, 16, 0);
                ZoneLeft.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black };
            }
        }
    }

    private void ApplyBackdrop(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        // NOTE: On Windows 11 the undocumented SetWindowCompositionAttribute blur renders as a flat
        // opaque tint for a non-activating tool window (no real blur), which actually *defeats* the
        // glass look. We instead use a translucent BarBackground for see-through glass, and keep this
        // policy disabled. Flip _useOsBlur to opt back in on systems where the blur works.
        uint abgr = (uint)((_backdropTint.A << 24) | (_backdropTint.B << 16) | (_backdropTint.G << 8) | _backdropTint.R);
        var accent = new ACCENT_POLICY
        {
            AccentState = (enabled && _useOsBlur)
                ? (_backdropAero ? ACCENT_ENABLE_BLURBEHIND : ACCENT_ENABLE_ACRYLICBLURBEHIND)
                : ACCENT_DISABLED,
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
        var mon = Monitors.Pick(_settings.MonitorIndex);
        _monitorBounds = mon.Bounds;
        _workArea = mon.WorkArea;

        uint dpi = GetDpiForWindow(_hwnd);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        _scaleX = _scaleY = scale;
        _barHeightPx = (int)Math.Round(_effectiveBarHeight * scale);
        // Bottom: sit just above the OS taskbar (work area). Top: the very top edge.
        _barTopPx = _barBottom ? _workArea.Bottom - _barHeightPx : _monitorBounds.Top;

        Left = _monitorBounds.Left / scale;
        Top = _barTopPx / scale;
        Width = _monitorBounds.Width / scale;
        Height = _effectiveBarHeight;

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (!_shown) SlideTransform.Y = HiddenOffset;
    }

    public void ApplyMode(bool initial = false)
    {
        _hotSince = null;
        _hideAt = null;

        if (_settings.Mode == VisibilityMode.AlwaysOn)
        {
            _appBar?.Reserve(_monitorBounds, _barHeightPx, _barBottom);
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

        var t = Themes.Resolve(_settings);
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
        RefreshInfoWidgets();
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
            Top = _barTopPx,
            Right = _monitorBounds.Right,
            Bottom = _barTopPx + _barHeightPx
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
        bool atTopEdge = xInRange && (_barBottom ? p.Y >= _barTopPx + _barHeightPx - _settings.TriggerZonePx : p.Y <= _monitorBounds.Top + _settings.TriggerZonePx);
        bool overBar = xInRange && p.Y >= _barTopPx && p.Y <= _barTopPx + _barHeightPx;

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

    private void ReassertTopmost()
    {
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // =========================================================== show/hide

    private void SetShown(bool show, bool animate)
    {
        _shown = show;
        SetClickThrough(!show);
        if (_backdrop) ApplyBackdrop(show);
        // Pause the capture loop while the bar is hidden; refresh immediately when it returns.
        if (_blurTimer != null) { if (show) { _blurTimer.Start(); CaptureBlurFrame(); } else _blurTimer.Stop(); }

        double target = show ? 0 : HiddenOffset;
        int ms = (animate && !_potato) ? _settings.AnimationMs : 0;

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

    private void UploadWidget_Click(object sender, RoutedEventArgs e) { AddPopup.IsOpen = false; ImportWidgetFromFile(); }
    private void UploadTheme_Click(object sender, RoutedEventArgs e) { AddPopup.IsOpen = false; ImportThemeFromFile(); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenCustomizationFolder();

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
        card.RenderTransformOrigin = new Point(0.5, _barBottom ? 1 : 0);
        if (_potato)
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(60)));
            return;
        }
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
        OverlayPopup.Placement = _barBottom ? PlacementMode.Top : PlacementMode.Bottom;
        OverlayPopup.HorizontalOffset = horizontalOffset;
        OverlayPopup.VerticalOffset = (_fluid ? -2 : 4) * (_barBottom ? -1 : 1);   // overlap the bar slightly so it's seamless

        if (!_overlayHover) ShowScrim();   // hover dropdowns are non-modal (no click-catcher)
        OverlayPopup.IsOpen = true;
        _forceOpen = true;
        BeginDropdownFrost(content as FrameworkElement);   // live-blur backdrop for frosted themes

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

        // OS-window chrome (XP Luna / Vista Aero) — a fake title bar + buttons styled like that OS.
        if (_chrome == DropdownChrome.Luna) return BuildLunaCard(content, padding, shadow);
        if (_chrome == DropdownChrome.Aero) return BuildAeroCard(content, padding, shadow);

        // Glass themes: a live-blurred backdrop (or, in Lite mode, a half-opacity tint) behind the
        // content + a glassy bevel, clipped to the rounded corners.
        if ((_frosted || _dropHalf) && !_shoulder)
        {
            const double R = 14;
            var grid = new Grid();
            if (_frosted)
            {
                var frost = new Border { CornerRadius = new CornerRadius(R) };   // blurred capture (set later)
                var tint = new Border { CornerRadius = new CornerRadius(R), Background = new SolidColorBrush(_backdropTint) };  // same tint as the bar → clear glass
                grid.Children.Add(frost);
                grid.Children.Add(tint);
                _pendingFrost = frost;
            }
            else   // Lite: half-opacity theme material, no blur
            {
                var dm = _dropMaterial;
                grid.Children.Add(new Border { CornerRadius = new CornerRadius(R), Background = new SolidColorBrush(Color.FromArgb(0x80, dm.R, dm.G, dm.B)) });
            }
            grid.Children.Add(new Border { Padding = padding, Child = content });
            if (_dropShine) grid.Children.Add(ShineOverlay(new CornerRadius(R)));   // glassy bevel (glass OS themes only)
            return new Border
            {
                CornerRadius = new CornerRadius(R),
                ClipToBounds = true,
                Child = grid,
                Effect = shadow
            };
        }

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

    // ---- OS-window chrome for dropdowns ----

    private static Brush VGrad(params (Color c, double at)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop(c, at));
        b.Freeze(); return b;
    }

    /// <summary>A glassy bevel ring — bright at the top, fading down — drawn over a dropdown's edge.</summary>
    private static Border ShineOverlay(CornerRadius cr) => new()
    {
        CornerRadius = cr,
        IsHitTestVisible = false,
        BorderThickness = new Thickness(1.3),
        BorderBrush = VGrad(
            (Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF), 0),
            (Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0.5),
            (Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 1))
    };

    private static Border WinButton(Color top, Color bottom, UIElement glyph) => new()
    {
        Width = 21, Height = 17, Margin = new Thickness(2, 0, 0, 0),
        CornerRadius = new CornerRadius(3),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.8),
        Background = VGrad((top, 0), (bottom, 1)),
        Child = glyph
    };

    private static UIElement WinButtons(bool aero)
    {
        Color bt = aero ? Color.FromArgb(0x70, 0x60, 0x70, 0x90) : Color.FromRgb(0x4A, 0x8C, 0xF0);
        Color bb = aero ? Color.FromArgb(0x70, 0x20, 0x28, 0x3A) : Color.FromRgb(0x1C, 0x55, 0xCE);
        var min = new System.Windows.Shapes.Rectangle { Width = 7, Height = 2, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
        var max = new Border { Width = 8, Height = 7, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4, 2, 1.4, 1.4) };
        var close = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L7,7 M7,0 L0,7"), Stroke = Brushes.White, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        row.Children.Add(WinButton(bt, bb, min));
        row.Children.Add(WinButton(bt, bb, max));
        row.Children.Add(WinButton(Color.FromRgb(0xE8, 0x6A, 0x52), Color.FromRgb(0xC0, 0x30, 0x20), close));   // red close
        return row;
    }

    private UIElement TitleBarContent(bool aero)
    {
        var dock = new DockPanel { LastChildFill = false };
        var buttons = WinButtons(aero);
        DockPanel.SetDock((UIElement)buttons, Dock.Right);
        dock.Children.Add(buttons);
        var title = new TextBlock { Text = "Lintel", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        if (aero) title.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.8, Color = Colors.Black };
        DockPanel.SetDock(title, Dock.Left);
        dock.Children.Add(title);
        return dock;
    }

    // Windows XP Luna window: blue title bar + buttons, blue frame, solid body.
    private FrameworkElement BuildLunaCard(UIElement content, Thickness padding, System.Windows.Media.Effects.Effect shadow)
    {
        var titleBar = new Grid { Height = 27 };
        titleBar.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Background = VGrad((Color.FromRgb(0x4B, 0x8E, 0xF7), 0), (Color.FromRgb(0x2C, 0x6B, 0xE8), 0.45), (Color.FromRgb(0x12, 0x4C, 0xD2), 0.55), (Color.FromRgb(0x2A, 0x63, 0xE0), 1))
        });
        titleBar.Children.Add(new Border   // glossy top highlight
        {
            Height = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 1, 4, 0),
            CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF))
        });
        titleBar.Children.Add(TitleBarContent(aero: false));
        var body = new Border { Background = new SolidColorBrush(_dropMaterial), Padding = padding, Child = content };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0); Grid.SetRow(body, 1);
        grid.Children.Add(titleBar); grid.Children.Add(body);

        return new Border
        {
            CornerRadius = new CornerRadius(8, 8, 3, 3),
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x4C, 0xCE)),   // XP blue frame
            Padding = new Thickness(3, 0, 3, 3),
            ClipToBounds = true,
            Child = grid,
            Effect = shadow
        };
    }

    // Windows Vista Aero window: glassy translucent title bar (over the live blur) + frosted body.
    private FrameworkElement BuildAeroCard(UIElement content, Thickness padding, System.Windows.Media.Effects.Effect shadow)
    {
        const double R = 9;
        var d = _dropMaterial;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Base: live-blur frost behind the whole window (if enabled), else solid dark glass.
        if (_frosted)
        {
            var frost = new Border { CornerRadius = new CornerRadius(R) };
            Grid.SetRowSpan(frost, 2);
            grid.Children.Add(frost);
            _pendingFrost = frost;
        }
        else
        {
            // Lite → half-opacity body; otherwise solid.
            byte a = _dropHalf ? (byte)0x80 : _dropMaterial.A;
            var solid = new Border { CornerRadius = new CornerRadius(R), Background = new SolidColorBrush(Color.FromArgb(a, d.R, d.G, d.B)) };
            Grid.SetRowSpan(solid, 2);
            grid.Children.Add(solid);
        }

        // Title bar: translucent glass gradient + a glossy top highlight.
        var titleGlass = new Grid { Height = 30 };
        titleGlass.Children.Add(new Border { CornerRadius = new CornerRadius(R, R, 0, 0), Background = VGrad((Color.FromArgb(0x9A, 0x28, 0x30, 0x42), 0), (Color.FromArgb(0x70, 0x10, 0x16, 0x22), 1)) });
        titleGlass.Children.Add(new Border { CornerRadius = new CornerRadius(R, R, 0, 0), Background = VGrad((Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.5), (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.52)) });
        titleGlass.Children.Add(TitleBarContent(aero: true));
        Grid.SetRow(titleGlass, 0);
        grid.Children.Add(titleGlass);

        // Body: bar tint when frosted (clear glass); otherwise the base layer already provides the colour.
        var bodyBrush = _frosted ? (Brush)new SolidColorBrush(_backdropTint) : Brushes.Transparent;
        var bodyTint = new Border { Background = bodyBrush, Padding = padding, Child = content };
        Grid.SetRow(bodyTint, 1);
        grid.Children.Add(bodyTint);

        var shine = ShineOverlay(new CornerRadius(R));
        Grid.SetRowSpan(shine, 2);
        grid.Children.Add(shine);

        return new Border
        {
            CornerRadius = new CornerRadius(R),
            ClipToBounds = true,
            Child = grid,
            Effect = shadow
        };
    }

    // ---- frosted dropdown backdrop (live blur of whatever is behind the popup) ----

    private Border? _pendingFrost;     // frost layer of the card just built by Card()
    private Border? _dropFrostBorder;
    private FrameworkElement? _dropFrostCard;
    private DispatcherTimer? _dropFrostTimer;
    private IntPtr _dropFrostHwnd;

    private void BeginDropdownFrost(FrameworkElement? card)
    {
        var pending = _pendingFrost;
        StopDropdownFrost();
        if (pending == null || card == null) return;
        _dropFrostBorder = pending;
        _dropFrostCard = card;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (PresentationSource.FromVisual(card) is System.Windows.Interop.HwndSource src && src.Handle != IntPtr.Zero)
            {
                _dropFrostHwnd = src.Handle;
                SetWindowDisplayAffinity(_dropFrostHwnd, WDA_EXCLUDEFROMCAPTURE);   // don't capture ourselves
            }
            UpdateDropdownFrost();
        }), DispatcherPriority.Loaded);

        _dropFrostTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(70) };
        _dropFrostTimer.Tick += (_, _) => UpdateDropdownFrost();
        _dropFrostTimer.Start();
    }

    private void UpdateDropdownFrost()
    {
        if (_dropFrostBorder == null || _dropFrostCard is not { ActualWidth: > 1 }) return;
        try
        {
            var tl = _dropFrostCard.PointToScreen(new Point(0, 0));   // device pixels
            int w = (int)Math.Round(_dropFrostCard.ActualWidth * _scaleX);
            int h = (int)Math.Round(_dropFrostCard.ActualHeight * _scaleY);
            var cap = ScreenCapture.Capture((int)Math.Round(tl.X), (int)Math.Round(tl.Y), w, h);
            if (cap == null) return;
            _dropFrostBorder.Background = new ImageBrush(BlurBitmap(cap, 22)) { Stretch = Stretch.Fill };
        }
        catch { }
    }

    private void StopDropdownFrost()
    {
        _dropFrostTimer?.Stop();
        _dropFrostTimer = null;
        _dropFrostBorder = null;
        _dropFrostCard = null;
        _pendingFrost = null;
        if (_dropFrostHwnd != IntPtr.Zero) { SetWindowDisplayAffinity(_dropFrostHwnd, WDA_NONE); _dropFrostHwnd = IntPtr.Zero; }
    }

    private static BitmapSource BlurBitmap(BitmapSource src, double radius)
    {
        var img = new Image
        {
            Source = src,
            Width = src.PixelWidth,
            Height = src.PixelHeight,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = radius, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance }
        };
        var sz = new Size(src.PixelWidth, src.PixelHeight);
        img.Measure(sz); img.Arrange(new Rect(sz));
        var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(img); rtb.Freeze();
        return rtb;
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
        StopDropdownFrost();
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
        string current = Themes.NameOf(_settings);
        var rows = new List<MenuRow>();
        foreach (var name in Themes.Names())
        {
            var captured = name;
            rows.Add(new MenuRow(Themes.DisplayName(name), () => ChangeTheme(captured), Checked: name == current));
        }
        rows.Add(Sep());
        rows.Add(new MenuRow("Import theme…", ImportThemeFromFile));
        double off = target.TranslatePoint(new Point(0, 0), BarRoot).X - 20;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - 200));
        OpenOverlay(BuildMenuCard(rows, 200), BarRoot, off);
    }

    public void ChangeTheme(string name)
    {
        _settings.ThemeName = name;
        if (Enum.TryParse<LintelTheme>(name, out var en)) _settings.Theme = en;   // keep enum in sync for built-ins
        _settings.Save();
        ApplySettings();
    }

    public void ChangeTheme(LintelTheme theme) => ChangeTheme(theme.ToString());

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
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var coverBorder = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Top, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black } };
        var coverImg = new Image { Stretch = Stretch.UniformToFill };
        var coverPh = new System.Windows.Shapes.Path { Data = Widgets.Icons.Get("media"), Fill = new SolidColorBrush(accent), Stretch = Stretch.Uniform, Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var coverInner = new Grid(); coverInner.Children.Add(coverPh); coverInner.Children.Add(coverImg);
        coverBorder.Child = coverInner;
        Grid.SetColumn(coverBorder, 0);
        header.Children.Add(coverBorder);

        var titleStack = new StackPanel { Margin = new Thickness(12, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = W - 90 };
        var artistTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA6)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = W - 90, Margin = new Thickness(0, 2, 0, 0) };
        var albumTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x86)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = W - 90, Margin = new Thickness(0, 2, 0, 0) };
        titleStack.Children.Add(titleTb);
        titleStack.Children.Add(artistTb);
        titleStack.Children.Add(albumTb);
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

        // lyrics (fetched from lrclib)
        panel.Children.Add(new Border { Height = 1, Background = HairLine(), Margin = new Thickness(0, 12, 0, 8) });
        var lyricsTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xC4, 0xCA)), FontSize = 12.5, LineHeight = 18, TextWrapping = TextWrapping.Wrap };
        var lyricsScroll = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = lyricsTb };
        panel.Children.Add(lyricsScroll);

        var card = Card(panel, new Thickness(16, 14, 16, 12));
        string lyricKey = "";

        void Update()
        {
            var m = _media!.Current;
            titleTb.Text = m.HasMedia ? m.Title : "Nothing playing";
            artistTb.Text = m.Artist;
            albumTb.Text = m.Album;
            albumTb.Visibility = string.IsNullOrEmpty(m.Album) ? Visibility.Collapsed : Visibility.Visible;

            string key = m.Artist + "|" + m.Title;
            if (m.HasMedia && key != lyricKey)
            {
                lyricKey = key;
                lyricsTb.Text = "Loading lyrics…";
                Lyrics.GetAsync(m.Artist, m.Title).ContinueWith(t =>
                {
                    if (!t.IsCompletedSuccessfully) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (lyricKey != key) return;   // track changed meanwhile
                        lyricsTb.Text = string.IsNullOrWhiteSpace(t.Result) ? "No lyrics found." : t.Result;
                    }));
                });
            }
            else if (!m.HasMedia) { lyricKey = ""; lyricsTb.Text = ""; }

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

        // your open PRs + recent repos
        if (GitHubService.GhAvailable)
        {
            panel.Children.Add(new Border { Height = 1, Background = HairLine(), Margin = new Thickness(0, 12, 0, 10) });
            panel.Children.Add(new TextBlock { Text = "YOUR OPEN PRS", Foreground = Sub(), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) });
            var prs = new StackPanel(); var prStatus = new TextBlock { Text = "loading…", Foreground = Sub(), FontSize = 11 };
            prs.Children.Add(prStatus); panel.Children.Add(prs);

            panel.Children.Add(new TextBlock { Text = "RECENT REPOS", Foreground = Sub(), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 0, 6) });
            var repos = new StackPanel(); var repoStatus = new TextBlock { Text = "loading…", Foreground = Sub(), FontSize = 11 };
            repos.Children.Add(repoStatus); panel.Children.Add(repos);

            GitHubService.MyPullRequestsAsync().ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.BeginInvoke(new Action(() => FillGhList(prs, t.Result, "No open PRs.")));
            });
            GitHubService.RecentReposAsync().ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.BeginInvoke(new Action(() => FillGhList(repos, t.Result, "No repos found.")));
            });
        }

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

    private void FillGhList(StackPanel container, List<GitHubService.GhItem> items, string emptyMsg)
    {
        container.Children.Clear();
        if (items.Count == 0) { container.Children.Add(new TextBlock { Text = emptyMsg, Foreground = Sub(), FontSize = 11 }); return; }
        foreach (var it in items)
        {
            var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = it.Title, Foreground = Brushes.White, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(it.Subtitle)) sp.Children.Add(new TextBlock { Text = it.Subtitle, Foreground = Sub(), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Child = sp;
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var url = it.Url;
            row.MouseLeftButtonDown += (_, e) => { e.Handled = true; OpenUrl(url); CloseOverlay(); };
            container.Children.Add(row);
        }
    }

    private void OpenDevOverlay(FrameworkElement card, WidgetView view, double width)
    {
        double off = view.TranslatePoint(new Point(0, 0), BarRoot).X - 20;
        off = Math.Clamp(off, 8, Math.Max(8, BarRoot.ActualWidth - width - 28));
        // dev panels have inputs/buttons → make the overlay focusable so clicks register
        OpenOverlay(card, BarRoot, off, focusable: true);
    }

    // =========================================================== new widgets: stats + panels

    private WeatherInfo? _weather; private DateTime _weatherAt;
    private List<Quote>? _quotes; private DateTime _quotesAt;
    private int _volLevel = -1; private bool _volMuted; private int _bright = -1; private DateTime _brightAt;

    public string WidgetStat(WidgetView view) => view.Descriptor.Kind switch
    {
        WidgetKind.Volume => _volMuted ? "muted" : (_volLevel >= 0 ? _volLevel + "%" : "—"),
        WidgetKind.Brightness => _bright >= 0 ? _bright + "%" : "—",
        WidgetKind.Weather => _weather?.Ok == true ? $"{_weather.TempC}°C" : "—",
        WidgetKind.Stocks => StockBarText(),
        WidgetKind.Todo => _settings.Todos.Count(t => !t.Done).ToString(),
        WidgetKind.Pomodoro => PomoText(),
        _ => ""
    };

    private string StockBarText()
    {
        if (_quotes == null || _quotes.Count == 0) return "—";
        var q = _quotes[0];
        string sym = q.Symbol.Replace("-USD", "");
        return q.Ok ? $"{sym} {CompactNum(q.Price)}" : sym;
    }

    private static string CompactNum(double n) =>
        n >= 1_000_000 ? (n / 1_000_000).ToString("0.0") + "M" :
        n >= 1000 ? (n / 1000).ToString("0.0") + "k" :
        n.ToString("0.##");

    /// <summary>Periodically refresh weather + stock + volume/brightness caches off the UI thread.</summary>
    private void RefreshInfoWidgets()
    {
        // Volume read is cheap; brightness (WMI) is slow → both cached off-thread to avoid bar jank.
        if (_allWidgets.Any(w => w.Descriptor.Kind == WidgetKind.Volume))
            System.Threading.Tasks.Task.Run(() => { _volLevel = SystemVolume.Level(); _volMuted = SystemVolume.Muted(); });
        if (_allWidgets.Any(w => w.Descriptor.Kind == WidgetKind.Brightness) && (DateTime.UtcNow - _brightAt).TotalSeconds > 3)
        {
            _brightAt = DateTime.UtcNow;
            System.Threading.Tasks.Task.Run(() => _bright = Brightness.Level());
        }

        if (_allWidgets.Any(w => w.Descriptor.Kind == WidgetKind.Weather) && (DateTime.UtcNow - _weatherAt).TotalMinutes > 12)
        {
            _weatherAt = DateTime.UtcNow;
            Weather.GetAsync(_settings.WeatherLocation).ContinueWith(t => { if (t.IsCompletedSuccessfully) _weather = t.Result; });
        }
        if (_allWidgets.Any(w => w.Descriptor.Kind == WidgetKind.Stocks) && (DateTime.UtcNow - _quotesAt).TotalMinutes > 2)
        {
            _quotesAt = DateTime.UtcNow;
            Stocks.GetAsync(_settings.StockSymbols.Split(',')).ContinueWith(t => { if (t.IsCompletedSuccessfully) _quotes = t.Result; });
        }
    }

    // ---- Volume ----

    public void ShowVolumePanel(WidgetView view)
    {
        const double W = 250;
        var panel = new StackPanel { Width = W };
        panel.Children.Add(SectionLabel("VOLUME"));
        if (!SystemVolume.Available) { panel.Children.Add(Hint("No audio output device found.")); OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W); return; }

        var val = new TextBlock { Text = SystemVolume.Level() + "%", Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold };
        panel.Children.Add(val);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Max(0, SystemVolume.Level()), Margin = new Thickness(0, 6, 0, 6) };
        slider.ValueChanged += (_, _) => { SystemVolume.SetLevel((int)slider.Value); val.Text = (int)slider.Value + "%"; };
        panel.Children.Add(slider);
        var mute = OpenButton(SystemVolume.Muted() ? "Unmute" : "Mute", Color.FromRgb(0x0A, 0x84, 0xFF), null);
        mute.MouseLeftButtonDown += (_, e) => { e.Handled = true; SystemVolume.ToggleMute(); CloseOverlay(); };
        panel.Children.Add(mute);
        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);
    }

    public void ShowBrightnessPanel(WidgetView view)
    {
        const double W = 250;
        var panel = new StackPanel { Width = W };
        panel.Children.Add(SectionLabel("BRIGHTNESS"));
        if (!Brightness.Available) { panel.Children.Add(Hint("Brightness control isn't available on this display (typical for desktop monitors).")); OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W); return; }

        var val = new TextBlock { Text = Brightness.Level() + "%", Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold };
        panel.Children.Add(val);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Max(0, Brightness.Level()), Margin = new Thickness(0, 6, 0, 6) };
        slider.ValueChanged += (_, _) => { val.Text = (int)slider.Value + "%"; };
        slider.PreviewMouseUp += (_, _) => Brightness.SetLevel((int)slider.Value);   // set on release (WMI is slow)
        panel.Children.Add(slider);
        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);
    }

    // ---- Weather ----

    public void ShowWeatherPanel(WidgetView view)
    {
        const double W = 290;
        var panel = new StackPanel { Width = W };
        panel.Children.Add(SectionLabel("WEATHER"));
        var bigVal = new TextBlock { Text = "…", Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeights.Bold };
        var desc = new TextBlock { Foreground = Sub(), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(bigVal); panel.Children.Add(desc);
        var details = new StackPanel(); panel.Children.Add(details);
        var fc = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(fc);
        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);

        void Apply(WeatherInfo w)
        {
            if (!w.Ok) { bigVal.Text = "—"; desc.Text = "Couldn't load weather."; return; }
            bigVal.Text = $"{w.TempC}°C";
            desc.Text = $"{w.Desc}  ·  {w.Location}";
            details.Children.Clear();
            details.Children.Add(StatLine("Feels like", $"{w.FeelsC}°C").row);
            details.Children.Add(StatLine("Humidity", $"{w.Humidity}%").row);
            details.Children.Add(StatLine("Wind", $"{w.WindKph:0} km/h").row);
            fc.Children.Clear();
            fc.Children.Add(new Border { Height = 1, Background = HairLine(), Margin = new Thickness(0, 2, 0, 8) });
            foreach (var (day, mn, mx, d) in w.Forecast.Take(3))
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dn = new TextBlock { Text = day, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
                var dd = new TextBlock { Text = d, Foreground = Sub(), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
                var tt = new TextBlock { Text = $"{mx}° / {mn}°", Foreground = Sub(), FontSize = 11.5 };
                Grid.SetColumn(dn, 0); Grid.SetColumn(dd, 1); Grid.SetColumn(tt, 2);
                g.Children.Add(dn); g.Children.Add(dd); g.Children.Add(tt);
                fc.Children.Add(g);
            }
        }
        if (_weather?.Ok == true) Apply(_weather);
        Weather.GetAsync(_settings.WeatherLocation).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            _weather = t.Result; _weatherAt = DateTime.UtcNow;
            Dispatcher.BeginInvoke(new Action(() => Apply(t.Result)));
        });
    }

    // ---- Stocks & crypto ----

    public void ShowStocksPanel(WidgetView view)
    {
        const double W = 290;
        var panel = new StackPanel { Width = W };
        panel.Children.Add(SectionLabel("STOCKS & CRYPTO"));
        var list = new StackPanel(); panel.Children.Add(list);
        var status = new TextBlock { Text = "Loading…", Foreground = Sub(), FontSize = 11.5, Margin = new Thickness(2, 4, 0, 0) };
        panel.Children.Add(status);
        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 10)), view, W);

        void Apply(List<Quote> qs)
        {
            list.Children.Clear();
            status.Visibility = Visibility.Collapsed;
            foreach (var q in qs)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                var sym = new TextBlock { Text = q.Symbol, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var price = new TextBlock { Text = q.Ok ? "$" + CompactNum(q.Price) : "n/a", Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCD)), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var up = q.ChangePct >= 0;
                var chg = new TextBlock { Text = q.Ok ? $"{(up ? "+" : "")}{q.ChangePct:0.0}%" : "", Foreground = new SolidColorBrush(up ? Color.FromRgb(0x39, 0xD3, 0x53) : Color.FromRgb(0xFF, 0x5B, 0x52)), FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(sym, 0); Grid.SetColumn(price, 1); Grid.SetColumn(chg, 2);
                g.Children.Add(sym); g.Children.Add(price); g.Children.Add(chg);
                list.Children.Add(g);
            }
            if (qs.Count == 0) { status.Visibility = Visibility.Visible; status.Text = "Set symbols in Advanced settings."; }
        }
        if (_quotes != null) Apply(_quotes);
        Stocks.GetAsync(_settings.StockSymbols.Split(',')).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            _quotes = t.Result; _quotesAt = DateTime.UtcNow;
            Dispatcher.BeginInvoke(new Action(() => Apply(t.Result)));
        });
    }

    // ---- To-Do ----

    public void ShowTodoPanel(WidgetView view)
    {
        const double W = 300;
        var panel = new StackPanel { Width = W };
        panel.Children.Add(SectionLabel("TO-DO"));
        var list = new StackPanel();
        panel.Children.Add(list);

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var item in _settings.Todos.ToList())
            {
                var captured = item;
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var chk = new CheckBox { IsChecked = item.Done, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                chk.Checked += (_, _) => { captured.Done = true; _settings.Save(); Rebuild(); };
                chk.Unchecked += (_, _) => { captured.Done = false; _settings.Save(); };
                var txt = new TextBlock { Text = item.Text, Foreground = item.Done ? Sub() : Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextDecorations = item.Done ? TextDecorations.Strikethrough : null };
                var del = new TextBlock { Text = "✕", Foreground = Sub(), FontSize = 12, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
                del.MouseLeftButtonDown += (_, e) => { e.Handled = true; _settings.Todos.Remove(captured); _settings.Save(); Rebuild(); };
                Grid.SetColumn(chk, 0); Grid.SetColumn(txt, 1); Grid.SetColumn(del, 2);
                row.Children.Add(chk); row.Children.Add(txt); row.Children.Add(del);
                list.Children.Add(row);
            }
            if (_settings.Todos.Count == 0) list.Children.Add(new TextBlock { Text = "Nothing yet — add a task below.", Foreground = Sub(), FontSize = 12, Margin = new Thickness(2, 2, 0, 2) });
        }
        Rebuild();

        panel.Children.Add(new Border { Height = 1, Background = HairLine(), Margin = new Thickness(0, 8, 0, 8) });
        var add = new Grid();
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x30)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), FontSize = 12.5, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        var addBtn = SmallButton("Add", Color.FromRgb(0x0A, 0x84, 0xFF)); addBtn.Margin = new Thickness(8, 0, 0, 0);
        void Commit() { var t = box.Text.Trim(); if (t.Length == 0) return; _settings.Todos.Add(new TodoItem { Text = t }); _settings.Save(); box.Clear(); Rebuild(); }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        addBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; Commit(); };
        Grid.SetColumn(box, 0); Grid.SetColumn(addBtn, 1);
        add.Children.Add(box); add.Children.Add(addBtn);
        panel.Children.Add(add);

        _overlayClosed = () => _settings.Save();
        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);
        Dispatcher.BeginInvoke(new Action(() => { box.Focus(); System.Windows.Input.Keyboard.Focus(box); }), DispatcherPriority.Input);
    }

    // ---- Pomodoro ----

    private DispatcherTimer? _pomoTimer;
    private int _pomoRemaining; private bool _pomoRunning; private bool _pomoBreak;

    private string PomoText()
    {
        int s = _pomoRunning || _pomoRemaining > 0 ? _pomoRemaining : _settings.PomodoroWorkMin * 60;
        return $"{s / 60:0}:{s % 60:00}";
    }

    private void PomoTick()
    {
        if (!_pomoRunning) return;
        if (_pomoRemaining > 0) _pomoRemaining--;
        if (_pomoRemaining <= 0)
        {
            _pomoBreak = !_pomoBreak;
            _pomoRemaining = (_pomoBreak ? _settings.PomodoroBreakMin : _settings.PomodoroWorkMin) * 60;
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
    }

    public void ShowPomodoroPanel(WidgetView view)
    {
        const double W = 240;
        if (_pomoTimer == null) { _pomoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _pomoTimer.Tick += (_, _) => PomoTick(); _pomoTimer.Start(); }
        if (_pomoRemaining == 0 && !_pomoRunning) _pomoRemaining = _settings.PomodoroWorkMin * 60;

        var panel = new StackPanel { Width = W };
        panel.Children.Add(SectionLabel("POMODORO"));
        var phase = new TextBlock { Foreground = Sub(), FontSize = 12.5 };
        var time = new TextBlock { Foreground = Brushes.White, FontSize = 36, FontWeight = FontWeights.Bold };
        panel.Children.Add(phase); panel.Children.Add(time);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var startBtn = SmallButton("", Color.FromRgb(0xE0, 0x53, 0x3C)); startBtn.Width = 84;
        var resetBtn = SmallButton("Reset", Color.FromRgb(0x3A, 0x3A, 0x40)); resetBtn.Margin = new Thickness(8, 0, 0, 0);
        var skipBtn = SmallButton("Skip", Color.FromRgb(0x3A, 0x3A, 0x40)); skipBtn.Margin = new Thickness(8, 0, 0, 0);
        btns.Children.Add(startBtn); btns.Children.Add(resetBtn); btns.Children.Add(skipBtn);
        panel.Children.Add(btns);

        void Refresh()
        {
            phase.Text = _pomoBreak ? "Break" : "Focus";
            time.Text = $"{_pomoRemaining / 60:0}:{_pomoRemaining % 60:00}";
            ((TextBlock)startBtn.Child).Text = _pomoRunning ? "Pause" : "Start";
        }
        Refresh();
        var ui = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        ui.Tick += (_, _) => Refresh();
        ui.Start();
        _overlayClosed = () => ui.Stop();

        startBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; _pomoRunning = !_pomoRunning; Refresh(); };
        resetBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; _pomoRunning = false; _pomoBreak = false; _pomoRemaining = _settings.PomodoroWorkMin * 60; Refresh(); };
        skipBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; _pomoBreak = !_pomoBreak; _pomoRemaining = (_pomoBreak ? _settings.PomodoroBreakMin : _settings.PomodoroWorkMin) * 60; Refresh(); };

        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);
    }

    // ---- Tic-Tac-Toe ----

    public void ShowTicTacToePanel(WidgetView view)
    {
        const double W = 224;
        var board = new char[9];
        var panel = new StackPanel { Width = W };
        var status = new TextBlock { Text = "Your move (X)", Foreground = Sub(), FontSize = 12.5, Margin = new Thickness(2, 0, 0, 8) };
        panel.Children.Add(status);
        var grid = new UniformGrid { Rows = 3, Columns = 3, Width = W - 28, Height = W - 28, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(grid);
        var cells = new Border[9];

        void Draw()
        {
            for (int i = 0; i < 9; i++)
                ((TextBlock)cells[i].Child).Text = board[i] == '\0' ? "" : board[i].ToString();
        }
        char Winner()
        {
            int[][] lines = { new[]{0,1,2}, new[]{3,4,5}, new[]{6,7,8}, new[]{0,3,6}, new[]{1,4,7}, new[]{2,5,8}, new[]{0,4,8}, new[]{2,4,6} };
            foreach (var l in lines) if (board[l[0]] != '\0' && board[l[0]] == board[l[1]] && board[l[1]] == board[l[2]]) return board[l[0]];
            return '\0';
        }
        int AiMove()
        {
            int[][] lines = { new[]{0,1,2}, new[]{3,4,5}, new[]{6,7,8}, new[]{0,3,6}, new[]{1,4,7}, new[]{2,5,8}, new[]{0,4,8}, new[]{2,4,6} };
            foreach (char who in new[] { 'O', 'X' })   // win, then block
                foreach (var l in lines)
                {
                    int empty = -1, count = 0;
                    foreach (var c in l) { if (board[c] == who) count++; else if (board[c] == '\0') empty = c; }
                    if (count == 2 && empty >= 0) return empty;
                }
            if (board[4] == '\0') return 4;
            var free = Enumerable.Range(0, 9).Where(i => board[i] == '\0').ToList();
            return free.Count == 0 ? -1 : free[Random.Shared.Next(free.Count)];
        }
        void Reset() { Array.Fill(board, '\0'); status.Text = "Your move (X)"; Draw(); }

        for (int i = 0; i < 9; i++)
        {
            int idx = i;
            var cell = new Border
            {
                Margin = new Thickness(3), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (board[idx] != '\0' || Winner() != '\0') return;
                board[idx] = 'X'; Draw();
                if (Winner() == 'X') { status.Text = "You win! \U0001F389"; return; }
                if (board.All(c => c != '\0')) { status.Text = "Draw."; return; }
                int m = AiMove(); if (m >= 0) board[m] = 'O'; Draw();
                if (Winner() == 'O') status.Text = "Lintel wins.";
                else if (board.All(c => c != '\0')) status.Text = "Draw.";
            };
            cells[i] = cell;
            grid.Children.Add(cell);
        }
        Draw();

        var reset = OpenButton("New game", Color.FromRgb(0x0A, 0x84, 0xFF), Reset);
        panel.Children.Add(reset);
        OpenDevOverlay(Card(panel, new Thickness(14, 12, 14, 12)), view, W);
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

    // =========================================================== customization (themes + widgets)

    /// <summary>(Re)load user themes and widgets from disk into the registries.</summary>
    private void LoadCustomization()
    {
        Customization.EnsureDirs();
        Themes.SetCustom(Customization.LoadThemes());
        WidgetCatalog.SetCustom(Customization.LoadWidgets());
    }

    public void ImportThemeFromFile()
    {
        var path = PickJsonFile("Import a Lintel theme");
        if (path == null) return;
        try
        {
            var name = Customization.ImportTheme(path);
            LoadCustomization();
            if (name != null) ChangeTheme(name);          // load + apply it immediately
            else ShowMessage("Import theme", "That file didn't look like a valid theme.");
        }
        catch (Exception ex) { ShowMessage("Import theme", "Couldn't import that theme:\n" + ex.Message); }
    }

    public void ImportWidgetFromFile()
    {
        var path = PickJsonFile("Import a Lintel widget");
        if (path == null) return;
        try
        {
            var key = Customization.ImportWidget(path);
            LoadCustomization();
            if (key != null)
            {
                AddWidget(key);                            // drop it on the bar right away
                if (AddPopup.IsOpen) BuildAddList();
            }
            else ShowMessage("Import widget", "That file didn't look like a valid widget.");
        }
        catch (Exception ex) { ShowMessage("Import widget", "Couldn't import that widget:\n" + ex.Message); }
    }

    public void OpenCustomizationFolder() => Customization.OpenFolder();

    private string? PickJsonFile(string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Lintel JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        FocusWindowForDialog();
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private void ShowMessage(string title, string body)
    {
        var stack = new StackPanel { Width = 300 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 16, Foreground = Brushes.White });
        stack.Children.Add(new TextBlock { Text = body, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC5)), FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        OpenOverlayCentered(Card(stack, new Thickness(18, 16, 18, 16)), 336);
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
        panel.ImportThemeRequested += ImportThemeFromFile;
        panel.ImportWidgetRequested += ImportWidgetFromFile;
        panel.OpenFolderRequested += OpenCustomizationFolder;
        var card = Card(panel, new Thickness(0));
        OpenOverlayCentered(card, panel.CurrentWidth, focusable: true);
        if (advanced) panel.ExpandToAdvanced();
    }

    public void OpenSettingsFromTray() => OpenSettings();
    public void OpenSettingsAdvancedFromTray() => OpenSettings(advanced: true);
    public void ToggleCustomizeFromTray() => ToggleCustomize();

    // =========================================================== helpers

    private static Brush GlossBrush(double strength)
    {
        byte A(double f) => (byte)Math.Clamp(f * strength, 0, 255);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(A(0x92), 0xFF, 0xFF, 0xFF), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(A(0x2C), 0xFF, 0xFF, 0xFF), 0.45));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.50));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(A(0x10), 0x00, 0x00, 0x00), 1.0));
        b.Freeze();
        return b;
    }

    private static Brush VerticalGradient(Color top, Color bottom)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) }
        };
        b.Freeze();
        return b;
    }

    private static SolidColorBrush BrushFrom(string hex, Color fallback)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch { var b = new SolidColorBrush(fallback); b.Freeze(); return b; }
    }
}
