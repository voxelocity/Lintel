using System.Drawing;
using System.Threading;
using System.Windows;
using Lintel.Models;
using Lintel.Services;
using Application = System.Windows.Application;
using WinForms = System.Windows.Forms;

namespace Lintel;

public partial class App : Application
{
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _bar;
    private AppSettings? _settings;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one Lintel at a time.
        _singleInstance = new Mutex(initiallyOwned: true, "Lintel.SingleInstance.A7F3", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();

        // Keep the registry Run entry in sync with the saved preference.
        StartupManager.Apply(_settings.LaunchAtStartup);

        _bar = new MainWindow(_settings);
        _bar.Show();

        BuildTray();
    }

    private void BuildTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Visible = true,
            Text = "Lintel"
        };

        var menu = new WinForms.ContextMenuStrip();

        menu.Items.Add("Settings…", null, (_, _) => _bar?.OpenSettingsFromTray());
        menu.Items.Add("Customize Widgets", null, (_, _) => _bar?.ToggleCustomizeFromTray());

        var visibility = new WinForms.ToolStripMenuItem("Visibility");
        AddModeItem(visibility, "Always On", VisibilityMode.AlwaysOn);
        AddModeItem(visibility, "Auto-Hide", VisibilityMode.AutoHide);
        AddModeItem(visibility, "Dynamic", VisibilityMode.Dynamic);
        visibility.DropDownOpening += (_, _) =>
        {
            foreach (WinForms.ToolStripMenuItem item in visibility.DropDownItems)
                item.Checked = (VisibilityMode)item.Tag! == _settings!.Mode;
        };
        menu.Items.Add(visibility);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit Lintel", null, (_, _) => Shutdown());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _bar?.OpenSettingsFromTray();
    }

    private void AddModeItem(WinForms.ToolStripMenuItem parent, string label, VisibilityMode mode)
    {
        var item = new WinForms.ToolStripMenuItem(label) { Tag = mode };
        item.Click += (_, _) => _bar?.ChangeMode(mode);
        parent.DropDownItems.Add(item);
    }

    /// <summary>Draw a small thematic icon (a lit top bar) so we don't ship an .ico asset.</summary>
    private static Icon BuildTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(220, 28, 28, 30));
            g.FillRectangle(body, 2, 4, 28, 24);
            using var bar = new SolidBrush(Color.FromArgb(255, 10, 132, 255));
            g.FillRectangle(bar, 2, 4, 28, 7); // the "top bar" strip
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
