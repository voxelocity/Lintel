using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Lintel.Controls;
using Lintel.Models;
using Lintel.Services;

namespace Lintel.Widgets;

/// <summary>A single widget pill on the bar. Renders by kind, animates, and (in customize
/// mode) can be dragged and removed.</summary>
public sealed class WidgetView : Border
{
    private readonly IWidgetHost _host;
    public WidgetDescriptor Descriptor { get; }
    public string Key => Descriptor.Key;

    private Metric? _metric;
    private TextBlock? _dynamicText;   // refreshed each second (clock/date/app)
    private TextBlock? _modeText;
    private Border? _removeBadge;

    private static readonly Brush IdleBg = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HoverBg = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    public WidgetView(IWidgetHost host, WidgetDescriptor descriptor)
    {
        _host = host;
        Descriptor = descriptor;

        double radius = host.Settings.WidgetCornerRadius;
        CornerRadius = new CornerRadius(radius);
        Padding = new Thickness(9, 1, 9, 1);
        Margin = new Thickness(0);
        Background = IdleBg;
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Arrow;

        Child = BuildContent();

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        MouseLeftButtonUp += OnMouseUp;
    }

    private Brush Foreground { get; set; } = Brushes.White;

    private UIElement BuildContent()
    {
        Foreground = ParseBrush(_host.Settings.ForegroundColor, Brushes.White);

        return Descriptor.Kind switch
        {
            WidgetKind.Gauge => BuildGauge(),
            WidgetKind.Clock => BuildText(out _dynamicText, semibold: true),
            WidgetKind.Date => BuildText(out _dynamicText, semibold: false, opacity: 0.9),
            WidgetKind.ActiveApp => BuildText(out _dynamicText, semibold: true),
            WidgetKind.Mode => BuildMode(),
            WidgetKind.Settings => BuildGlyph(""),
            _ => BuildText(out _dynamicText, false)
        };
    }

    // ---- builders ----

    private UIElement BuildGauge()
    {
        _metric = _host.GetMetric(Key);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        double ring = Math.Max(16, _host.Settings.BarHeight * 0.6);
        var gauge = new RingGauge { Width = ring, Height = ring, VerticalAlignment = VerticalAlignment.Center };
        gauge.SetBinding(RingGauge.PercentProperty, new Binding(nameof(Metric.Percent)) { Source = _metric });
        row.Children.Add(gauge);

        var labels = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = Descriptor.Name.ToUpperInvariantSafe(),
            FontSize = 8.5,
            Opacity = 0.55,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, -1)
        });
        var value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Foreground };
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(Metric.Text)) { Source = _metric });
        labels.Children.Add(value);
        row.Children.Add(labels);

        return WrapWithBadge(row);
    }

    private UIElement BuildText(out TextBlock tb, bool semibold, double opacity = 1.0)
    {
        tb = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Foreground,
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center
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

    private UIElement BuildGlyph(string glyph)
    {
        var tb = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center
        };
        return WrapWithBadge(tb);
    }

    /// <summary>Overlays a remove (×) badge that only shows in customize mode.</summary>
    private UIElement WrapWithBadge(UIElement content)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(content);

        _removeBadge = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -14, 0),
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 8,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _removeBadge.MouseLeftButtonDown += (s, e) => { e.Handled = true; _host.RemoveWidget(this); };
        grid.Children.Add(_removeBadge);
        return grid;
    }

    // ---- refresh ----

    public void RefreshDynamic()
    {
        if (_dynamicText != null) Refresh(_dynamicText);
        if (_modeText != null) UpdateModeText();
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
        if (_removeBadge != null)
            _removeBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Cursor = on ? Cursors.SizeAll : Cursors.Arrow;
        BorderThickness = on ? new Thickness(1) : new Thickness(0);
        BorderBrush = on ? Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) : null;
    }

    // ---- interaction ----

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Background = HoverBg;
        if (Descriptor.Kind == WidgetKind.Gauge && _metric != null && !_host.Customizing)
            _host.ShowGraph(this, _metric);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        Background = IdleBg;
        if (Descriptor.Kind == WidgetKind.Gauge)
            _host.HideGraph(this);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_host.Customizing)
        {
            _host.BeginWidgetDrag(this, e);
            e.Handled = true;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_host.Customizing) return;
        switch (Descriptor.Kind)
        {
            case WidgetKind.Mode: _host.OnModeClicked(); UpdateModeText(); break;
            case WidgetKind.Settings: _host.OnSettingsClicked(); break;
        }
    }

    // ---- helpers ----

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Brush ParseBrush(string hex, Brush fallback)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch { return fallback; }
    }
}

internal static class StringExt
{
    public static string ToUpperInvariantSafe(this string s) => s?.ToUpperInvariant() ?? "";
}
