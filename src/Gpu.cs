using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Vendor-neutral eGPU monitoring.
//   Detection   any PCI display adapter that Windows marks as removable (Thunderbolt / USB4 / hot-plug PCIe)
//   Readiness   the graphics driver has published a WDDM adapter (D3DKMT)
//   Usage/VRAM  Windows GPU performance counters (same source as Task Manager)
//   Temp/fan    D3DKMT adapter perf data, reported by the driver
//   PCIe link   Windows PCI device properties
//   Power       NVML, only on NVIDIA cards

enum GpuState { Absent, Starting, Ready, Error, Ejecting }

class GpuSnapshot
{
    public GpuState State;
    public bool Present;          // device node exists and is not held for removal
    public bool SafeToUnplug;     // Windows released it; waiting for the cable to be pulled
    public string Name = "eGPU";
    public string Vendor = "";
    public uint Problem;          // Device Manager error code
    public string Message;        // e.g. why a disconnect was refused
    public bool HasMetrics;
    public uint Util, Temp, FanRpm, PcieGen, PcieWidth;
    public double PowerW = -1;
    public ulong MemUsed, MemTotal;
    public List<string> Apps;     // apps using the GPU; filled only while the panel is open

    public string ShortName
    {
        get
        {
            string n = Name;
            foreach (string p in new[] { "NVIDIA ", "GeForce ", "AMD ", "Intel(R) ", "Intel " })
                if (n.StartsWith(p)) n = n.Substring(p.Length);
            return n;
        }
    }

    public string StatusText
    {
        get
        {
            switch (State)
            {
                case GpuState.Ready: return "Connected";
                case GpuState.Starting: return "Starting\u2026";
                case GpuState.Ejecting: return "Disconnecting\u2026";
                case GpuState.Error: return Problem != 0 ? "Problem (code " + Problem + ")" : "Not responding";
                default: return SafeToUnplug ? "Safe to unplug" : "Not connected";
            }
        }
    }

    public string BodyText
    {
        get
        {
            string drv = Vendor.Length > 0 ? "the " + Vendor + " driver" : "the graphics driver";
            switch (State)
            {
                case GpuState.Starting: return "eGPU detected. Waiting for " + drv + "\u2026";
                case GpuState.Ejecting: return "Asking Windows to release the eGPU\u2026";
                case GpuState.Error:
                    switch (Problem)
                    {
                        case 0: return "The eGPU is connected but " + drv + " isn't responding. Unplug it, wait 5 seconds and plug it back in.";
                        case 43: return "Code 43: the driver stopped the device. Unplug it, wait 5 seconds and plug it back in. If it keeps happening, reinstall " + drv + ".";
                        case 12: return "Code 12: Windows couldn't give the eGPU enough resources. Check the Thunderbolt / PCIe settings in the BIOS.";
                        case 10: return "Code 10: the device couldn't start. Unplug the cable and plug it back in.";
                        case 22: return "The eGPU is disabled in Device Manager.";
                        case 28: return "No driver is installed for the eGPU. Install the graphics driver from the card's maker.";
                        default: return "Windows reports error code " + Problem + " for the eGPU. Try unplugging it and plugging it back in.";
                    }
                case GpuState.Ready: return null;
                default:
                    return SafeToUnplug
                        ? "Windows has released the eGPU. You can unplug the cable now. To use it again, unplug it and plug it back in."
                        : "Plug in your eGPU. The chip icon turns solid when it's ready to use.";
            }
        }
    }

    public ChipGlyph Glyph(bool blinkPhase)
    {
        switch (State)
        {
            case GpuState.Ready: return ChipGlyph.Connected;
            case GpuState.Error: return ChipGlyph.Error;
            case GpuState.Starting: return blinkPhase ? ChipGlyph.Connected : ChipGlyph.Busy;
            case GpuState.Ejecting: return blinkPhase ? ChipGlyph.Disconnected : ChipGlyph.Busy;
            default: return ChipGlyph.Disconnected;
        }
    }

    public static string Gb(ulong bytes) { return (bytes / 1073741824.0).ToString("0.0"); }
}

// Polls the device on a background thread and publishes snapshots.
class GpuMonitor
{
    public event Action<GpuSnapshot> Updated;
    public volatile bool Fast;   // panel open: poll faster and collect the app list

    readonly object gate = new object();   // guards everything that opens the adapter
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    volatile bool stop, ejecting;
    volatile string deviceId;
    bool nvUp, first = true;
    GpuCounters counters;
    GpuState state = GpuState.Absent;
    DateTime since = DateTime.Now;
    string lastName = LoadName();
    string ejectMessage;
    DateTime ejectMessageAt;

    // Remember the card's name so the panel can show it while the eGPU is unplugged.
    static string LoadName()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.SettingsKey))
            {
                string n = k == null ? null : k.GetValue("LastName") as string;
                return string.IsNullOrEmpty(n) ? "eGPU" : n;
            }
        }
        catch { return "eGPU"; }
    }
    void RememberName(string name)
    {
        if (string.IsNullOrEmpty(name) || name == lastName) return;
        lastName = name;
        try { using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.SettingsKey)) k.SetValue("LastName", name); } catch { }
    }

    public void Start()
    {
        var t = new Thread(Loop);
        t.IsBackground = true;
        t.Name = "gpu-poll";
        t.Start();
    }
    public void Stop() { stop = true; wake.Set(); }
    public void Wake() { wake.Set(); }

    void Loop()
    {
        while (!stop)
        {
            GpuSnapshot s;
            try { s = Poll(); }
            catch (Exception ex)
            {
                Log.Write("Poll error: " + ex.Message);
                s = new GpuSnapshot { State = GpuState.Absent, Name = lastName };
            }
            var h = Updated;
            if (h != null) h(s);
            wake.WaitOne(Fast ? 1000 : 2500);
        }
        lock (gate) Release();
    }

    // Drops everything that refers to the adapter. Caller holds the lock.
    void Release()
    {
        if (nvUp) { Nvml.Shutdown(); nvUp = false; }
        if (counters != null) { counters.Dispose(); counters = null; }
    }

    GpuSnapshot Poll()
    {
        var s = new GpuSnapshot { Name = lastName };
        string id = Cfg.FindExternalGpu();
        uint problem = 0;
        bool present = id != null && Cfg.GetProblem(id, out problem);
        bool held = present && problem == 47;   // CM_PROB_HELD_FOR_EJECT: prepared for safe removal
        if (held) present = false;
        deviceId = present ? id : null;
        if (id != null) s.Vendor = Cfg.VendorName(id);

        if (ejectMessage != null && (DateTime.Now - ejectMessageAt).TotalMinutes > 2) ejectMessage = null;

        if (ejecting)
        {
            s.State = GpuState.Ejecting;
            s.Present = present;
            return s;
        }

        if (!present)
        {
            lock (gate) Release();
            if (state != GpuState.Absent && !first) Log.Write(held ? "eGPU released, safe to unplug" : "eGPU disconnected");
            state = GpuState.Absent;
            s.State = GpuState.Absent;
            s.SafeToUnplug = held;
            first = false;
            return s;
        }

        if (state == GpuState.Absent)
        {
            state = GpuState.Starting;
            since = DateTime.Now;
            ejectMessage = null;
            if (!first) Log.Write("eGPU detected");
        }
        RememberName(Cfg.GetDescription(id));
        s.Name = lastName;
        s.Present = true;
        s.Problem = problem;

        if (problem != 0)
        {
            lock (gate) Release();
            if (state == GpuState.Ready || (state != GpuState.Error && (DateTime.Now - since).TotalSeconds >= 15))
            {
                state = GpuState.Error;
                Log.Write("eGPU problem: code " + problem);
            }
        }
        else
        {
            bool ok = false;
            List<uint> pids = null;
            lock (gate)
            {
                if (!ejecting)
                {
                    long luid;
                    Kmt.PerfData perf;
                    string iface = Cfg.GetAdapterInterface(id);
                    if (iface != null && Kmt.Probe(iface, out luid, out perf))
                    {
                        ok = true;
                        if (counters == null || counters.Luid != luid)
                        {
                            if (counters != null) counters.Dispose();
                            counters = new GpuCounters(luid);
                        }
                        counters.Collect();
                        s.Util = (uint)Math.Round(counters.Usage);
                        s.MemUsed = counters.DedicatedUsed;
                        s.Temp = perf.TempC;
                        s.FanRpm = perf.FanRpm;
                        if (Fast) pids = counters.Pids;

                        if (s.Vendor == "NVIDIA")
                        {
                            if (!nvUp) nvUp = Nvml.Init();
                            double w;
                            uint t;
                            if (nvUp && Nvml.Query(out w, out t))
                            {
                                s.PowerW = w;
                                if (s.Temp == 0) s.Temp = t;
                            }
                            else if (nvUp) { Nvml.Shutdown(); nvUp = false; }
                        }
                    }
                    else Release();
                }
            }

            if (ok)
            {
                s.MemTotal = Cfg.GetVramTotal(id);
                uint gen, width;
                if (Cfg.GetPcieLink(id, out gen, out width)) { s.PcieGen = gen; s.PcieWidth = width; }
                if (pids != null) s.Apps = ProcessNames(pids);
                s.HasMetrics = true;
                if (state != GpuState.Ready)
                    Log.Write(string.Format("eGPU ready: {0}, PCIe Gen{1} x{2}, {3} GB ({4:0}s)",
                        s.Name, s.PcieGen, s.PcieWidth, GpuSnapshot.Gb(s.MemTotal), (DateTime.Now - since).TotalSeconds));
                state = GpuState.Ready;
            }
            else if (state == GpuState.Ready)
            {
                state = GpuState.Starting;
                since = DateTime.Now;
            }
            else if (state != GpuState.Error && (DateTime.Now - since).TotalSeconds >= 30)
            {
                state = GpuState.Error;
                Log.Write("eGPU driver not responding");
            }
        }

        s.State = state;
        s.Message = ejectMessage;
        first = false;
        return s;
    }

    static List<string> ProcessNames(List<uint> pids)
    {
        int self = Process.GetCurrentProcess().Id;
        var names = new List<string>();
        foreach (uint pid in pids.Distinct())
        {
            if (pid == self || pid <= 4) continue;   // 0 = Idle, 4 = System (the kernel itself)
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    string n = p.ProcessName;
                    if (n.Equals("dwm", StringComparison.OrdinalIgnoreCase) || n.Equals("csrss", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(n);
                }
            }
            catch { }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
    }

    // Asks Windows to release the eGPU (like "Safely Remove Hardware"). done(null) on success.
    public void Eject(Action<string> done)
    {
        string id = deviceId;
        if (id == null) { done("The eGPU is not connected."); return; }
        ThreadPool.QueueUserWorkItem(delegate
        {
            lock (gate)
            {
                ejecting = true;
                Release();   // our own handles must not block removal
            }
            Wake();
            Log.Write("Disconnect requested");
            string err;
            try
            {
                err = Cfg.Eject(id);
                if (err == Cfg.AccessDenied) err = EjectElevated(id);
            }
            catch (Exception ex) { err = ex.Message; }
            Log.Write(err == null ? "Disconnect accepted" : "Disconnect failed: " + err);
            ejectMessage = err;
            ejectMessageAt = DateTime.Now;
            ejecting = false;
            Wake();
            done(err);
        });
    }

    static string EjectElevated(string id)
    {
        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--eject \"" + id + "\"");
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode == 0 ? null : "Windows refused to release the eGPU. Close apps using the GPU and try again.";
            }
        }
        catch (Win32Exception) { return "Disconnect cancelled."; }
    }
}

// ---------------------------------------------------------------- Windows device APIs (cfgmgr32)

static class Cfg
{
    public const string AccessDenied = "access-denied";
    const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    static Guid DisplayAdapterInterface = new Guid("1CA05180-A699-450A-9A0C-DE4FBE3DDD89");   // GUID_DISPLAY_DEVICE_ARRIVAL
    static readonly Guid PciDeviceProps = new Guid("3AB22E31-8264-4b4e-9AF5-A8D2D8E33E62");    // DEVPKEY_PciDevice_*
    const uint FILTER_PRESENT = 0x100, FILTER_CLASS = 0x200;
    const uint DN_HAS_PROBLEM = 0x400;
    const uint DRP_DEVICEDESC = 0x01, DRP_DRIVER = 0x0A, DRP_FRIENDLYNAME = 0x0D, DRP_CAPABILITIES = 0x10;
    const uint CAP_REMOVABLE = 0x4;
    const int CR_ACCESS_DENIED = 0x33;

    [StructLayout(LayoutKind.Sequential)] struct DevPropKey { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint prop, out uint type, byte[] buf, ref uint len, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[] buf, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid iface, string devId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid iface, string devId, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, StringBuilder vetoName, uint nameLen, uint flags);

    // First present PCI display adapter that is removable itself or sits behind a removable bridge.
    // PCI only, so USB display adapters (DisplayLink etc.) are not mistaken for an eGPU.
    public static string FindExternalGpu()
    {
        uint len;
        if (CM_Get_Device_ID_List_SizeW(out len, DisplayClass, FILTER_PRESENT | FILTER_CLASS) != 0 || len == 0) return null;
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(DisplayClass, buf, len, FILTER_PRESENT | FILTER_CLASS) != 0) return null;
        foreach (string id in new string(buf).Split('\0'))
        {
            if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
            uint dev;
            if (CM_Locate_DevNodeW(out dev, id, 0) != 0) continue;
            for (int depth = 0; depth < 4; depth++)
            {
                uint caps;
                if (GetDword(dev, DRP_CAPABILITIES, out caps) && (caps & CAP_REMOVABLE) != 0) return id;
                if (CM_Get_Parent(out dev, dev, 0) != 0) break;
            }
        }
        return null;
    }

    public static string VendorName(string id)
    {
        if (id.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0) return "NVIDIA";
        if (id.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0) return "AMD";
        if (id.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0) return "Intel";
        return "";
    }

    public static bool GetProblem(string id, out uint problem)
    {
        problem = 0;
        uint dev, status, prob;
        if (CM_Locate_DevNodeW(out dev, id, 0) != 0) return false;
        if (CM_Get_DevNode_Status(out status, out prob, dev, 0) != 0) return false;
        if ((status & DN_HAS_PROBLEM) != 0) problem = prob;
        return true;
    }

    public static string GetDescription(string id)
    {
        uint dev;
        if (CM_Locate_DevNodeW(out dev, id, 0) != 0) return null;
        return GetString(dev, DRP_FRIENDLYNAME) ?? GetString(dev, DRP_DEVICEDESC);
    }

    // Device path of the WDDM adapter; exists only once the graphics driver has started.
    public static string GetAdapterInterface(string id)
    {
        uint len;
        if (CM_Get_Device_Interface_List_SizeW(out len, ref DisplayAdapterInterface, id, 0) != 0 || len <= 1) return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref DisplayAdapterInterface, id, buf, len, 0) != 0) return null;
        string first = new string(buf).Split('\0')[0];
        return first.Length > 0 ? first : null;
    }

    // Total dedicated VRAM, as the driver reports it in its registry key.
    public static ulong GetVramTotal(string id)
    {
        uint dev;
        if (CM_Locate_DevNodeW(out dev, id, 0) != 0) return 0;
        string drv = GetString(dev, DRP_DRIVER);
        if (drv == null) return 0;
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + drv))
            {
                if (k == null) return 0;
                object q = k.GetValue("HardwareInformation.qwMemorySize");
                if (q is long) return (ulong)(long)q;
                object d = k.GetValue("HardwareInformation.MemorySize");
                if (d is int) return (uint)(int)d;
                var b = d as byte[];
                if (b != null && b.Length >= 4) return BitConverter.ToUInt32(b, 0);
            }
        }
        catch { }
        return 0;
    }

    public static bool GetPcieLink(string id, out uint gen, out uint width)
    {
        gen = width = 0;
        uint dev;
        if (CM_Locate_DevNodeW(out dev, id, 0) != 0) return false;
        return GetPropDword(dev, PciDeviceProps, 9, out gen) && GetPropDword(dev, PciDeviceProps, 10, out width) && gen > 0;
    }

    // Returns null on success, AccessDenied, or a human-readable reason.
    public static string Eject(string id)
    {
        uint dev;
        if (CM_Locate_DevNodeW(out dev, id, 0) != 0) return null;   // already gone

        // Eject the top-most removable node (the GPU itself on most enclosures).
        uint target = dev, cur = dev;
        for (int i = 0; i < 4; i++)
        {
            uint parent, caps;
            if (CM_Get_Parent(out parent, cur, 0) != 0 || !GetDword(parent, DRP_CAPABILITIES, out caps)) break;
            if ((caps & CAP_REMOVABLE) == 0) break;
            target = cur = parent;
        }

        int veto;
        var name = new StringBuilder(512);
        int cr = CM_Request_Device_EjectW(target, out veto, name, (uint)name.Capacity, 0);
        if (cr == 0 && (veto == 0 || veto == 13)) return null;
        if (cr == CR_ACCESS_DENIED || veto == 12) return AccessDenied;
        return VetoText(veto, name.ToString(), cr);
    }

    static string VetoText(int veto, string name, int cr)
    {
        string[] why =
        {
            "an unknown reason", "a legacy device", "a pending close", "an app", "a Windows service",
            "an app or service still using it", "another device", "a driver", "an invalid request",
            "insufficient power", "a device that can't be disabled", "a legacy driver", "insufficient rights",
        };
        string reason = veto > 0 && veto < why.Length ? why[veto] : (veto == 0 ? "error 0x" + cr.ToString("X") : "veto type " + veto);
        string who = string.IsNullOrEmpty(name) || name.StartsWith(@"\\?\") || name.StartsWith("PCI\\") ? "" : " (" + name + ")";
        return "Windows refused to release the eGPU because of " + reason + who + ". Close apps using the GPU and try again.";
    }

    static string GetString(uint dev, uint prop)
    {
        uint type, len = 1024;
        var buf = new byte[len];
        if (CM_Get_DevNode_Registry_PropertyW(dev, prop, out type, buf, ref len, 0) != 0) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
    }

    static bool GetDword(uint dev, uint prop, out uint value)
    {
        uint type, len = 4;
        var buf = new byte[4];
        value = 0;
        if (CM_Get_DevNode_Registry_PropertyW(dev, prop, out type, buf, ref len, 0) != 0) return false;
        value = BitConverter.ToUInt32(buf, 0);
        return true;
    }

    static bool GetPropDword(uint dev, Guid fmtid, uint pid, out uint value)
    {
        var key = new DevPropKey { fmtid = fmtid, pid = pid };
        uint type, size = 4;
        var buf = new byte[4];
        value = 0;
        if (CM_Get_DevNode_PropertyW(dev, ref key, out type, buf, ref size, 0) != 0 || size < 4) return false;
        value = BitConverter.ToUInt32(buf, 0);
        return true;
    }
}

// ---------------------------------------------------------------- WDDM kernel thunks (gdi32)

static class Kmt
{
    public struct PerfData { public uint TempC, FanRpm; }

    [StructLayout(LayoutKind.Sequential)]
    struct OpenFromDeviceName { public IntPtr DeviceName; public uint Adapter; public uint LuidLow; public int LuidHigh; }
    [StructLayout(LayoutKind.Sequential)]
    struct CloseAdapter { public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)]
    struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }

    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromDeviceName(ref OpenFromDeviceName p);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter p);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo p);

    const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    const int PerfDataSize = 64;   // D3DKMT_ADAPTER_PERFDATA

    // Opens the adapter briefly (so it never blocks removal), reads its LUID and perf data, closes it.
    public static bool Probe(string interfacePath, out long luid, out PerfData perf)
    {
        luid = 0;
        perf = new PerfData();
        var open = new OpenFromDeviceName { DeviceName = Marshal.StringToHGlobalUni(interfacePath) };
        try
        {
            if (D3DKMTOpenAdapterFromDeviceName(ref open) != 0) return false;
        }
        finally { Marshal.FreeHGlobal(open.DeviceName); }

        luid = ((long)open.LuidHigh << 32) | open.LuidLow;
        IntPtr buf = Marshal.AllocHGlobal(PerfDataSize);
        try
        {
            for (int i = 0; i < PerfDataSize; i++) Marshal.WriteByte(buf, i, 0);
            var q = new QueryAdapterInfo { Adapter = open.Adapter, Type = KMTQAITYPE_ADAPTERPERFDATA, Data = buf, Size = PerfDataSize };
            if (D3DKMTQueryAdapterInfo(ref q) == 0)
            {
                perf.FanRpm = (uint)Marshal.ReadInt32(buf, 48);
                uint deci = (uint)Marshal.ReadInt32(buf, 56);   // tenths of a degree Celsius
                perf.TempC = deci > 0 && deci < 2000 ? (uint)Math.Round(deci / 10.0) : 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            var close = new CloseAdapter { Adapter = open.Adapter };
            D3DKMTCloseAdapter(ref close);
        }
        return true;
    }
}

// ---------------------------------------------------------------- GPU performance counters (pdh)

class GpuCounters : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

    public readonly long Luid;
    public double Usage;
    public ulong DedicatedUsed;
    public List<uint> Pids = new List<uint>();

    IntPtr query, engine, adapterMem, processMem;

    public GpuCounters(long luid)
    {
        Luid = luid;
        string tag = string.Format("luid_0x{0:x8}_0x{1:x8}", (uint)(luid >> 32), (uint)luid);
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
        PdhAddEnglishCounterW(query, @"\GPU Engine(*" + tag + @"*)\Utilization Percentage", IntPtr.Zero, out engine);
        PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*" + tag + @"*)\Dedicated Usage", IntPtr.Zero, out adapterMem);
        PdhAddEnglishCounterW(query, @"\GPU Process Memory(*" + tag + @"*)\Dedicated Usage", IntPtr.Zero, out processMem);
        PdhCollectQueryData(query);   // rate counters need a first sample
    }

    public void Collect()
    {
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return;

        // Like Task Manager: sum every process per engine, report the busiest engine.
        var perEngine = new Dictionary<string, double>();
        foreach (var kv in Read(engine))
        {
            int at = kv.Key.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
            string key = at >= 0 ? kv.Key.Substring(at) : kv.Key;
            double v;
            perEngine.TryGetValue(key, out v);
            perEngine[key] = v + kv.Value;
        }
        Usage = perEngine.Count == 0 ? 0 : Math.Min(100, perEngine.Values.Max());

        DedicatedUsed = (ulong)Read(adapterMem).Sum(kv => kv.Value);

        var pids = new List<uint>();
        foreach (var kv in Read(processMem))
        {
            uint pid;
            if (kv.Value > 0 && kv.Key.StartsWith("pid_") && uint.TryParse(kv.Key.Substring(4).Split('_')[0], out pid))
                pids.Add(pid);
        }
        Pids = pids;
    }

    static List<KeyValuePair<string, double>> Read(IntPtr counter)
    {
        var list = new List<KeyValuePair<string, double>>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0, count;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero) != PDH_MORE_DATA)
            return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return list;
            int stride = IntPtr.Size + 16;   // PDH_FMT_COUNTERVALUE_ITEM_W: name, status (+pad), double
            for (int i = 0; i < count; i++)
            {
                IntPtr item = IntPtr.Add(buf, i * stride);
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                int status = Marshal.ReadInt32(item, IntPtr.Size);
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                if ((status == 0 || status == 1) && name != null) list.Add(new KeyValuePair<string, double>(name, value));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
    }
}

// ---------------------------------------------------------------- NVIDIA extras (NVML, optional)

static class Nvml
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlShutdown();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr dev);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint milliwatts);

    static bool preloaded;
    static IntPtr dev;

    static void Preload()
    {
        if (preloaded) return;
        preloaded = true;
        string sys = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (File.Exists(sys)) LoadLibraryW(sys);
    }

    public static bool Init()
    {
        Preload();
        try
        {
            if (nvmlInit_v2() != 0) return false;
            uint n;
            IntPtr d;
            if (nvmlDeviceGetCount_v2(out n) != 0 || n == 0 || nvmlDeviceGetHandleByIndex_v2(0, out d) != 0)
            {
                nvmlShutdown();
                return false;
            }
            dev = d;
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static void Shutdown()
    {
        try { nvmlShutdown(); } catch { }
        dev = IntPtr.Zero;
    }

    public static bool Query(out double watts, out uint tempC)
    {
        watts = -1;
        tempC = 0;
        uint v;
        if (nvmlDeviceGetPowerUsage(dev, out v) == 0) watts = v / 1000.0;
        if (nvmlDeviceGetTemperature(dev, 0, out v) == 0) tempC = v;
        return watts >= 0 || tempC > 0;
    }
}
