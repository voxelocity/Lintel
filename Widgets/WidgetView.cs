using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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

    public WidgetView(IWidgetHost host, WidgetDescriptor descriptor, bool preview = false)
    {
        _host = host;
        Descriptor = descriptor;
        _preview = preview;

        var theme = Themes.For(host.Settings.Theme, host.Settings.WidgetCornerRadius);
        _idleBg = Frozen(theme.BubbleIdle);
        _hoverBg = Frozen(theme.BubbleHover);
        _iconSat = theme.IconSaturation;
        _fgColor = ParseColor(host.Settings.ForegroundColor, Colors.White);

        CornerRadius = new CornerRadius(theme.CornerRadius);
        Height = Math.Max(22, host.Settings.BarHeight - 8);   // uniform bubble height
        Padding = theme.Padding;
        Background = _idleBg;
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

    private UIElement BuildIconWidget(string key, double size)
    {
        var icon = IconPath(key, _fgColor, size);
        return WrapWithBadge(icon);
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

    private Image? _mediaCover;
    private Path? _mediaPlaceholder;
    private Controls.Visualizer? _mediaViz;

    private UIElement BuildMedia()
    {
        double sz = Math.Max(18, _host.Settings.BarHeight - 10);
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

    private UIElement WrapWithBadge(UIElement content)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(content);

        // Sit on the bubble's top-right corner, lifted up to the bar's top edge (never above it).
        double gap = Math.Max(0, (_host.Settings.BarHeight - Height) / 2.0);
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
        BorderThickness = on ? new Thickness(1) : new Thickness(0);
        BorderBrush = on ? Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) : null;
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
        if (_host.HasDropdown(this)) _host.WidgetHoverLeft(this);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_host.Customizing) return;
        // Don't start a drag when the click is on the remove (x) badge — let it delete.
        if (_removeBadge != null && e.OriginalSource is DependencyObject src && IsWithin(src, _removeBadge)) return;
        _host.BeginWidgetDrag(this, e);
        e.Handled = true;
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
                if (!_host.OpenOnHover) _host.OpenWidgetDropdown(this, hover: false);
                break;
        }
    }

    // ---- helpers ----

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Brush ParseBrush(string hex, Brush fallback) { try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; } catch { return fallback; } }
    private static Color ParseColor(string hex, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; } }
}
