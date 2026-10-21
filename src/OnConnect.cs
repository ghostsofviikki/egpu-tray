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

    const int ExitWaitMs = 30000;
    // Seconds to wait before each retry. For a while after the eGPU connects, schtasks can fail
    // ("cannot find the file specified") even though the task exists.
    static readonly int[] RetryDelays = { 10, 20, 30, 60, 60 };
    static int running;

    // Set when every attempt failed; the tray runs it again when the session is next unlocked.
    public static volatile bool Pending;

    // Runs the saved program in the background; failures only go to the log.
    public static void Run()
    {
        string program, args;
        Load(out program, out args);
        if (program.Length == 0) return;
        if (Interlocked.Exchange(ref running, 1) == 1) return;   // already running or retrying
        Pending = false;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                int attempts = RetryDelays.Length + 1;
                for (int i = 1; i <= attempts; i++)
                {
                    string err = Start(program, args, true);
                    if (err == null) { Log.Write("Ran on-connect program: " + program); return; }
                    Log.Write("On-connect program failed (attempt " + i + " of " + attempts + "): " + err);                    if (i < attempts) Thread.Sleep(RetryDelays[i - 1] * 1000);
                }
                Pending = true;
                Log.Write("On-connect program will be retried when Windows is next unlocked");
            }
            finally { Interlocked.Exchange(ref running, 0); }
        });
    }

    // Returns null on success or a human-readable reason. With wait, a program started directly is
    // waited for (up to 30 s) and a non-zero exit code counts as a failure.
    public static string Start(string program, string args, bool wait = false)
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
                psi.RedirectStandardOutput = psi.RedirectStandardError = wait;
                try
                {
                    using (var p = Process.Start(psi))
                    {
                        if (!wait) return null;
                        var output = p.StandardOutput.ReadToEndAsync();
                        var errors = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(ExitWaitMs)) return null;   // still running, e.g. a GUI program
                        if (p.ExitCode == 0) return null;
                        string text = (errors.Result.Trim() + " " + output.Result.Trim()).Trim();
                        return "exit code " + p.ExitCode + (text.Length > 0 ? ": " + text : "");
                    }
                }
                catch (Win32Exception ex) { if (ex.NativeErrorCode != 740) throw; }   // ERROR_ELEVATION_REQUIRED
                psi.CreateNoWindow = false;
                psi.RedirectStandardOutput = psi.RedirectStandardError = false;
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
        string a = args.Text.Trim();
        UseWaitCursor = true;
        status.Text = "Running…";
        // Waits for the exit code off the UI thread, so a failing schtasks shows its error here.
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = ConnectAction.Start(p, a, true);
            Log.Write(err == null ? "Run now: " + p : "Run now failed: " + err);
            try
            {
                BeginInvoke((Action)(() =>
                {
                    UseWaitCursor = false;
                    status.Text = err == null ? "Started." : "Couldn't start it: " + err;
                }));
            }
            catch (InvalidOperationException) { }   // window already closed
        });
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
