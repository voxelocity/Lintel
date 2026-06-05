using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lintel.Models;

namespace Lintel;

/// <summary>Themed settings card, hosted in an in-bar popup (no separate OS window).</summary>
public partial class SettingsPanel : UserControl
{
    private readonly AppSettings _settings;
    private int _selMode;

    public event Action? SettingsApplied;
    public event Action? CloseRequested;

    public SettingsPanel(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadFromSettings();
    }

    private static readonly string[] ModeHints =
    {
        "Always visible; reserves desktop space so windows sit below it.",
        "Hidden until you push the cursor to the very top of the screen.",
        "Floats on top, hides under fullscreen or overlapping windows."
    };

    private void LoadFromSettings()
    {
        _selMode = (int)_settings.Mode;
        UpdateModeButtons();

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
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        _selMode = int.Parse((string)((Button)sender).Tag);
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        var on = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        var off = Brushes.Transparent;
        ModeAlways.Background = _selMode == 0 ? on : off;
        ModeAuto.Background = _selMode == 1 ? on : off;
        ModeDynamic.Background = _selMode == 2 ? on : off;
        ModeAlways.Foreground = ModeAuto.Foreground = ModeDynamic.Foreground =
            new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD5));
        (_selMode switch { 0 => ModeAlways, 1 => ModeAuto, _ => ModeDynamic }).Foreground = Brushes.White;
        ModeHint.Text = ModeHints[Math.Clamp(_selMode, 0, 2)];
    }

    private void WriteToSettings()
    {
        _settings.Mode = (VisibilityMode)_selMode;
        _settings.RevealHoldMs = ParseInt(RevealHoldBox.Text, _settings.RevealHoldMs);
        _settings.HideDelayMs = ParseInt(HideDelayBox.Text, _settings.HideDelayMs);
        _settings.TriggerZonePx = ParseInt(TriggerZoneBox.Text, _settings.TriggerZonePx);
        _settings.DynamicHideDelayMs = ParseInt(DynamicHideBox.Text, _settings.DynamicHideDelayMs);

        _settings.BarHeight = ParseDouble(BarHeightBox.Text, _settings.BarHeight);
        _settings.BackgroundColor = NonEmpty(BgColorBox.Text, _settings.BackgroundColor);
        _settings.ForegroundColor = NonEmpty(FgColorBox.Text, _settings.ForegroundColor);
        _settings.AccentColor = NonEmpty(AccentColorBox.Text, _settings.AccentColor);
        _settings.AnimationMs = ParseInt(AnimationBox.Text, _settings.AnimationMs);

        _settings.Use24HourClock = Clock24Chk.IsChecked == true;
        _settings.MonitorIndex = ParseInt(MonitorBox.Text, _settings.MonitorIndex);
        _settings.LaunchAtStartup = StartupChk.IsChecked == true;

        _settings.Clamped();
        LoadFromSettings();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        WriteToSettings();
        _settings.Save();
        SettingsApplied?.Invoke();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        WriteToSettings();
        _settings.Save();
        SettingsApplied?.Invoke();
        CloseRequested?.Invoke();
    }

    private void Close_Click(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

    private static int ParseInt(string t, int f) =>
        int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : f;
    private static double ParseDouble(string t, double f) =>
        double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : f;
    private static string NonEmpty(string t, string f) => string.IsNullOrWhiteSpace(t) ? f : t.Trim();
}
