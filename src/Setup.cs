using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("eGPU Tray Setup")]
[assembly: AssemblyProduct("eGPU Tray")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

// Setup.exe carries eGPUTray.exe as an embedded resource.
//   eGPU-Tray-Setup.exe        interactive install
//   eGPU-Tray-Setup.exe /S     silent install (autostart on, launches the app)
static class SetupProgram
{
    [STAThread]
    static int Main(string[] args)
    {
        Hardening.Apply();
        if (args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase)))
        {
            try { Run(true, true); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
        return 0;
    }

    public static void Run(bool autostart, bool launch)
    {
        var asm = Assembly.GetExecutingAssembly();
        using (var app = asm.GetManifestResourceStream(AppInfo.ExeName))
        using (var uninstaller = asm.GetManifestResourceStream(AppInfo.UninstallerName))
            Installer.Install(app, uninstaller, autostart);
        if (launch)
        {
            var psi = new ProcessStartInfo(AppInfo.InstalledExe);
            psi.WorkingDirectory = AppInfo.InstallDir;
            Process.Start(psi);
        }
    }
}

class SetupForm : Form
{
    readonly Label desc = new Label();
    readonly CheckBox auto = new CheckBox();
    readonly Button install = new Button();
    readonly Button cancel = new Button();

    public SetupForm()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f);
        Text = AppInfo.Name + " Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Window;
        ClientSize = Dlg.S(460, 262);

        var tile = ChipIcon.RenderTile(Dlg.D(48));
        Icon = Icon.FromHandle(ChipIcon.RenderTile(32).GetHicon());
        Controls.Add(new PictureBox { Image = tile, SizeMode = PictureBoxSizeMode.Zoom, Bounds = Dlg.R(22, 22, 48, 48) });

        Controls.Add(new Label { Text = AppInfo.Name, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = Dlg.P(84, 20) });
        Controls.Add(new Label
        {
            Text = "Version " + AppInfo.Version + (Installer.IsInstalled ? "  ·  already installed, will be updated" : ""),
            ForeColor = SystemColors.GrayText, AutoSize = true, Location = Dlg.P(86, 52)
        });

        desc.Text = "Keeps a small chip icon in the system tray that shows whether your eGPU is connected. " +
                    "Click it to see usage, VRAM and temperature, or to disconnect the eGPU safely.";
        desc.Bounds = Dlg.R(22, 90, 416, 52);
        Controls.Add(desc);

        Controls.Add(new Label
        {
            Text = "Installs for your account only (no admin rights needed):\n" + AppInfo.InstallDir,
            ForeColor = SystemColors.GrayText, Bounds = Dlg.R(22, 146, 416, 40), AutoEllipsis = true
        });

        auto.Text = "Start automatically when I sign in";
        auto.Checked = true;
        auto.AutoSize = true;
        auto.Location = Dlg.P(24, 192);
        Controls.Add(auto);

        install.Text = Installer.IsInstalled ? "Update" : "Install";
        install.Bounds = Dlg.R(262, 222, 90, 28);
        install.Click += OnInstall;
        Controls.Add(install);

        cancel.Text = "Cancel";
        cancel.Bounds = Dlg.R(360, 222, 80, 28);
        cancel.Click += delegate { Close(); };
        Controls.Add(cancel);

        AcceptButton = install;
        CancelButton = cancel;
        ResumeLayout(false);
        PerformLayout();
    }

    void OnInstall(object sender, EventArgs e)
    {
        install.Enabled = cancel.Enabled = auto.Enabled = false;
        UseWaitCursor = true;
        try
        {
            SetupProgram.Run(auto.Checked, true);
            desc.Text = "eGPU Tray is installed and running. Look for the chip icon in the system tray. " +
                        "You can remove it any time from Settings › Apps.";
            install.Visible = false;
            cancel.Text = "Finish";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Installation failed:\n\n" + ex.Message, Text);
            install.Enabled = auto.Enabled = true;
        }
        finally
        {
            cancel.Enabled = true;
            UseWaitCursor = false;
        }
    }
}
