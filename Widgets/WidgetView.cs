using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Lintel.Controls;
using Lintel.Models;
using Lintel.Services;

namespace Lintel.Widgets;

/// <summary>A single widget bubble on the bar. Renders by kind, animates, and (in customize
/// mode) can be dragged and removed.</summary>
public sealed class WidgetView : Border
{
    private readonly IWidgetHost _host;
    public WidgetDescriptor Descriptor { get; }
    public string Key => Descriptor.Key;

    private Metric? _metric;
    private TextBlock? _dynamicText;
    private TextBlock? _modeText;
    private TextBlock? _loadText;
    private TextBlock? _statText;
    private TextBlock? _wsText;
    private System.Windows.Shapes.Ellipse? _loadDot;
    private StackPanel? _tabsHost;
    private string _winSig = "";
    private Border? _removeBadge;

    private readonly Brush _idleBg;
    private readonly Brush _hoverBg;
    private readonly double _iconSat;
    private readonly Color _fgColor;
    private readonly bool _preview;
    private Color? _bubbleBorder;
    private double _bubbleBorderThickness = 1;
    private double _bubbleGloss;

    public WidgetView(IWidgetHost host, WidgetDescriptor descriptor, bool preview = false)
    {
        _host = host;
        Descriptor = descriptor;
        _preview = preview;

        var theme = Themes.Resolve(host.Settings);
        // Tactile bevel: a faint top-to-bottom sheen so each bubble reads as a raised key. The clean
        // themes (Squircles/Power/Mond) opt out via FlatBubble and keep flat fills.
        _idleBg = theme.FlatBubble ? Frozen(theme.BubbleIdle) : Bevel(theme.BubbleIdle);
        _hoverBg = theme.FlatBubble ? Frozen(theme.BubbleHover) : Bevel(theme.BubbleHover);
        _iconSat = theme.IconSaturation;
        _fgColor = ParseColor(host.Settings.ForegroundColor, Colors.White);

        CornerRadius = new CornerRadius(theme.CornerRadius);
        Height = Math.Max(18, host.BarHeight - 8 - theme.BubbleVInset);   // uniform bubble height (theme may resize the bar / inset the bubble)
        Padding = theme.Padding;
        Background = _idleBg;
        _bubbleBorder = theme.BubbleBorder;
        _bubbleBorderThickness = theme.BubbleBorderThickness;
        _bubbleGloss = theme.BubbleGloss;
        if (_bubbleBorder is Color bb)
        {
            BorderBrush = Frozen(bb);
            BorderThickness = new Thickness(_bubbleBorderThickness);
        }
        // Soft contact shadow gives the clean themes a gentle sense of depth (skipped where the theme
        // already provides its own depth: separated pills / heavy gloss OS themes). One shared frozen
        // effect is reused across every bubble instead of allocating one per widget.
        if (!_preview && !theme.SeparatedZones && _bubbleGloss <= 0.001)
            Effect = BubbleShadow;
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Arrow;

        Foreground = ParseBrush(host.Settings.ForegroundColor, Brushes.White);
        Accent = ParseColor(host.Settings.AccentColor, Color.FromRgb(0x0A, 0x84, 0xFF));

        Child = BuildContent();

        if (_preview) { IsHitTestVisible = false; return; }   // static preview for the add menu

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        MouseLeftButtonUp += OnMouseUp;
    }

    private Brush Foreground { get; }
    private Color Accent { get; }

    private UIElement BuildContent() => Descriptor.Kind switch
    {
        WidgetKind.Gauge => BuildGauge(),
        WidgetKind.Clock => BuildText(out _dynamicText, semibold: true),
        WidgetKind.Date => BuildText(out _dynamicText, semibold: false, opacity: 0.9),
        WidgetKind.ActiveApp => BuildText(out _dynamicText, semibold: true),
        WidgetKind.Mode => BuildMode(),
        WidgetKind.Settings => BuildIconWidget("settings", 16),
        WidgetKind.Note => BuildNote(),
        WidgetKind.Windows => BuildAppTabs(),
        WidgetKind.Workspaces => BuildWorkspaces(),
        WidgetKind.Load => BuildLoad(),
        WidgetKind.Media => BuildMedia(),
        WidgetKind.Claude => BuildStatWidget("claude", Color.FromRgb(0xD9, 0x77, 0x57), "—"),
        WidgetKind.GitHub => BuildStatWidget("github", Color.FromRgb(0xE6, 0xE6, 0xEA), "—"),
        WidgetKind.Custom => BuildCustom(),
        WidgetKind.Volume => BuildSlider("volume", Color.FromRgb(0xDE, 0xE2, 0xEA), Accent, isVolume: true),
        WidgetKind.Brightness => BuildSlider("brightness", Color.FromRgb(0xFF, 0xC8, 0x3C), Color.FromRgb(0xFF, 0xB0, 0x22), isVolume: false),
        WidgetKind.Weather => BuildStatWidget("weather", Color.FromRgb(0x5C, 0xB4, 0xF0), _preview ? "18°C" : "—"),
        WidgetKind.Stocks => BuildStatWidget("stocks", Color.FromRgb(0x39, 0xD3, 0x53), _preview ? "BTC 67k" : "—"),
        WidgetKind.Todo => BuildStatWidget("todo", Accent, _preview ? "3" : "0"),
        WidgetKind.Pomodoro => BuildStatWidget("pomodoro", Color.FromRgb(0xE0, 0x53, 0x3C), _preview ? "25:00" : "25:00"),
        WidgetKind.TicTacToe => BuildStatWidget("tictactoe", Color.FromRgb(0xE6, 0xE6, 0xEA), ""),
        WidgetKind.Launcher => BuildIconWidget("launcher", 17),
        WidgetKind.Start => BuildIconWidget("start", 15, Color.FromRgb(0x36, 0x9E, 0xFF)),
        WidgetKind.Tray => BuildTray(),
        _ => BuildText(out _dynamicText, false)
    };

    // ---- gauges (icon only, no ring) ----

    private UIElement BuildGauge()
    {
        _metric = _host.GetMetric(Key);
        var sig = MetricStyle.For(Key).Signature;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Fixed value width so the bubble never resizes as the number changes.
        double valW = Key switch { "ram" => 86, "net" => 68, _ => 36 };
        var value = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 1, 0),
            Width = valW, TextAlignment = TextAlignment.Left
        };

        if (Key == "battery")
        {
            var bat = new BatteryIcon { VerticalAlignment = VerticalAlignment.Center, Saturation = _iconSat };
            bat.SetBinding(BatteryIcon.PercentProperty, new Binding(nameof(Metric.Percent)) { Source = _metric });
            row.Children.Add(bat);

            // Charging is shown by the bolt inside the battery — keep the marker out of the text.
            void UpdBattery()
            {
                bat.Charging = _metric.Text.Contains('⚡');
                value.Text = _metric.Text.Replace("⚡", "").Trim();
            }
            UpdBattery();
            if (!_preview)
                _metric.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Metric.Text)) UpdBattery(); };
        }
        else
        {
            row.Children.Add(IconPath(Key, IconColor(sig), 17));
            value.SetBinding(TextBlock.TextProperty, new Binding(nameof(Metric.Text)) { Source = _metric });
        }

        row.Children.Add(value);
        return WrapWithBadge(row);
    }

    // ---- text widgets ----

    private UIElement BuildText(out TextBlock tb, bool semibold, double opacity = 1.0)
    {
        tb = new TextBlock
        {
            FontSize = 12.5, FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Foreground, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center
        };
        Refresh(tb);
        return WrapWithBadge(tb);
    }

    private UIElement BuildMode()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _modeText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center };
        UpdateModeText();
        row.Children.Add(_modeText);
        return WrapWithBadge(row);
    }

    private UIElement BuildIconWidget(string key, double size, Color? tint = null)
    {
        var icon = IconPath(key, tint is Color c ? IconColor(c) : _fgColor, size);
        return WrapWithBadge(icon);
    }

    // System tray: the real notification-area icons, rendered inline, with a chevron to open the rest.
    private StackPanel? _trayHost;
    private string _traySig = "";

    private UIElement BuildTray()
    {
        _trayHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        RefreshTray(force: true);
        return WrapWithBadge(_trayHost);
    }

    private void RefreshTray(bool force = false)
    {
        if (_trayHost == null) return;
        var icons = _preview ? new List<Services.TrayItem>() : Services.SystemTray.Items();
        string sig = string.Join("|", icons.Select(i => i.Title));
        if (!force && sig == _traySig) return;
        _traySig = sig;

        _trayHost.Children.Clear();
        double sz = Math.Max(14, Math.Min(18, Height - 6));
        foreach (var item in icons.Take(10))
        {
            var img = new System.Windows.Controls.Image
            {
                Source = item.Icon, Width = sz, Height = sz, Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title,
                SnapsToDevicePixels = true
            };
            var captured = item;
            img.MouseLeftButtonUp += (_, e) => { if (_host.Customizing) return; e.Handled = true; captured.Invoke(false); };
            img.MouseRightButtonUp += (_, e) => { if (_host.Customizing) return; e.Handled = true; captured.Invoke(true); };
            _trayHost.Children.Add(img);
        }
        // Chevron → open the rest / the real overflow.
        var chev = IconPath("tray", _fgColor, 12);
        chev.Margin = new Thickness(3, 0, 1, 0); chev.Opacity = 0.75; chev.Cursor = Cursors.Hand;
        chev.MouseLeftButtonUp += (_, e) => { if (_host.Customizing) return; e.Handled = true; _host.ShowTray(this); };
        _trayHost.Children.Add(chev);
        if (icons.Count == 0)
        {
            var label = new TextBlock { Text = "Tray", FontSize = 12, Foreground = Foreground, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            _trayHost.Children.Insert(0, label);
        }
    }

    // ---- interactive widgets ----

    private UIElement BuildNote()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(IconPath("note", IconColor(Accent), 15));
        _dynamicText = new TextBlock { FontSize = 12.5, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis };
        RefreshNote();
        row.Children.Add(_dynamicText);
        return WrapWithBadge(row);
    }

    private UIElement BuildWorkspaces()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(ArrowButton("‹", -1));
        _wsText = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(7, 0, 7, 0), MinWidth = 64, TextTrimming = TextTrimming.CharacterEllipsis
        };
        RefreshWorkspace();
        row.Children.Add(_wsText);
        row.Children.Add(ArrowButton("›", +1));
        return WrapWithBadge(row);
    }

    private void RefreshWorkspace()
    {
        if (_wsText != null) _wsText.Text = _preview ? "Desktop 1" : DesktopInfo.CurrentName();
    }

    // A standout arrow rendered as its own little button (its own bubble + hover).
    private Border ArrowButton(string glyph, int dir)
    {
        var idle = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        var hover = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        var b = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
            Background = idle, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
        };
        b.MouseEnter += (_, _) => { if (!_host.Customizing) b.Background = hover; };
        b.MouseLeave += (_, _) => b.Background = idle;
        b.MouseLeftButtonDown += (_, e) =>
        {
            if (_host.Customizing) return;
            e.Handled = true;
            _host.SwitchWorkspace(dir);
            Dispatcher.BeginInvoke(new Action(RefreshWorkspace), System.Windows.Threading.DispatcherPriority.Background);
        };
        return b;
    }

    // Icon + compact stat (Claude tokens left / GitHub contributions). Updated via SetStat.
    private UIElement BuildStatWidget(string icon, Color tint, string initial)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(IconPath(icon, IconColor(tint), 16));
        _statText = new TextBlock
        {
            Text = initial, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 1, 0)
        };
        row.Children.Add(_statText);
        return WrapWithBadge(row);
    }

    /// <summary>Set the compact label on a Claude/GitHub widget.</summary>
    public void SetStat(string text) { if (_statText != null) _statText.Text = text; }

    // ---- inline physical sliders (volume / brightness) ----

    private TactileSlider? _slider;

    private UIElement BuildSlider(string iconKey, Color iconTint, Color fillAccent, bool isVolume)
    {
        var icon = IconPath(iconKey, IconColor(iconTint), 16);
        _slider = new TactileSlider(icon, fillAccent);
        int initial = _preview ? (isVolume ? 60 : 80)
                    : Math.Max(0, isVolume ? Services.SystemVolume.Level() : Services.Brightness.Level());
        _slider.SetValue(initial);

        if (!_preview)
        {
            if (isVolume) _slider.ValueChanged += v => Services.SystemVolume.SetLevel(v);     // cheap → live
            else _slider.ValueCommitted += v => Services.Brightness.SetLevel(v);              // WMI is slow → on release
        }
        return WrapWithBadge(_slider);
    }

    // ---- custom (user-defined) widgets ----

    private CustomWidgetSpec? _custSpec;
    private DateTime _cmdLastRun = DateTime.MinValue;

    private UIElement BuildCustom()
    {
        var spec = Descriptor.Custom ?? new CustomWidgetSpec();
        _custSpec = spec;
        var accent = ParseColor(spec.Accent, Accent);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var icon = BuildCustomIcon(spec.Icon, IconColor(accent));
        if (icon != null) row.Children.Add(icon);

        string initial = !string.IsNullOrEmpty(spec.Label) ? spec.Label
                       : spec.Type.Equals("command", StringComparison.OrdinalIgnoreCase) ? "…"
                       : spec.Name;
        _statText = new TextBlock
        {
            Text = initial, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(icon != null ? 6 : 0, 0, 1, 0),
            MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (string.IsNullOrEmpty(initial)) _statText.Visibility = Visibility.Collapsed;
        row.Children.Add(_statText);
        return WrapWithBadge(row);
    }

    /// <summary>Icon for a custom widget: a single emoji/char, a built-in icon key, or raw path data.</summary>
    private UIElement? BuildCustomIcon(string icon, Color color)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();

        // Built-in icon key (e.g. "cpu", "github").
        if (Icons.Get(icon) != null) return IconPath(icon, color, 16);

        // A short string → treat as a text glyph (emoji or letter).
        var info = new System.Globalization.StringInfo(icon);
        if (info.LengthInTextElements <= 2)
            return new TextBlock { Text = icon, FontSize = 14, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };

        // Otherwise assume SVG-style path data.
        try
        {
            var geo = Geometry.Parse(icon);
            var brush = new SolidColorBrush(color); brush.Freeze();
            return new Path { Data = geo, Fill = brush, Stretch = Stretch.Uniform, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        }
        catch { return null; }
    }

    private void RefreshCustom()
    {
        var spec = _custSpec;
        if (spec == null || _preview) return;
        if (!spec.Type.Equals("command", StringComparison.OrdinalIgnoreCase)) return;
        if (!_host.Settings.EnableCommandWidgets) { SetStat("(disabled)"); return; }

        double interval = Math.Max(500, spec.IntervalMs);
        if ((DateTime.UtcNow - _cmdLastRun).TotalMilliseconds < interval) return;
        _cmdLastRun = DateTime.UtcNow;

        var cmd = spec.Command;
        Services.Customization.RunCommandAsync(cmd).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.BeginInvoke(new Action(() => { if (!string.IsNullOrEmpty(t.Result)) SetStat(t.Result); }));
        });
    }

    private Image? _mediaCover;
    private Path? _mediaPlaceholder;
    private Controls.Visualizer? _mediaViz;

    private UIElement BuildMedia()
    {
        double sz = Math.Max(18, _host.BarHeight - 10);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var coverGrid = new Grid { Width = sz, Height = sz };
        var coverBorder = new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) };
        _mediaPlaceholder = IconPath("media", IconColor(Accent), sz * 0.55);
        _mediaCover = new Image { Stretch = Stretch.UniformToFill };
        var inner = new Grid();
        inner.Children.Add(_mediaPlaceholder);
        inner.Children.Add(_mediaCover);
        coverBorder.Child = inner;
        coverGrid.Children.Add(coverBorder);
        row.Children.Add(coverGrid);

        _mediaViz = new Controls.Visualizer { Width = 32, Height = sz, BarColor = IconColor(Accent), Bars = 11, Margin = new Thickness(7, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_mediaViz);

        UpdateMedia();
        return WrapWithBadge(row);
    }

    public void UpdateMedia()
    {
        if (_mediaCover == null) return;
        var m = _host.Media.Current;
        _mediaCover.Source = m.Cover;
        if (_mediaPlaceholder != null) _mediaPlaceholder.Visibility = m.Cover == null ? Visibility.Visible : Visibility.Collapsed;
        if (_mediaViz != null) { _mediaViz.BarColor = m.Accent; _mediaViz.Active = m.IsPlaying; }
    }

    private UIElement BuildLoad()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _loadDot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Fill = Brushes.Gray };
        row.Children.Add(_loadDot);
        _loadText = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 1, 0) };
        row.Children.Add(_loadText);
        var caret = new TextBlock { Text = "▾", FontSize = 9, Opacity = 0.6, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) };
        row.Children.Add(caret);
        RefreshLoad();
        return WrapWithBadge(row);
    }

    // ---- app tabs ----

    private UIElement BuildAppTabs()
    {
        _tabsHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        RefreshAppTabs(force: true);
        return WrapWithBadge(_tabsHost);
    }

    private void RefreshAppTabs(bool force = false)
    {
        if (_tabsHost == null) return;
        bool compressed = _host.Settings.AppTabsCompressed;
        var windows = WindowList.Enumerate();

        string sig = compressed + "|" + string.Join("|", windows.Take(8).Select(w => w.Title));
        if (!force && sig == _winSig) return;
        _winSig = sig;

        _tabsHost.Children.Clear();

        if (compressed)
        {
            var focused = windows.Count > 0 ? windows[0] : new AppWindow(IntPtr.Zero, "Desktop", "");
            _tabsHost.Children.Add(Tab(focused.Title, true, () => { _host.Settings.AppTabsCompressed = false; RefreshAppTabs(true); }));
            _tabsHost.Children.Add(IconButton("▾", () => _host.ShowWindowSwitcher(this))); // expand to dropdown
            return;
        }

        // full tabs
        _tabsHost.Children.Add(IconButton("‹", () => { _host.Settings.AppTabsCompressed = true; RefreshAppTabs(true); })); // collapse
        int shown = Math.Min(6, windows.Count);
        for (int i = 0; i < shown; i++)
        {
            var w = windows[i];
            var handle = w.Handle;
            _tabsHost.Children.Add(Tab(w.Title, i == 0, () => WindowList.Activate(handle)));
        }
        if (windows.Count > shown)
            _tabsHost.Children.Add(IconButton($"+{windows.Count - shown}", () => _host.ShowWindowSwitcher(this), text: true));
    }

    private Border Tab(string title, bool focused, Action onClick)
    {
        string text = title.Length > 16 ? title[..16] + "…" : title;
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(2, 0, 0, 0),
            Background = focused ? new SolidColorBrush(Color.FromArgb(0x33, Accent.R, Accent.G, Accent.B)) : Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = focused ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Foreground, Opacity = focused ? 1 : 0.85 }
        };
        b.MouseLeftButtonDown += (_, e) => { if (_host.Customizing) return; e.Handled = true; onClick(); };
        return b;
    }

    private Border IconButton(string glyph, Action onClick, bool text = false)
    {
        var b = new Border
        {
            Padding = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontSize = 11, Foreground = Foreground, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center }
        };
        b.MouseLeftButtonDown += (_, e) => { if (_host.Customizing) return; e.Handled = true; onClick(); };
        return b;
    }

    private Border Arrow(string glyph, int dir)
    {
        var b = new Border
        {
            Padding = new Thickness(3, 0, 3, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontSize = 13, Foreground = Foreground, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center }
        };
        b.MouseLeftButtonDown += (_, e) => { if (_host.Customizing) return; e.Handled = true; _host.SwitchWorkspace(dir); };
        return b;
    }

    private static Path IconPath(string key, Color color, double size)
    {
        var brush = new SolidColorBrush(color); brush.Freeze();
        return new Path { Data = Icons.Get(key), Fill = brush, Stretch = Stretch.Uniform, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
    }

    // ---- remove badge (top-right corner) ----

    private Grid? _contentRoot;

    private UIElement WrapWithBadge(UIElement content)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5) };
        _contentRoot = grid;

        // Glossy sheen across the top of the bubble (XP / Aero themes), behind the content.
        if (_bubbleGloss > 0)
        {
            byte A(double f) => (byte)Math.Clamp(f * _bubbleGloss, 0, 255);
            var gloss = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gloss.GradientStops.Add(new GradientStop(Color.FromArgb(A(0xB0), 0xFF, 0xFF, 0xFF), 0.0));
            gloss.GradientStops.Add(new GradientStop(Color.FromArgb(A(0x40), 0xFF, 0xFF, 0xFF), 0.48));
            gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.52));
            gloss.Freeze();
            grid.Children.Add(new Border
            {
                CornerRadius = CornerRadius,
                Background = gloss,
                IsHitTestVisible = false,
                Margin = new Thickness(-Padding.Left, 0, -Padding.Right, 0)   // span the bubble's padding
            });
        }

        grid.Children.Add(content);

        // Sit on the bubble's top-right corner, lifted up to the bar's top edge (never above it).
        double gap = Math.Max(0, (_host.BarHeight - Height) / 2.0);
        _removeBadge = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -gap, -7, 0),
            Visibility = Visibility.Collapsed, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "✕", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) }
        };
        _removeBadge.MouseLeftButtonDown += (s, e) => { e.Handled = true; _host.RemoveWidget(this); };
        grid.Children.Add(_removeBadge);
        return grid;
    }

    // ---- refresh ----

    public void RefreshDynamic()
    {
        switch (Descriptor.Kind)
        {
            case WidgetKind.Note: RefreshNote(); break;
            case WidgetKind.Windows: RefreshAppTabs(); break;
            case WidgetKind.Load: RefreshLoad(); break;
            case WidgetKind.Media: UpdateMedia(); break;
            case WidgetKind.Mode: UpdateModeText(); break;
            case WidgetKind.Workspaces: RefreshWorkspace(); break;
            case WidgetKind.Tray: RefreshTray(); break;
            case WidgetKind.Custom: RefreshCustom(); break;
            case WidgetKind.Volume:
            case WidgetKind.Brightness:
                if (!_preview && _slider != null) { int lv = _host.WidgetLevel(this); if (lv >= 0) _slider.SetValue(lv); }
                break;
            case WidgetKind.Weather:
            case WidgetKind.Stocks:
            case WidgetKind.Todo:
            case WidgetKind.Pomodoro:
                if (!_preview && _statText != null) SetStat(_host.WidgetStat(this));
                break;
            default: if (_dynamicText != null) Refresh(_dynamicText); break;
        }
    }

    private void Refresh(TextBlock tb)
    {
        var now = DateTime.Now;
        tb.Text = Descriptor.Kind switch
        {
            WidgetKind.Clock => now.ToString(_host.Settings.Use24HourClock ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture),
            WidgetKind.Date => now.ToString("ddd d MMM", CultureInfo.CurrentCulture),
            WidgetKind.ActiveApp => _host.ActiveAppName,
            _ => tb.Text
        };
    }

    private void RefreshNote()
    {
        if (_dynamicText == null) return;
        var text = _host.Settings.NoteText?.Replace("\r", " ").Replace("\n", " ").Trim() ?? "";
        _dynamicText.Text = string.IsNullOrEmpty(text) ? "Note" : text;
        _dynamicText.Opacity = string.IsNullOrEmpty(text) ? 0.7 : 1.0;
    }

    private void RefreshLoad()
    {
        if (_loadText == null || _loadDot == null) return;
        double v = Math.Max(_host.GetMetric("cpu").Percent, Math.Max(_host.GetMetric("ram").Percent, _host.GetMetric("gpu").Percent));
        (string word, Color c) = v < 40 ? ("Low", Color.FromRgb(0x30, 0xD1, 0x58))
                               : v < 75 ? ("Medium", Color.FromRgb(0xFF, 0xD6, 0x0A))
                               : ("High", Color.FromRgb(0xFF, 0x45, 0x3A));
        _loadText.Text = word;
        _loadDot.Fill = new SolidColorBrush(c);
    }

    private void UpdateModeText()
    {
        if (_modeText == null) return;
        _modeText.Text = _host.Settings.Mode switch
        {
            VisibilityMode.AlwaysOn => "● Always",
            VisibilityMode.AutoHide => "▲ Auto",
            _ => "◉ Dynamic"
        };
    }

    // ---- customize ----

    public void SetCustomizing(bool on)
    {
        if (_removeBadge != null) _removeBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Cursor = on ? Cursors.SizeAll : Cursors.Arrow;
        if (on)
        {
            BorderThickness = new Thickness(1);
            BorderBrush = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        }
        else  // restore the theme's bubble border (if any)
        {
            BorderThickness = _bubbleBorder != null ? new Thickness(_bubbleBorderThickness) : new Thickness(0);
            BorderBrush = _bubbleBorder is Color bb ? Frozen(bb) : null;
        }
    }

    // ---- interaction ----

    private Color IconColor(Color preferred) => _iconSat >= 0.999 ? preferred : ColorUtil.Desaturate(preferred, _iconSat);

    public void ResetBackground() => Background = _idleBg;

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Background = _hoverBg;
        if (!_host.Customizing && _host.OpenOnHover && _host.HasDropdown(this))
            _host.OpenWidgetDropdown(this, hover: true);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        Background = _idleBg;
        PressUp();
        if (_host.HasDropdown(this)) _host.WidgetHoverLeft(this);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_host.Customizing) { PressDown(); return; }
        // Don't start a drag when the click is on the remove (x) badge — let it delete.
        if (_removeBadge != null && e.OriginalSource is DependencyObject src && IsWithin(src, _removeBadge)) return;
        _host.BeginWidgetDrag(this, e);
        e.Handled = true;
    }

    // ---- tactile press feedback (scale the content slightly on tap) ----

    private ScaleTransform? _pressScale;

    // Widgets whose own surface is the click target (not internal sliders / tab strips).
    private bool TapWidget => Descriptor.Kind is not (WidgetKind.Volume or WidgetKind.Brightness
        or WidgetKind.Tray or WidgetKind.Windows or WidgetKind.Workspaces);

    private void PressDown()
    {
        if (!TapWidget || _contentRoot == null || _preview) return;
        _pressScale ??= new ScaleTransform(1, 1);
        _contentRoot.RenderTransform = _pressScale;
        PressAnim(0.92);
    }

    private void PressUp()
    {
        if (_pressScale == null) return;
        PressAnim(1.0);
    }

    private void PressAnim(double to)
    {
        if (_pressScale == null) return;
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to < 1 ? 70 : 120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        PressUp();
        if (_host.Customizing) return;
        switch (Descriptor.Kind)
        {
            case WidgetKind.Mode: _host.OnModeClicked(); UpdateModeText(); break;
            case WidgetKind.Settings: _host.OnSettingsClicked(); break;
            case WidgetKind.Note: _host.ShowNote(this); break;
            case WidgetKind.Gauge:
            case WidgetKind.Load:
            case WidgetKind.Media:
            case WidgetKind.Claude:
            case WidgetKind.GitHub:
            case WidgetKind.Weather:
            case WidgetKind.Stocks:
            case WidgetKind.Todo:
            case WidgetKind.Pomodoro:
            case WidgetKind.TicTacToe:
                if (!_host.OpenOnHover) _host.OpenWidgetDropdown(this, hover: false);
                break;
            case WidgetKind.Custom:
                if (!string.IsNullOrWhiteSpace(_custSpec?.OnClick)) OpenTarget(_custSpec!.OnClick);
                break;
            case WidgetKind.Launcher: _host.ShowLauncher(this); break;
            case WidgetKind.Start: _host.OpenStartMenu(); break;
            case WidgetKind.Tray: _host.ShowTray(this); break;
        }
    }

    // ---- helpers ----

    private static void OpenTarget(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.Trim()) { UseShellExecute = true }); }
        catch { /* ignore bad targets */ }
    }

    // Shared, frozen contact shadow — created once, reused by every bubble that opts in.
    private static readonly System.Windows.Media.Effects.DropShadowEffect BubbleShadow = FreezeShadow();
    private static System.Windows.Media.Effects.DropShadowEffect FreezeShadow()
    {
        var e = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Direction = 270, Opacity = 0.28, Color = Colors.Black };
        e.Freeze();
        return e;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>A subtle vertical sheen built from a base bubble colour — brighter at the top, a touch
    /// deeper at the bottom — for a raised, tactile feel without leaving the clean aesthetic.</summary>
    private static Brush Bevel(Color c)
    {
        byte Up(int d) => (byte)Math.Clamp(c.A + d, 0, 255);
        var top = Color.FromArgb(Up(0x12), c.R, c.G, c.B);
        var bot = Color.FromArgb(Up(-0x05), c.R, c.G, c.B);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(top, 0));
        b.GradientStops.Add(new GradientStop(c, 0.55));
        b.GradientStops.Add(new GradientStop(bot, 1));
        b.Freeze();
        return b;
    }
    private static Brush ParseBrush(string hex, Brush fallback) { try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; } catch { return fallback; } }
    private static Color ParseColor(string hex, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; } }
}
