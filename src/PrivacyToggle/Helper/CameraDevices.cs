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

    /// <summary>
    /// Enables or disables one camera. Returns an error message, or null on success. When a program
    /// holds the camera open and refuses, <paramref name="blocker"/> says which one.
    /// </summary>
    public static string? SetEnabled(string id, bool enable, out bool needsRestart, out BlockerInfo? blocker)
    {
        bool restart = false;
        BlockerInfo? found = null;
        string? error = $"Camera not found: {id}";
        Enumerate((set, data, device) =>
        {
            if (!device.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return false;
            if (enable)
            {
                // A running camera only needs a queued disable cleared (restarting it would need a reboot
                // while a program holds it); a disabled one is switched back on.
                if (device.Enabled) { ClearQueuedDisable(set, ref data); error = null; }
                else error = Enable(set, data, out restart);
            }
            else if (device.Enabled)
            {
                // A refused attempt also leaves Windows' camera service unable to open the camera until it
                // restarts, so don't even try while the program that refused last time is still running.
                found = Blockers.StillBlocking(device.Id);
                error = found != null ? BlockedMessage(found) : Disable(set, ref data, device.Id, out found);
            }
            else
            {
                error = null;
            }
            return true;
        });
        needsRestart = restart;
        blocker = found;
        return error;
    }

    public static string BlockedMessage(BlockerInfo b) =>
        $"{b.Name} is holding the camera open, so Windows can't turn it off.";

    /// <summary>Clears any camera left "disabled at next boot" by an earlier refused attempt. Returns how many.</summary>
    public static int RepairPendingDisables()
    {
        int repaired = 0;
        Enumerate((set, data, device) =>
        {
            if (device.Enabled && ClearQueuedDisable(set, ref data)) repaired++;
            return false;
        });
        return repaired;
    }

    /// <summary>
    /// Disables through the PnP manager, which fails cleanly when a program holding the camera refuses.
    /// It's done without "persist" first: persisting up front would leave the camera queued as
    /// "disabled at next boot" whenever a program refuses.
    /// </summary>
    private static string? Disable(IntPtr set, ref SP_DEVINFO_DATA data, string id, out BlockerInfo? blocker)
    {
        blocker = null;
        uint cr = CM_Disable_DevNode(data.DevInst, CM_DISABLE_UI_NOT_OK);
        if (cr == CR_SUCCESS)
        {
            // Stopped; now make it stick across reboots (the flag Device Manager sets for "disabled").
            uint flags = GetConfigFlags(set, ref data) | CONFIGFLAG_DISABLED;
            var buffer = BitConverter.GetBytes(flags);
            SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_CONFIGFLAGS, buffer, (uint)buffer.Length);
            return null;
        }

        ClearQueuedDisable(set, ref data); // belt and braces: never leave a queued disable behind
        if (cr == CR_REMOVE_VETOED)
        {
            blocker = Blockers.FromLatestVeto(id);
            return blocker != null ? BlockedMessage(blocker) : "Another program is holding the camera open, so Windows can't turn it off.";
        }
        return $"Windows couldn't turn the camera off (error 0x{cr:X}).";
    }

    /// <summary>Removes a queued "disabled at next boot" flag without restarting the device.</summary>
    private static bool ClearQueuedDisable(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        uint flags = GetConfigFlags(set, ref data);
        if ((flags & CONFIGFLAG_DISABLED) == 0) return false;
        var buffer = BitConverter.GetBytes(flags & ~CONFIGFLAG_DISABLED);
        return SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_CONFIGFLAGS, buffer, (uint)buffer.Length);
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
    private const uint CM_DISABLE_UI_NOT_OK = 0x4;
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
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        byte[] buffer, uint size);

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
