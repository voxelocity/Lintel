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

        var menu = new WinForms.ContextMenuStrip
        {
            RenderMode = WinForms.ToolStripRenderMode.Professional,
            BackColor = Color.FromArgb(31, 31, 35),
            ForeColor = Color.White,
            ShowImageMargin = false
        };
        menu.Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = true };

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

        ThemeMenuItems(menu.Items);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _bar?.OpenSettingsFromTray();
    }

    private static void ThemeMenuItems(WinForms.ToolStripItemCollection items)
    {
        foreach (WinForms.ToolStripItem item in items)
        {
            item.ForeColor = Color.White;
            if (item is WinForms.ToolStripMenuItem mi && mi.HasDropDownItems)
            {
                mi.DropDown.BackColor = Color.FromArgb(31, 31, 35);
                ThemeMenuItems(mi.DropDownItems);
            }
        }
    }

    /// <summary>Dark palette so the tray menu matches the bar.</summary>
    private sealed class DarkMenuColors : WinForms.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(31, 31, 35);
        private static readonly Color Hover = Color.FromArgb(54, 54, 60);
        private static readonly Color Accent = Color.FromArgb(10, 132, 255);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Color.FromArgb(60, 60, 66);
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Bg;
        public override Color MenuItemPressedGradientEnd => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Color.FromArgb(60, 60, 66);
        public override Color SeparatorLight => Color.FromArgb(60, 60, 66);
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
    }

    private void AddModeItem(WinForms.ToolStripMenuItem parent, string label, VisibilityMode mode)
    {
        var item = new WinForms.ToolStripMenuItem(label) { Tag = mode };
        item.Click += (_, _) => _bar?.ChangeMode(mode);
        parent.DropDownItems.Add(item);
    }

    /// <summary>Tray icon: the Lintel logo (embedded .ico), with a drawn fallback.</summary>
    private static Icon BuildTrayIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("Lintel.lintel.ico");
            if (s != null)
                return new Icon(s, new System.Drawing.Size(32, 32));   // closest frame to 32px
        }
        catch { /* fall back to the drawn mark */ }

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
