using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("eGPU Tray")]
[assembly: AssemblyProduct("eGPU Tray")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Hardening.Apply();
        if (args.Length > 0)
        {
            string a = args[0].ToLowerInvariant();
            if (a == "--uninstall")   // older Apps entries pointed here; hand over to the uninstaller
            {
                if (File.Exists(AppInfo.UninstallerExe))
                    Process.Start(AppInfo.UninstallerExe, string.Join(" ", args.Skip(1)));
                return 0;
            }
            // Elevated retry of a disconnect. Only the eGPU itself may be ejected this way, whatever
            // device ID the command line names.
            if (a == "--eject" && args.Length > 1)
            {
                string gpu = Cfg.FindExternalGpu();
                if (gpu == null || !gpu.Equals(args[1], StringComparison.OrdinalIgnoreCase)) return 2;
                return Cfg.Eject(gpu) == null ? 0 : 1;
            }
        }

        bool created;
        var mutex = new Mutex(true, AppInfo.MutexName, out created);
        if (!created)
        {
            try { using (var ev = EventWaitHandle.OpenExisting(AppInfo.ShowEvent)) ev.Set(); } catch { }
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Crash: " + e.ExceptionObject);
        Application.Run(new TrayApp());
        GC.KeepAlive(mutex);
        return 0;
    }
}

static class Log
{
    static readonly object gate = new object();
    public static string FilePath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "egpu-tray.log"); }
    }
    public static void Write(string msg)
    {
        try
        {
            lock (gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
        }
        catch { }
    }
}

// ---------------------------------------------------------------- UI

class Theme
{
    public bool Light;
    public Color Bg, Fg, Fg2, Line, BarBg, BtnBg, BtnHover, BtnBorder, Good, Bad, Warn;

    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool TaskbarIsLight() { return ReadDword("SystemUsesLightTheme", 0) != 0; }

    public static Theme Current()
    {
        bool light = ReadDword("AppsUseLightTheme", 1) != 0;
        var t = new Theme { Light = light };
        if (light)
        {
            t.Bg = Color.FromArgb(249, 249, 249); t.Fg = Color.FromArgb(26, 26, 26); t.Fg2 = Color.FromArgb(96, 96, 96);
            t.Line = Color.FromArgb(222, 222, 222); t.BarBg = Color.FromArgb(226, 226, 226);
            t.BtnBg = Color.White; t.BtnHover = Color.FromArgb(240, 240, 240); t.BtnBorder = Color.FromArgb(208, 208, 208);
            t.Good = Color.FromArgb(15, 123, 15); t.Bad = Color.FromArgb(196, 43, 28); t.Warn = Color.FromArgb(157, 93, 0);
        }
        else
        {
            t.Bg = Color.FromArgb(32, 32, 32); t.Fg = Color.White; t.Fg2 = Color.FromArgb(166, 166, 166);
            t.Line = Color.FromArgb(62, 62, 62); t.BarBg = Color.FromArgb(62, 62, 62);
            t.BtnBg = Color.FromArgb(45, 45, 45); t.BtnHover = Color.FromArgb(55, 55, 55); t.BtnBorder = Color.FromArgb(72, 72, 72);
            t.Good = Color.FromArgb(108, 203, 95); t.Bad = Color.FromArgb(255, 107, 107); t.Warn = Color.FromArgb(242, 184, 75);
        }
        return t;
    }

    static int ReadDword(string name, int fallback)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object v = k == null ? null : k.GetValue(name);
                if (v is int) return (int)v;
            }
        }
        catch { }
        return fallback;
    }
}

// Hidden top-level window: receives device-change broadcasts so plug/unplug is noticed instantly.
class MsgWindow : NativeWindow
{
    public event Action DeviceChanged;
    public MsgWindow() { CreateHandle(new CreateParams { Caption = "eGPUTrayMsg" }); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0219)   // WM_DEVICECHANGE
        {
            var h = DeviceChanged;
            if (h != null) h();
        }
        base.WndProc(ref m);
    }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly StatusPopup popup = new StatusPopup();
    readonly GpuMonitor mon = new GpuMonitor();
    readonly MsgWindow msgs = new MsgWindow();
    readonly System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer promote = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer connected = new System.Windows.Forms.Timer();
    readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>();
    readonly EventWaitHandle showEv = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ShowEvent);
    readonly EventWaitHandle exitEv = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ExitEvent);
    GpuSnapshot snap = new GpuSnapshot();
    bool blinkPhase, lightTaskbar, exiting, wasReady;
    string iconKey;
    int promoteTries;
    Font boldFont;
    ConnectActionForm actionForm;

    public TrayApp()
    {
        lightTaskbar = Theme.TaskbarIsLight();
        IntPtr force = popup.Handle;   // so BeginInvoke works before it is first shown
        popup.DisconnectClicked += delegate { RequestDisconnect(); };
        popup.VisibleChanged += delegate { mon.Fast = popup.Visible; mon.Wake(); };

        menu.Opening += BuildMenu;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        RefreshIcon();
        tray.Visible = true;

        blink.Interval = 600;
        blink.Tick += delegate { blinkPhase = !blinkPhase; RefreshIcon(); };

        // Runs the on-connect program only once the eGPU has stayed ready for a moment, so a card
        // that drops straight back out doesn't trigger it, and other tools see the driver as up.
        connected.Interval = 3000;
        connected.Tick += delegate { connected.Stop(); ConnectAction.Run(); };

        msgs.DeviceChanged += () => mon.Wake();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        mon.Updated += s =>
        {
            try { popup.BeginInvoke((Action)(() => OnSnapshot(s))); } catch { }
        };
        mon.Start();

        var waiter = new Thread(() =>
        {
            while (true)
            {
                int i = WaitHandle.WaitAny(new WaitHandle[] { showEv, exitEv });
                try { popup.BeginInvoke((Action)(() => { if (i == 0) ShowPopup(); else ExitApp(); })); } catch { }
                if (i == 1) break;
            }
        });
        waiter.IsBackground = true;
        waiter.Start();

        // Keep the icon on the taskbar instead of the hidden overflow (Windows 11).
        promote.Interval = 4000;
        promote.Tick += delegate
        {
            if (PromoteTrayIcon() || ++promoteTries > 15) promote.Stop();
        };
        promote.Start();

        Log.Write("eGPU Tray " + AppInfo.Version + " started");
    }

    void OnSnapshot(GpuSnapshot s)
    {
        if (exiting) return;
        snap = s;
        bool animate = s.State == GpuState.Starting || s.State == GpuState.Ejecting;
        if (animate != blink.Enabled) { blink.Enabled = animate; blinkPhase = false; }
        bool ready = s.State == GpuState.Ready;
        if (ready && !wasReady) connected.Start();
        else if (!ready) connected.Stop();
        wasReady = ready;
        RefreshIcon();
        tray.Text = Clip(TooltipText(s), 63);
        if (popup.Visible) popup.SetSnapshot(s);
    }

    static string TooltipText(GpuSnapshot s)
    {
        if (s.State == GpuState.Ready && s.HasMetrics)
        {
            var parts = new List<string> { s.Util + "%" };
            if (s.Temp > 0) parts.Add(s.Temp + "\u00B0C");
            if (s.MemTotal > 0) parts.Add(GpuSnapshot.Gb(s.MemUsed) + "/" + GpuSnapshot.Gb(s.MemTotal) + " GB");
            return s.ShortName + ": " + string.Join(" \u00B7 ", parts);
        }
        return "eGPU: " + s.StatusText.ToLowerInvariant();
    }

    static string Clip(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "\u2026"; }

    void RefreshIcon()
    {
        ChipGlyph g = snap.Glyph(blinkPhase);
        int size = SystemInformation.SmallIconSize.Width;
        string key = g + "|" + lightTaskbar + "|" + size;
        if (key == iconKey) return;
        Icon icon;
        if (!icons.TryGetValue(key, out icon))
        {
            using (var bmp = ChipIcon.Render(size, g, lightTaskbar ? Color.Black : Color.White))
                icon = Icon.FromHandle(bmp.GetHicon());
            icons[key] = icon;
        }
        tray.Icon = icon;
        iconKey = key;
    }

    void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        bool light = Theme.TaskbarIsLight();
        if (light != lightTaskbar) { lightTaskbar = light; RefreshIcon(); }
        popup.ApplyTheme();
    }

    void OnDisplayChanged(object sender, EventArgs e) { iconKey = null; RefreshIcon(); }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock && ConnectAction.Pending && wasReady)
            ConnectAction.Run();
    }

    void TogglePopup()
    {
        if (popup.Visible) { popup.HideNow(); return; }
        // A click on the icon first deactivates (and hides) the popup; don't reopen it straight away.
        if ((DateTime.Now - popup.HiddenAt).TotalMilliseconds < 300) return;
        ShowPopup();
    }

    void ShowPopup()
    {
        popup.SetSnapshot(snap);
        popup.ShowNearCursor();
    }

    void BuildMenu(object sender, CancelEventArgs e)
    {
        // Rebuilt on every open; dispose the old items so their handles don't pile up.
        while (menu.Items.Count > 0) menu.Items[0].Dispose();
        if (boldFont == null) boldFont = new Font(menu.Font, FontStyle.Bold);
        var head = new ToolStripLabel(snap.ShortName + " \u2014 " + snap.StatusText);
        head.Font = boldFont;
        menu.Items.Add(head);
        if (snap.State == GpuState.Ready && snap.HasMetrics)
        {
            menu.Items.Add(new ToolStripLabel(string.Format("Usage {0}%   \u00B7   VRAM {1} / {2} GB",
                snap.Util, GpuSnapshot.Gb(snap.MemUsed), GpuSnapshot.Gb(snap.MemTotal))));
            var extra = new List<string>();
            if (snap.Temp > 0) extra.Add("Temp " + snap.Temp + "\u00B0C");
            if (snap.PowerW >= 0) extra.Add("Power " + snap.PowerW.ToString("0") + " W");
            if (snap.FanRpm > 0) extra.Add("Fan " + snap.FanRpm + " rpm");
            if (extra.Count > 0) menu.Items.Add(new ToolStripLabel(string.Join("   \u00B7   ", extra)));
        }
        menu.Items.Add(new ToolStripSeparator());

        var show = new ToolStripMenuItem("Show status", null, delegate { ShowPopup(); });
        show.Font = boldFont;
        menu.Items.Add(show);
        var disc = new ToolStripMenuItem("Disconnect eGPU", null, delegate { RequestDisconnect(); });
        disc.Enabled = snap.Present && snap.State != GpuState.Ejecting;
        menu.Items.Add(disc);
        menu.Items.Add(new ToolStripSeparator());

        var auto = new ToolStripMenuItem("Start with Windows");
        auto.Checked = Installer.GetAutostart();
        auto.Click += delegate { Installer.SetAutostart(!auto.Checked, Application.ExecutablePath); };
        menu.Items.Add(auto);
        var onConnect = new ToolStripMenuItem("Run when connected…", null, delegate { ShowConnectAction(); });
        onConnect.Checked = ConnectAction.IsSet;
        menu.Items.Add(onConnect);
        menu.Items.Add(new ToolStripMenuItem("Open log", null, delegate
        {
            if (File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
        }));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitApp(); }));
        e.Cancel = false;
    }

    void ShowConnectAction()
    {
        if (actionForm != null) { actionForm.Activate(); return; }
        actionForm = new ConnectActionForm();
        actionForm.FormClosed += delegate { actionForm.Dispose(); actionForm = null; };
        actionForm.Show();
        actionForm.Activate();
    }

    void RequestDisconnect()
    {
        if (!snap.Present || snap.State == GpuState.Ejecting) return;

        // No dialogs: the result shows up only as an icon change (and in the panel if it is open).
        snap.State = GpuState.Ejecting;
        OnSnapshot(snap);
        mon.Eject(err => mon.Wake());
    }

    // Windows 11 keeps per-icon settings under NotifyIconSettings; IsPromoted=1 pins it to the taskbar.
    // Only set when Windows hasn't recorded a choice yet, so a user who later hides it stays in control.
    static bool PromoteTrayIcon()
    {
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
            {
                if (root == null) return false;
                bool found = false;
                foreach (string name in root.GetSubKeyNames())
                    using (var k = root.OpenSubKey(name, true))
                    {
                        string path = k == null ? null : k.GetValue("ExecutablePath") as string;
                        if (path == null || !path.EndsWith("\\" + AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (k.GetValue("IsPromoted") == null) k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        found = true;
                    }
                return found;
            }
        }
        catch { return false; }
    }

    void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        Log.Write("eGPU Tray exiting");
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        mon.Stop();
        connected.Stop();
        if (actionForm != null) actionForm.Close();
        tray.Visible = false;
        tray.Dispose();
        msgs.DestroyHandle();
        ExitThread();
    }
}

// Flyout shown when the tray icon is clicked.
class StatusPopup : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    const int W = 300, P = 16;

    class Row { public string Label, Value; public float Bar = -1; }

    public event EventHandler DisconnectClicked;
    public DateTime HiddenAt = DateTime.MinValue;

    readonly Button btn = new Button();
    readonly Font fTitle = new Font("Segoe UI Semibold", 10.5f);
    readonly Font fBody = new Font("Segoe UI", 9f);
    readonly Font fValue = new Font("Segoe UI Semibold", 9f);
    readonly float sc;
    GpuSnapshot snap = new GpuSnapshot();
    Theme th;
    int anchorBottom = -1;
    Bitmap headIcon;
    string headKey;

    public StatusPopup()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) sc = g.DpiX / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = AppInfo.Name;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        btn.FlatStyle = FlatStyle.Flat;
        btn.Font = fBody;
        btn.Text = "Disconnect eGPU";
        btn.Cursor = Cursors.Hand;
        btn.TabStop = false;
        btn.Click += delegate { var h = DisconnectClicked; if (h != null) h(this, EventArgs.Empty); };
        Controls.Add(btn);

        ApplyTheme();
        Relayout();
    }

    int D(float v) { return (int)Math.Round(v * sc); }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwm();
    }

    void ApplyDwm()
    {
        if (th == null || !IsHandleCreated) return;
        try
        {
            int round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = th.Line.R | (th.Line.G << 8) | (th.Line.B << 16);
            DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }
        catch { }
    }

    public void ApplyTheme()
    {
        th = Theme.Current();
        BackColor = th.Bg;
        btn.BackColor = th.BtnBg;
        btn.ForeColor = th.Fg;
        btn.FlatAppearance.BorderColor = th.BtnBorder;
        btn.FlatAppearance.MouseOverBackColor = th.BtnHover;
        btn.FlatAppearance.MouseDownBackColor = th.BtnBorder;
        ApplyDwm();
        Invalidate();
    }

    public void SetSnapshot(GpuSnapshot s)
    {
        snap = s;
        Relayout();
        Invalidate();
    }

    List<Row> Rows()
    {
        var rows = new List<Row>();
        if (snap.State != GpuState.Ready || !snap.HasMetrics) return rows;
        rows.Add(new Row { Label = "GPU usage", Value = snap.Util + "%", Bar = snap.Util / 100f });
        if (snap.MemTotal > 0)
            rows.Add(new Row
            {
                Label = "VRAM",
                Value = GpuSnapshot.Gb(snap.MemUsed) + " / " + GpuSnapshot.Gb(snap.MemTotal) + " GB",
                Bar = (float)snap.MemUsed / snap.MemTotal
            });
        if (snap.Temp > 0) rows.Add(new Row { Label = "Temperature", Value = snap.Temp + " \u00B0C" });
        if (snap.PowerW >= 0) rows.Add(new Row { Label = "Power", Value = snap.PowerW.ToString("0") + " W" });
        if (snap.FanRpm > 0) rows.Add(new Row { Label = "Fan", Value = snap.FanRpm + " rpm" });
        if (snap.PcieGen > 0) rows.Add(new Row { Label = "PCIe link", Value = "Gen " + snap.PcieGen + " \u00D7" + snap.PcieWidth });
        return rows;
    }

    int TextHeight(string text)
    {
        return TextRenderer.MeasureText(text, fBody, new Size(D(W - 2 * P), int.MaxValue),
            TextFormatFlags.WordBreak).Height;
    }

    // Lays out the content and returns the y where the button goes.
    int ContentBottom()
    {
        int y = D(78);
        var rows = Rows();
        if (rows.Count > 0)
            foreach (var r in rows) y += r.Bar >= 0 ? D(38) : D(26);
        else
            y += TextHeight(snap.BodyText ?? "") + D(6);
        if (snap.Message != null && rows.Count > 0) y += TextHeight(snap.Message) + D(8);
        if (AppsText != null && rows.Count > 0) y += TextHeight(AppsText) + D(8);
        return y;
    }

    string AppsText
    {
        get { return snap.Apps != null && snap.Apps.Count > 0 ? "In use by " + string.Join(", ", snap.Apps) : null; }
    }

    void Relayout()
    {
        int y = ContentBottom() + D(4);
        bool showBtn = snap.Present || snap.State == GpuState.Ejecting;
        btn.Visible = showBtn;
        btn.Bounds = new Rectangle(D(P), y, D(W - 2 * P), D(32));
        btn.Enabled = snap.Present && snap.State != GpuState.Ejecting;
        btn.Text = snap.State == GpuState.Ejecting ? "Disconnecting\u2026" : "Disconnect eGPU";
        var size = new Size(D(W), showBtn ? y + D(32) + D(P) : y + D(P) - D(6));
        if (Size != size)
        {
            Size = size;
            if (Visible && anchorBottom >= 0) Top = anchorBottom - Height;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(th.Bg);

        // The panel repaints every second while open; only redraw the chip when it changes.
        string key = snap.Glyph(false) + "|" + th.Fg.ToArgb();
        if (key != headKey)
        {
            if (headIcon != null) headIcon.Dispose();
            headIcon = ChipIcon.Render(D(28), snap.Glyph(false), th.Fg);
            headKey = key;
        }
        g.DrawImage(headIcon, D(P), D(17), headIcon.Width, headIcon.Height);
        TextRenderer.DrawText(g, snap.Name, fTitle, new Rectangle(D(56), D(12), D(W - 56 - P), D(24)), th.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        Color dot = snap.State == GpuState.Ready ? th.Good
                  : snap.State == GpuState.Error ? th.Bad
                  : snap.State == GpuState.Absent && !snap.SafeToUnplug ? th.Fg2 : th.Warn;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(dot)) g.FillEllipse(b, D(57), D(42), D(7), D(7));
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, snap.StatusText, fBody, new Point(D(69), D(37)), th.Fg2);

        using (var pen = new Pen(th.Line)) g.DrawLine(pen, D(P), D(66), D(W - P), D(66));

        int y = D(78);
        var rows = Rows();
        if (rows.Count > 0)
        {
            using (var barBg = new SolidBrush(th.BarBg))
            using (var barFg = new SolidBrush(th.Fg))
                foreach (var r in rows)
                {
                    var line = new Rectangle(D(P), y, D(W - 2 * P), D(20));
                    TextRenderer.DrawText(g, r.Label, fBody, line, th.Fg2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, r.Value, fValue, line, th.Fg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    if (r.Bar >= 0)
                    {
                        var bar = new Rectangle(D(P), y + D(23), D(W - 2 * P), Math.Max(2, D(4)));
                        g.FillRectangle(barBg, bar);
                        int w = (int)Math.Round(bar.Width * Math.Max(0f, Math.Min(1f, r.Bar)));
                        if (w > 0) g.FillRectangle(barFg, bar.X, bar.Y, w, bar.Height);
                        y += D(38);
                    }
                    else y += D(26);
                }
            if (snap.Message != null)
            {
                TextRenderer.DrawText(g, snap.Message, fBody, new Rectangle(D(P), y + D(4), D(W - 2 * P), TextHeight(snap.Message)),
                    th.Bad, TextFormatFlags.WordBreak);
                y += TextHeight(snap.Message) + D(8);
            }
            if (AppsText != null)
                TextRenderer.DrawText(g, AppsText, fBody, new Rectangle(D(P), y + D(4), D(W - 2 * P), TextHeight(AppsText)),
                    th.Fg2, TextFormatFlags.WordBreak);
        }
        else
        {
            string body = snap.BodyText ?? "";
            TextRenderer.DrawText(g, body, fBody, new Rectangle(D(P), y, D(W - 2 * P), TextHeight(body)),
                th.Fg2, TextFormatFlags.WordBreak);
        }
    }

    public void ShowNearCursor()
    {
        Relayout();
        Point pt = Cursor.Position;
        Screen scr = Screen.FromPoint(pt);
        Rectangle wa = scr.WorkingArea, b = scr.Bounds;
        int m = D(12);
        int x = pt.X - Width / 2, y;
        anchorBottom = -1;
        if (wa.Bottom < b.Bottom) { anchorBottom = wa.Bottom - m; y = anchorBottom - Height; }   // taskbar at the bottom
        else if (wa.Top > b.Top) y = wa.Top + m;                                                // taskbar at the top
        else
        {
            if (wa.Left > b.Left) x = wa.Left + m;
            else if (wa.Right < b.Right) x = wa.Right - Width - m;
            if (pt.Y > b.Top + b.Height / 2) { anchorBottom = Math.Min(pt.Y, wa.Bottom) - m; y = anchorBottom - Height; }
            else y = pt.Y + m;
        }
        x = Math.Max(wa.Left + m, Math.Min(x, wa.Right - Width - m));
        y = Math.Max(wa.Top + m, y);
        Location = new Point(x, y);
        Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    public void HideNow()
    {
        if (!Visible) return;
        Hide();
        HiddenAt = DateTime.Now;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HideNow();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { HideNow(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
