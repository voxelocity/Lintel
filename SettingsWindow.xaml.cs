using System.Globalization;
using System.Windows;
using Lintel.Models;

namespace Lintel;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>Raised after the user applies changes so the bar can refresh live.</summary>
    public event Action? SettingsApplied;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        ModeBox.SelectedIndex = (int)_settings.Mode;

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

    private void WriteToSettings()
    {
        _settings.Mode = (VisibilityMode)Math.Max(0, ModeBox.SelectedIndex);

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
        // Reflect any clamping back into the boxes.
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
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string NonEmpty(string text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}
