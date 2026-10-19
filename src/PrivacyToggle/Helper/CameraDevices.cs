using System.Runtime.InteropServices;
using System.Text;

namespace PrivacyToggle.Helper;

/// <summary>
/// Lists camera devices and enables/disables them through SetupAPI (what Device Manager does).
/// Changing state needs admin rights, so it only runs inside the helper service.
/// Only Camera-class devices and UVC webcams in the Image class are ever touched.
/// </summary>
internal static class CameraDevices
{
    private static readonly Guid CameraClass = new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");
    private static readonly Guid ImageClass = new("6bdd1fc6-810f-11d0-bec7-08002be2092f");

    public static List<CameraDevice> List()
    {
        var result = new List<CameraDevice>();
        Enumerate((_, _, device) => { result.Add(device); return false; });
        return result.OrderBy(d => d.Name).ToList();
    }

    /// <summary>Enables or disables one camera. Returns an error message, or null on success.</summary>
    public static string? SetEnabled(string id, bool enable, out bool needsRestart)
    {
        bool restart = false;
        string? error = $"Camera not found: {id}";
        Enumerate((set, data, device) =>
        {
            if (!device.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return false;
            if (enable)
                // Always run: enabling also clears a disable that Windows queued for the next reboot.
                error = Enable(set, data, out restart);
            else
                error = device.Enabled ? Disable(data.DevInst, device.Id) : null;
            return true;
        });
        needsRestart = restart;
        return error;
    }

    /// <summary>
    /// Repairs cameras that an earlier disable left "disabled at next boot" because a program vetoed it:
    /// such a camera still runs but apps can't open it until reboot. Returns how many were repaired.
    /// </summary>
    public static int RepairPendingDisables()
    {
        int repaired = 0;
        Enumerate((set, data, device) =>
        {
            if (device.Enabled && (GetConfigFlags(set, ref data) & CONFIGFLAG_DISABLED) != 0 &&
                Enable(set, data, out _) == null)
                repaired++;
            return false;
        });
        return repaired;
    }

    /// <summary>
    /// Disables through the PnP manager. Unlike a class-installer property change, this fails cleanly
    /// when a program holding the camera vetoes it, instead of queueing the disable for the next reboot.
    /// </summary>
    private static string? Disable(uint devInst, string id)
    {
        uint cr = CM_Disable_DevNode(devInst, CM_DISABLE_UI_NOT_OK | CM_DISABLE_PERSIST);
        if (cr == CR_SUCCESS) return null;
        if (cr == CR_REMOVE_VETOED)
        {
            string? blocker = FindVetoingApp(id);
            return blocker != null
                ? $"{blocker} is using the camera and won't let Windows turn it off. Close it and try again."
                : "Another program is using the camera and won't let Windows turn it off. Close it and try again.";
        }
        return $"Windows couldn't turn the camera off (error 0x{cr:X}).";
    }

    /// <summary>Kernel-PnP logs which process blocked a removal (event 225); read the newest one for this camera.</summary>
    private static string? FindVetoingApp(string id)
    {
        try
        {
            const string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and EventID=225 and TimeCreated[timediff(@SystemTime) <= 15000]]]";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                    new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName, query)
                    { ReverseDirection = true });
                for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        string text = record.FormatDescription() ?? "";
                        if (!text.Contains(id, StringComparison.OrdinalIgnoreCase)) continue;
                        var match = System.Text.RegularExpressions.Regex.Match(text, @"application (.+?) with process id");
                        if (match.Success) return System.IO.Path.GetFileName(match.Groups[1].Value.Trim());
                    }
                }
                Thread.Sleep(200); // the event can land just after the veto
            }
        }
        catch { }
        return null;
    }

    private delegate bool Visitor(IntPtr set, SP_DEVINFO_DATA data, CameraDevice device);

    /// <summary>Walks present camera devices; the visitor returns true to stop.</summary>
    private static void Enumerate(Visitor visit)
    {
        foreach (var classGuid in new[] { CameraClass, ImageClass })
        {
            var guid = classGuid;
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT);
            if (set == INVALID_HANDLE_VALUE) continue;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    // Image class also holds scanners; only take UVC webcams from it.
                    if (classGuid == ImageClass &&
                        !string.Equals(GetProperty(set, ref data, SPDRP_SERVICE), "usbvideo", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string id = GetInstanceId(set, ref data);
                    if (id.Length == 0) continue;
                    string name = GetProperty(set, ref data, SPDRP_FRIENDLYNAME) ?? GetProperty(set, ref data, SPDRP_DEVICEDESC) ?? id;
                    bool enabled = !IsDisabled(data.DevInst);
                    if (visit(set, data, new CameraDevice(id, name, enabled))) return;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
    }

    /// <summary>Enables via the class installer (what Device Manager does); also clears a queued disable.</summary>
    private static string? Enable(IntPtr set, SP_DEVINFO_DATA data, out bool needsRestart)
    {
        needsRestart = false;
        var p = new SP_PROPCHANGE_PARAMS
        {
            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
            {
                cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                InstallFunction = DIF_PROPERTYCHANGE,
            },
            StateChange = DICS_ENABLE,
            Scope = DICS_FLAG_GLOBAL,
            HwProfile = 0,
        };
        if (!SetupDiSetClassInstallParams(set, ref data, ref p, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
            return $"SetupDiSetClassInstallParams failed ({Marshal.GetLastWin32Error()})";
        if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data))
            return $"Changing the camera state failed ({Marshal.GetLastWin32Error()})";

        var installParams = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>() };
        if (SetupDiGetDeviceInstallParams(set, ref data, ref installParams))
            needsRestart = (installParams.Flags & (DI_NEEDREBOOT | DI_NEEDRESTART)) != 0;
        return null;
    }

    private static bool IsDisabled(uint devInst) =>
        CM_Get_DevNode_Status(out _, out uint problem, devInst, 0) == 0 && problem == CM_PROB_DISABLED;

    private static uint GetConfigFlags(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new byte[4];
        return SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_CONFIGFLAGS, out _, buffer, 4, out _)
            ? BitConverter.ToUInt32(buffer, 0) : 0;
    }

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : "";
    }

    private static string? GetProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out uint size) || size < 2)
            return null;
        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }

    // ---------------- SetupAPI / CfgMgr ----------------

    private const uint DIGCF_PRESENT = 0x2;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_SERVICE = 0x4, SPDRP_CONFIGFLAGS = 0xA, SPDRP_FRIENDLYNAME = 0xC;
    private const uint CONFIGFLAG_DISABLED = 0x1;
    private const uint DIF_PROPERTYCHANGE = 0x12;
    private const uint DICS_ENABLE = 1, DICS_FLAG_GLOBAL = 1;
    private const uint DI_NEEDRESTART = 0x80, DI_NEEDREBOOT = 0x100;
    private const uint CM_PROB_DISABLED = 22;
    private const uint CM_DISABLE_UI_NOT_OK = 0x4, CM_DISABLE_PERSIST = 0x8;
    private const uint CR_SUCCESS = 0, CR_REMOVE_VETOED = 0x17;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS
    {
        public uint cbSize;
        public uint Flags;
        public uint FlagsEx;
        public IntPtr hwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public UIntPtr ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DriverPath;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        out uint regType, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, uint size);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint devInst, uint flags);
}
