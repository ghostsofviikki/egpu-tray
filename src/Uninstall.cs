using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("eGPU Tray Uninstall")]
[assembly: AssemblyProduct("eGPU Tray")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

// Uninstall.exe lives in the install folder and is registered in Settings > Apps.
//   Uninstall.exe        asks first, then removes everything
//   Uninstall.exe /S     silent: removes everything, settings included
// A program can't delete the folder it runs from, so it first re-launches itself from %TEMP%;
// that copy does the work and deletes itself afterwards.
static class UninstallProgram
{
    const string TempFlag = "/fromtemp";
    const string TempPrefix = "eGPU-Tray-Uninstall-";

    [STAThread]
    static int Main(string[] args)
    {
        Hardening.Apply();
        bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        bool fromTemp = args.Any(a => a.Equals(TempFlag, StringComparison.OrdinalIgnoreCase));
        string self = Application.ExecutablePath;

        if (!fromTemp && self.StartsWith(AppInfo.InstallDir + "\\", StringComparison.OrdinalIgnoreCase))
        {
            // A fresh folder of its own, not %TEMP% itself, where other programs' leftover DLLs could be loaded.
            string dir = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, AppInfo.UninstallerName);
            File.Copy(self, copy);
            var psi = new ProcessStartInfo(copy, (silent ? "/S " : "") + TempFlag);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            using (var p = Process.Start(psi))
            {
                // Silent callers (scripts, Settings > Apps) expect to wait for the result.
                if (silent) { p.WaitForExit(); return p.ExitCode; }
            }
            return 0;
        }

        int code;
        if (silent)
        {
            try { Installer.RemoveAll(true); code = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); code = 1; }
        }
        else
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new UninstallForm();
            Application.Run(form);
            code = form.Removed ? 0 : 1;
        }

        if (fromTemp) DeleteSelfLater(self);
        return code;
    }

    // Removes the temp copy's folder once this process has exited. Only a folder this program created
    // is ever removed, even if /fromtemp is passed to a copy somewhere else.
    static void DeleteSelfLater(string self)
    {
        string dir = Path.GetDirectoryName(self);
        string parent = Path.GetDirectoryName(dir);
        bool ours = Path.GetFileName(dir).StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parent.TrimEnd('\\'), Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string cmd = ours ? "rmdir /s /q \"" + dir + "\"" : "del /f /q \"" + self + "\"";
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & " + cmd);
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetTempPath();
        Process.Start(psi);
    }
}

class UninstallForm : Form
{
    public bool Removed;

    readonly Label desc = new Label();
    readonly CheckBox settings = new CheckBox();
    readonly Button remove = new Button();
    readonly Button cancel = new Button();

    public UninstallForm()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f);
        Text = "Uninstall " + AppInfo.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Window;
        ClientSize = Dlg.S(460, 250);

        Icon = Icon.FromHandle(ChipIcon.RenderTile(32).GetHicon());
        Controls.Add(new PictureBox
        {
            Image = ChipIcon.RenderTile(Dlg.D(48)),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = Dlg.R(22, 22, 48, 48)
        });
        Controls.Add(new Label { Text = "Uninstall " + AppInfo.Name, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = Dlg.P(84, 20) });
        Controls.Add(new Label
        {
            Text = Installer.IsInstalled ? "Version " + AppInfo.Version : "Not installed (cleaning up leftovers)",
            ForeColor = SystemColors.GrayText, AutoSize = true, Location = Dlg.P(86, 52)
        });

        desc.Text = "This closes eGPU Tray and removes it from your PC: the tray app, its folder, " +
                    "the Start menu entry, the sign-in autostart and the Settings › Apps entry.";
        desc.Bounds = Dlg.R(22, 90, 416, 52);
        Controls.Add(desc);

        settings.Text = "Also remove saved settings (remembered card name, tray icon position)";
        settings.Checked = true;
        settings.Bounds = Dlg.R(24, 150, 416, 40);
        Controls.Add(settings);

        remove.Text = "Uninstall";
        remove.Bounds = Dlg.R(262, 206, 90, 28);
        remove.Click += OnRemove;
        Controls.Add(remove);

        cancel.Text = "Cancel";
        cancel.Bounds = Dlg.R(360, 206, 80, 28);
        cancel.Click += delegate { Close(); };
        Controls.Add(cancel);

        AcceptButton = remove;
        CancelButton = cancel;
        ResumeLayout(false);
        PerformLayout();
    }

    void OnRemove(object sender, EventArgs e)
    {
        remove.Enabled = cancel.Enabled = settings.Enabled = false;
        UseWaitCursor = true;
        Application.DoEvents();
        try
        {
            Installer.RemoveAll(settings.Checked);
            Removed = true;
            desc.Text = AppInfo.Name + " has been removed from your PC.";
            settings.Visible = false;
            remove.Visible = false;
            cancel.Text = "Close";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Uninstall didn't finish:\n\n" + ex.Message, Text);
            remove.Enabled = settings.Enabled = true;
        }
        finally
        {
            cancel.Enabled = true;
            UseWaitCursor = false;
        }
    }
}
