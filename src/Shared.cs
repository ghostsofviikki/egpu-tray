using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// P/Invoke DLLs (cfgmgr32, pdh, nvml...) load from System32 only.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

static class AppInfo
{
    public const string Name = "eGPU Tray";
    public const string Version = "1.1.0";
    public const string ExeName = "eGPUTray.exe";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\eGPUTray";
    public const string MutexName = @"Local\eGPUTray.Instance";
    public const string ShowEvent = @"Local\eGPUTray.Show";
    public const string ExitEvent = @"Local\eGPUTray.Exit";

    public static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name); }
    }
    public const string UninstallerName = "Uninstall.exe";
    public const string SettingsKey = @"Software\eGPU Tray";
    public static string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }
    public static string UninstallerExe { get { return Path.Combine(InstallDir, UninstallerName); } }
    public static string StartMenuLink
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); }
    }
}

// DLL-planting protection. Setup runs from Downloads and the uninstaller from %TEMP%, folders that
// can hold stray DLLs; load Windows DLLs from System32 only, never from the exe's or current folder.
static class Hardening
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
    [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(uint flags);
    const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

    public static void Apply()
    {
        try { SetDllDirectoryW(""); } catch { }   // drops the current directory from the search order
        try { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32); } catch { }
    }
}

// Layout helpers for the setup and uninstall windows. Coordinates are written at 96 DPI and
// scaled here explicitly (WinForms auto-scaling scaled some controls twice on high-DPI screens).
static class Dlg
{
    public static readonly float Scale = GetScale();
    static float GetScale()
    {
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
    }
    public static int D(int v) { return (int)Math.Round(v * Scale); }
    public static System.Drawing.Rectangle R(int x, int y, int w, int h) { return new System.Drawing.Rectangle(D(x), D(y), D(w), D(h)); }
    public static System.Drawing.Point P(int x, int y) { return new System.Drawing.Point(D(x), D(y)); }
    public static System.Drawing.Size S(int w, int h) { return new System.Drawing.Size(D(w), D(h)); }
}

// Per-user install: no admin rights needed.
static class Installer
{
    public static bool IsInstalled { get { return File.Exists(AppInfo.InstalledExe); } }

    public static void Install(Stream app, Stream uninstaller, bool autostart)
    {
        RemoveLegacy();
        StopRunning();
        Directory.CreateDirectory(AppInfo.InstallDir);
        string target = AppInfo.InstalledExe;
        WriteFile(target, app);
        WriteFile(AppInfo.UninstallerExe, uninstaller);

        CreateShortcut(AppInfo.StartMenuLink, target, "Shows the eGPU connection in the system tray");
        SetAutostart(autostart, target);

        long bytes = new FileInfo(target).Length + new FileInfo(AppInfo.UninstallerExe).Length;
        using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.UninstallKey))
        {
            k.SetValue("DisplayName", AppInfo.Name);
            k.SetValue("DisplayVersion", AppInfo.Version);
            k.SetValue("Publisher", AppInfo.Name);
            k.SetValue("DisplayIcon", target + ",0");
            k.SetValue("InstallLocation", AppInfo.InstallDir);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("UninstallString", "\"" + AppInfo.UninstallerExe + "\"");
            k.SetValue("QuietUninstallString", "\"" + AppInfo.UninstallerExe + "\" /S");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
        }
    }

    // The app used to be called "eGPU Monitor". Remove that install quietly so there aren't two copies,
    // and carry over the remembered card name.
    static void RemoveLegacy()
    {
        const string oldName = "eGPU Monitor";
        const string oldExe = "EGpuMonitor.exe";
        const string oldSettings = @"Software\eGPU Monitor";
        string oldDir = Path.Combine(Path.GetDirectoryName(AppInfo.InstallDir), oldName);

        try { using (var ev = EventWaitHandle.OpenExisting(@"Local\eGPUMonitor.Exit")) ev.Set(); } catch { }
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(oldExe)))
            using (p)
                try { if (!p.WaitForExit(5000)) p.Kill(); } catch { }

        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey, true))
                if (run != null) run.DeleteValue(oldName, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\eGPUMonitor", false);
        }
        catch { }
        try { File.Delete(Path.Combine(Path.GetDirectoryName(AppInfo.StartMenuLink), oldName + ".lnk")); } catch { }

        try
        {
            using (var old = Registry.CurrentUser.OpenSubKey(oldSettings))
            {
                object name = old == null ? null : old.GetValue("LastName");
                if (name != null)
                    using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.SettingsKey))
                        if (k.GetValue("LastName") == null) k.SetValue("LastName", name);
            }
            Registry.CurrentUser.DeleteSubKeyTree(oldSettings, false);
        }
        catch { }
        RemoveTrayIconEntries(oldExe);

        for (int i = 0; i < 10 && Directory.Exists(oldDir); i++)
        {
            try { Directory.Delete(oldDir, true); }
            catch { Thread.Sleep(300); }
        }
    }

    // Writes next to the target first and swaps it in, so a failed update never leaves a half-written exe.
    static void WriteFile(string path, Stream data)
    {
        string tmp = path + ".new";
        using (var f = File.Create(tmp)) data.CopyTo(f);
        for (int i = 0; ; i++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                if (i >= 10) { try { File.Delete(tmp); } catch { } throw; }
                Thread.Sleep(500);   // the old exe can stay locked for a moment after it exits
            }
        }
    }

    // Removes everything Install() created. Must not run from inside the install folder.
    // removeSettings also clears the remembered card name and Windows' tray-icon entry.
    public static void RemoveAll(bool removeSettings)
    {
        StopRunning();
        SetAutostart(false, null);
        try { File.Delete(AppInfo.StartMenuLink); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKey, false); } catch { }

        if (removeSettings)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.SettingsKey, false); } catch { }
            RemoveTrayIconEntries(AppInfo.ExeName);
        }

        // Files can stay locked for a moment after the app exits.
        for (int i = 0; Directory.Exists(AppInfo.InstallDir); i++)
        {
            try { Directory.Delete(AppInfo.InstallDir, true); }
            catch (Exception)
            {
                if (i < 10) { Thread.Sleep(300); continue; }
                // Still locked: usually the copy of Uninstall.exe that launched us is waiting for us to finish.
                // Remove what we can now and let cmd remove the folder once that process has exited.
                if (OnlyUninstallerLeft()) { DeleteFolderLater(AppInfo.InstallDir); return; }
                throw new IOException("Couldn't delete " + AppInfo.InstallDir + ". Close any window showing that folder and try again.");
            }
        }
    }

    static bool OnlyUninstallerLeft()
    {
        try
        {
            foreach (string f in Directory.GetFiles(AppInfo.InstallDir, "*", SearchOption.AllDirectories))
                if (!f.Equals(AppInfo.UninstallerExe, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { return false; }
            return true;
        }
        catch { return false; }
    }

    static void DeleteFolderLater(string dir)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetTempPath();
        Process.Start(psi);
    }

    static void RemoveTrayIconEntries(string exeName)
    {
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
            {
                if (root == null) return;
                foreach (string name in root.GetSubKeyNames())
                {
                    string path;
                    using (var k = root.OpenSubKey(name)) path = k == null ? null : k.GetValue("ExecutablePath") as string;
                    if (path != null && path.EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase))
                        root.DeleteSubKeyTree(name, false);
                }
            }
        }
        catch { }
    }

    // Asks a running copy to exit cleanly (so its tray icon disappears), then kills any leftovers.
    public static void StopRunning()
    {
        try { using (var ev = EventWaitHandle.OpenExisting(AppInfo.ExitEvent)) ev.Set(); } catch { }
        int self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppInfo.ExeName)))
        {
            using (p)
            {
                if (p.Id == self) continue;
                try
                {
                    if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }
    }

    public static bool GetAutostart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey))
            return k != null && k.GetValue(AppInfo.Name) != null;
    }

    public static void SetAutostart(bool on, string exe)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.RunKey))
        {
            if (on) k.SetValue(AppInfo.Name, "\"" + exe + "\"");
            else k.DeleteValue(AppInfo.Name, false);
        }
    }

    static void CreateShortcut(string link, string target, string description)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
        Type lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }
}
