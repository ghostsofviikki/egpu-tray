using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Optional program run each time the eGPU becomes ready, e.g. to re-apply an MSI Afterburner profile
// (the card's clocks and power limit reset whenever it is plugged in).
static class ConnectAction
{
    const string ProgramValue = "OnConnectProgram", ArgsValue = "OnConnectArgs";

    public static bool IsSet
    {
        get { string p, a; Load(out p, out a); return p.Length > 0; }
    }

    public static void Load(out string program, out string args)
    {
        program = args = "";
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.SettingsKey))
            {
                if (k == null) return;
                program = (k.GetValue(ProgramValue) as string ?? "").Trim();
                args = (k.GetValue(ArgsValue) as string ?? "").Trim();
            }
        }
        catch { }
    }

    public static void Save(string program, string args)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.SettingsKey))
        {
            if (string.IsNullOrEmpty(program))
            {
                k.DeleteValue(ProgramValue, false);
                k.DeleteValue(ArgsValue, false);
            }
            else
            {
                k.SetValue(ProgramValue, program);
                k.SetValue(ArgsValue, args ?? "");
            }
        }
    }

    // Runs the saved program in the background; failures only go to the log.
    public static void Run()
    {
        string program, args;
        Load(out program, out args);
        if (program.Length == 0) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = Start(program, args);
            Log.Write(err == null ? "Ran on-connect program: " + program : "On-connect program failed: " + err);
        });
    }

    // Returns null on success or a human-readable reason.
    public static string Start(string program, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(program, args ?? "");
            if (Path.IsPathRooted(program)) psi.WorkingDirectory = Path.GetDirectoryName(program);
            if (program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Started directly so console tools such as schtasks don't flash a window.
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                try { using (Process.Start(psi)) { } return null; }
                catch (Win32Exception ex) { if (ex.NativeErrorCode != 740) throw; }   // ERROR_ELEVATION_REQUIRED
                psi.CreateNoWindow = false;
            }
            psi.UseShellExecute = true;   // .bat, .lnk, or an exe that needs admin rights (Windows asks first)
            using (Process.Start(psi)) { }
            return null;
        }
        catch (Win32Exception ex) { return ex.NativeErrorCode == 1223 ? "cancelled at the admin prompt" : ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }
}

class ConnectActionForm : Form
{
    readonly TextBox program = new TextBox();
    readonly TextBox args = new TextBox();
    readonly Label status = new Label();

    public ConnectActionForm()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f);
        Text = AppInfo.Name + " — Run when connected";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Window;
        ClientSize = Dlg.S(480, 292);
        Icon = Icon.FromHandle(ChipIcon.RenderTile(32).GetHicon());

        Controls.Add(new Label
        {
            Text = "Runs a program each time the eGPU is plugged in and ready, for example to re-apply " +
                   "an MSI Afterburner profile. Leave Program empty to turn this off.",
            Bounds = Dlg.R(18, 16, 444, 40)
        });

        Controls.Add(new Label { Text = "Program", AutoSize = true, Location = Dlg.P(18, 68) });
        program.Bounds = Dlg.R(18, 88, 356, 24);
        Controls.Add(program);
        var browse = new Button { Text = "Browse…", Bounds = Dlg.R(382, 87, 80, 26) };
        browse.Click += OnBrowse;
        Controls.Add(browse);

        Controls.Add(new Label { Text = "Arguments", AutoSize = true, Location = Dlg.P(18, 124) });
        args.Bounds = Dlg.R(18, 144, 444, 24);
        Controls.Add(args);

        Controls.Add(new Label
        {
            Text = "Programs that need admin rights (like Afterburner) ask for permission every time. " +
                   "To avoid that, run an elevated scheduled task instead: schtasks.exe with /run /tn \"task name\".",
            ForeColor = SystemColors.GrayText, Bounds = Dlg.R(18, 178, 444, 48)
        });

        status.ForeColor = SystemColors.GrayText;
        status.Bounds = Dlg.R(18, 232, 444, 20);
        status.AutoEllipsis = true;
        Controls.Add(status);

        var test = new Button { Text = "Run now", Bounds = Dlg.R(18, 254, 90, 28) };
        test.Click += OnTest;
        Controls.Add(test);

        var ok = new Button { Text = "Save", Bounds = Dlg.R(286, 254, 84, 28) };
        ok.Click += OnSave;
        Controls.Add(ok);

        // The window is modeless, so DialogResult alone wouldn't close it.
        var cancel = new Button { Text = "Cancel", Bounds = Dlg.R(378, 254, 84, 28) };
        cancel.Click += delegate { Close(); };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;

        string p, a;
        ConnectAction.Load(out p, out a);
        program.Text = p;
        args.Text = a;
        ResumeLayout(false);
        PerformLayout();
    }

    void OnBrowse(object sender, EventArgs e)
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Filter = "Programs (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|All files (*.*)|*.*";
            dlg.DereferenceLinks = false;
            if (program.Text.Length > 0 && Path.IsPathRooted(program.Text))
                try { dlg.InitialDirectory = Path.GetDirectoryName(program.Text); } catch { }
            if (dlg.ShowDialog(this) == DialogResult.OK) program.Text = dlg.FileName;
        }
    }

    void OnTest(object sender, EventArgs e)
    {
        string p = program.Text.Trim();
        if (p.Length == 0) { status.Text = "Enter a program first."; return; }
        UseWaitCursor = true;
        string err = ConnectAction.Start(p, args.Text.Trim());
        UseWaitCursor = false;
        status.Text = err == null ? "Started." : "Couldn't start it: " + err;
    }

    void OnSave(object sender, EventArgs e)
    {
        try
        {
            string p = program.Text.Trim();
            ConnectAction.Save(p, p.Length == 0 ? "" : args.Text.Trim());
            Log.Write(p.Length == 0 ? "On-connect program cleared" : "On-connect program set: " + p);
            Close();
        }
        catch (Exception ex) { status.Text = "Couldn't save: " + ex.Message; }
    }
}
