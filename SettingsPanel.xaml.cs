using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lintel.Models;

namespace Lintel;

/// <summary>Themed settings card hosted in an in-bar popup. Opens as a compact Quick view
/// and dynamically scales up into a landscape Advanced view.</summary>
public partial class SettingsPanel : UserControl
{
    private const double QuickWidth = 384;
    private const double AdvancedWidth = 760;

    private readonly AppSettings _settings;
    private int _selMode;
    private int _selTheme;
    private int _selPos;
    private bool _themeTouched;

    public double CurrentWidth { get; private set; } = QuickWidth;

    public event Action? SettingsApplied;
    public event Action? CloseRequested;
    public event Action<double>? WidthChanged;
    public event Action? ImportThemeRequested;
    public event Action? ImportWidgetRequested;
    public event Action? OpenFolderRequested;

    public SettingsPanel(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Root.Width = QuickWidth;
        LoadFromSettings();
    }

    private static readonly string[] ModeHints =
    {
        "Always visible; reserves desktop space so windows sit below it.",
        "Hidden until you push the cursor to the very top of the screen.",
        "Floats on top, hides under fullscreen or overlapping windows."
    };

    // ---- load / write ----

    private void LoadFromSettings()
    {
        _selMode = (int)_settings.Mode;
        _themeTouched = false;
        // Highlight the matching built-in segment; a custom theme highlights none (-1).
        _selTheme = (Widgets.Themes.NameOf(_settings)) switch
        {
            "Power" or "Resin" => 1,
            "Islands" => 2,
            "Mond" => 3,
            "Squircles" => 0,
            _ => -1
        };
        _selPos = (int)_settings.BarPosition;
        UpdateModeButtons();
        UpdateThemeButtons();
        UpdatePosButtons();

        // quick
        QBarHeight.Text = _settings.BarHeight.ToString(CultureInfo.InvariantCulture);
        QHover.IsChecked = _settings.OpenOnHover;
        QClock24.IsChecked = _settings.Use24HourClock;
        QStartup.IsChecked = _settings.LaunchAtStartup;

        // advanced
        RevealHoldBox.Text = _settings.RevealHoldMs.ToString();
        HideDelayBox.Text = _settings.HideDelayMs.ToString();
        TriggerZoneBox.Text = _settings.TriggerZonePx.ToString();
        DynamicHideBox.Text = _settings.DynamicHideDelayMs.ToString();
        BarHeightBox.Text = _settings.BarHeight.ToString(CultureInfo.InvariantCulture);
        BgColorBox.Text = _settings.BackgroundColor;
        FgColorBox.Text = _settings.ForegroundColor;
        AccentColorBox.Text = _settings.AccentColor;
        AnimationBox.Text = _settings.AnimationMs.ToString();
        Clock24Chk.IsChecked = _settings.Use24HourClock;
        MonitorBox.Text = _settings.MonitorIndex.ToString();
        StartupChk.IsChecked = _settings.LaunchAtStartup;
        ClaudeLimitBox.Text = _settings.ClaudeTokenLimit.ToString();
        CmdWidgetsChk.IsChecked = _settings.EnableCommandWidgets;
        LiveBlurChk.IsChecked = _settings.LiveBlur;
        PotatoChk.IsChecked = _settings.PotatoMode;
        LiteChk.IsChecked = _settings.LiteMode;
        WeatherLocBox.Text = _settings.WeatherLocation;
        StockSymBox.Text = _settings.StockSymbols;
    }

    private void ImportTheme_Click(object sender, RoutedEventArgs e) => ImportThemeRequested?.Invoke();
    private void ImportWidget_Click(object sender, RoutedEventArgs e) => ImportWidgetRequested?.Invoke();
    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke();

    private static readonly LintelTheme[] ThemeOrder =
        { LintelTheme.Squircles, LintelTheme.Power, LintelTheme.Islands, LintelTheme.Mond };

    private void Pos_Click(object sender, RoutedEventArgs e) { _selPos = int.Parse((string)((Button)sender).Tag); UpdatePosButtons(); }

    private void UpdatePosButtons()
    {
        var on = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        var dim = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD5));
        Button[] b = { PosTop, PosBottom, PosLeft, PosRight };
        for (int i = 0; i < b.Length; i++) { b[i].Background = _selPos == i ? on : Brushes.Transparent; b[i].Foreground = _selPos == i ? Brushes.White : dim; }
    }

    private void WriteQuick()
    {
        _settings.Mode = (VisibilityMode)_selMode;
        _settings.BarPosition = (BarEdge)_selPos;
        _settings.BarHeight = ParseD(QBarHeight.Text, _settings.BarHeight);
        _settings.OpenOnHover = QHover.IsChecked == true;
        _settings.Use24HourClock = QClock24.IsChecked == true;
        _settings.LaunchAtStartup = QStartup.IsChecked == true;
    }

    private void WriteAdvanced()
    {
        _settings.Mode = (VisibilityMode)_selMode;
        _settings.BarPosition = (BarEdge)_selPos;
        // Only override the theme if the user actually picked a built-in segment here —
        // otherwise leave a custom (imported) theme selection intact.
        if (_themeTouched && _selTheme >= 0)
        {
            _settings.Theme = ThemeOrder[Math.Clamp(_selTheme, 0, ThemeOrder.Length - 1)];
            _settings.ThemeName = _settings.Theme.ToString();
        }
        _settings.ClaudeTokenLimit = Math.Max(0, ParseL(ClaudeLimitBox.Text, _settings.ClaudeTokenLimit));
        _settings.EnableCommandWidgets = CmdWidgetsChk.IsChecked == true;
        _settings.LiveBlur = LiveBlurChk.IsChecked == true;
        _settings.PotatoMode = PotatoChk.IsChecked == true;
        _settings.LiteMode = LiteChk.IsChecked == true;
        _settings.WeatherLocation = WeatherLocBox.Text.Trim();
        _settings.StockSymbols = string.IsNullOrWhiteSpace(StockSymBox.Text) ? _settings.StockSymbols : StockSymBox.Text.Trim();
        _settings.RevealHoldMs = ParseI(RevealHoldBox.Text, _settings.RevealHoldMs);
        _settings.HideDelayMs = ParseI(HideDelayBox.Text, _settings.HideDelayMs);
        _settings.TriggerZonePx = ParseI(TriggerZoneBox.Text, _settings.TriggerZonePx);
        _settings.DynamicHideDelayMs = ParseI(DynamicHideBox.Text, _settings.DynamicHideDelayMs);
        _settings.BarHeight = ParseD(BarHeightBox.Text, _settings.BarHeight);
        _settings.BackgroundColor = NonEmpty(BgColorBox.Text, _settings.BackgroundColor);
        _settings.ForegroundColor = NonEmpty(FgColorBox.Text, _settings.ForegroundColor);
        _settings.AccentColor = NonEmpty(AccentColorBox.Text, _settings.AccentColor);
        _settings.AnimationMs = ParseI(AnimationBox.Text, _settings.AnimationMs);
        _settings.Use24HourClock = Clock24Chk.IsChecked == true;
        _settings.MonitorIndex = ParseI(MonitorBox.Text, _settings.MonitorIndex);
        _settings.LaunchAtStartup = StartupChk.IsChecked == true;
    }

    private bool AdvancedVisible => AdvancedView.Visibility == Visibility.Visible;

    private void WriteActive()
    {
        if (AdvancedVisible) WriteAdvanced(); else WriteQuick();
        _settings.Clamped();
        LoadFromSettings();
    }

    // ---- mode segmented ----

    private void QMode_Click(object sender, RoutedEventArgs e) => SetMode(sender);
    private void Mode_Click(object sender, RoutedEventArgs e) => SetMode(sender);

    private void SetMode(object sender)
    {
        _selMode = int.Parse((string)((Button)sender).Tag);
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        var on = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        var dim = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD5));
        Button[] quick = { QModeAlways, QModeAuto, QModeDynamic };
        Button[] adv = { ModeAlways, ModeAuto, ModeDynamic };
        for (int i = 0; i < 3; i++)
        {
            bool sel = _selMode == i;
            quick[i].Background = sel ? on : Brushes.Transparent;
            adv[i].Background = sel ? on : Brushes.Transparent;
            quick[i].Foreground = sel ? Brushes.White : dim;
            adv[i].Foreground = sel ? Brushes.White : dim;
        }
        ModeHint.Text = ModeHints[Math.Clamp(_selMode, 0, 2)];
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _selTheme = int.Parse((string)((Button)sender).Tag);
        _themeTouched = true;
        UpdateThemeButtons();
    }

    private void UpdateThemeButtons()
    {
        var on = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        var dim = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD5));
        Button[] btns = { ThemeSquircles, ThemePower, ThemeIslands, ThemeMond };
        for (int i = 0; i < btns.Length; i++)
        {
            btns[i].Background = _selTheme == i ? on : Brushes.Transparent;
            btns[i].Foreground = _selTheme == i ? Brushes.White : dim;
        }
    }

    // ---- view switching ----

    private void ShowAdvanced_Click(object sender, RoutedEventArgs e) => ExpandToAdvanced();

    public void ExpandToAdvanced()
    {
        WriteQuick();
        LoadFromSettings();
        QuickView.Visibility = Visibility.Collapsed;
        AdvancedView.Visibility = Visibility.Visible;
        SetWidth(AdvancedWidth);
        AnimateExpand(0.94);
    }

    private void ShowQuick_Click(object sender, RoutedEventArgs e)
    {
        WriteAdvanced();
        LoadFromSettings();
        AdvancedView.Visibility = Visibility.Collapsed;
        QuickView.Visibility = Visibility.Visible;
        SetWidth(QuickWidth);
        AnimateExpand(1.04);
    }

    private void SetWidth(double w)
    {
        CurrentWidth = w;
        Root.Width = w;
        WidthChanged?.Invoke(w);
    }

    private void AnimateExpand(double from)
    {
        var a = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, a.Clone());
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(150)));
    }

    // ---- buttons ----

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        WriteActive();
        _settings.Save();
        SettingsApplied?.Invoke();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        WriteActive();
        _settings.Save();
        SettingsApplied?.Invoke();
        CloseRequested?.Invoke();
    }

    private void Close_Click(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

    private static int ParseI(string t, int f) => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : f;
    private static double ParseD(string t, double f) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : f;
    private static long ParseL(string t, long f) => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : f;
    private static string NonEmpty(string t, string f) => string.IsNullOrWhiteSpace(t) ? f : t.Trim();
}
