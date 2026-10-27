using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PrivacyToggle.Helper;

/// <summary>
/// Lists the computer's real network adapters (Wi-Fi, Ethernet, mobile broadband) and enables/disables
/// them through SetupAPI, the way Device Manager and the Network Connections window do. A disabled
/// adapter carries no traffic at all, and stays disabled across reboots until turned back on.
/// Changing state needs admin rights, so that only runs inside the helper service.
/// Virtual adapters (VPN, Hyper-V, WSL, Wi-Fi Direct) and Bluetooth networking are never listed or touched.
/// </summary>
internal static class NetworkAdapters
{
    private static readonly Guid NetClass = new("4d36e972-e325-11ce-bfc1-08002be10318");

    public static List<NetworkAdapter> List()
    {
        var up = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (nic.OperationalStatus == OperationalStatus.Up) up.Add(nic.Id);
        }
        catch
        {
            // Connection state is a nice-to-have; the list itself doesn't depend on it.
        }

        var result = new List<NetworkAdapter>();
        Enumerate((_, _, info) =>
        {
            result.Add(new NetworkAdapter(info.Id, info.Name, info.Alias, info.Kind, info.Enabled,
                info.Enabled && up.Contains(info.InterfaceGuid)));
            return false;
        });
        return result.OrderBy(a => a.Kind).ThenBy(a => a.Alias).ToList();
    }

    /// <summary>Enables or disables one adapter. Returns an error message, or null on success.</summary>
    public static string? SetEnabled(string id, bool enable, out bool needsRestart)
    {
        bool restart = false;
        string? error = $"Network adapter not found: {id}";
        Enumerate((set, data, info) =>
        {
            if (!info.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return false;
            error = info.Enabled == enable ? null : ChangeState(set, data, enable, out restart);
            return true;
        });
        needsRestart = restart;
        return error;
    }

    private sealed record AdapterInfo(string Id, string Name, string Alias, NetworkKind Kind, bool Enabled, string InterfaceGuid);

    private delegate bool Visitor(IntPtr set, SP_DEVINFO_DATA data, AdapterInfo info);

    /// <summary>Walks present, physical network adapters; the visitor returns true to stop.</summary>
    private static void Enumerate(Visitor visit)
    {
        var guid = NetClass;
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE) return;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (ReadDriverKey(set, ref data) is not { } driver) continue;
                // Only hardware: NCF_PHYSICAL set, NCF_VIRTUAL / NCF_HIDDEN not (VPNs, Hyper-V, WAN miniports...).
                if ((driver.Characteristics & NCF_PHYSICAL) == 0 || (driver.Characteristics & (NCF_VIRTUAL | NCF_HIDDEN)) != 0) continue;
                if (driver.MediaType == MEDIA_BLUETOOTH) continue;

                string id = GetInstanceId(set, ref data);
                if (id.Length == 0) continue;
                string name = GetProperty(set, ref data, SPDRP_FRIENDLYNAME) ?? GetProperty(set, ref data, SPDRP_DEVICEDESC) ?? id;
                var kind = driver.MediaType switch
                {
                    MEDIA_WIFI => NetworkKind.WiFi,
                    MEDIA_WWAN => NetworkKind.Mobile,
                    _ => NetworkKind.Ethernet,
                };
                string alias = ConnectionName(driver.InterfaceGuid) ?? name;
                var info = new AdapterInfo(id, name, alias, kind, !IsDisabled(data.DevInst), driver.InterfaceGuid);
                if (visit(set, data, info)) return;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private sealed record DriverInfo(uint Characteristics, uint MediaType, string InterfaceGuid);

    /// <summary>The adapter's driver settings: what kind of adapter it is, and its interface GUID.</summary>
    private static DriverInfo? ReadDriverKey(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        IntPtr hkey = SetupDiOpenDevRegKey(set, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DRV, KEY_READ);
        if (hkey == INVALID_HANDLE_VALUE || hkey == IntPtr.Zero) return null;
        try
        {
            using var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, ownsHandle: true));
            return new DriverInfo(
                ToUInt(key.GetValue("Characteristics")),
                ToUInt(key.GetValue("*PhysicalMediaType")),
                key.GetValue("NetCfgInstanceId") as string ?? "");
        }
        catch
        {
            return null;
        }
    }

    private static uint ToUInt(object? value) => value switch
    {
        int i => unchecked((uint)i),
        string s when uint.TryParse(s, out var u) => u,
        _ => 0,
    };

    /// <summary>The name shown in Network Connections ("Wi-Fi", "Ethernet 2"), or null.</summary>
    private static string? ConnectionName(string interfaceGuid)
    {
        if (interfaceGuid.Length == 0) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Network\{{{NetClass}}}\{interfaceGuid}\Connection");
            return key?.GetValue("Name") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Enables or disables via the class installer (what Device Manager does); persists across reboots.</summary>
    private static string? ChangeState(IntPtr set, SP_DEVINFO_DATA data, bool enable, out bool needsRestart)
    {
        needsRestart = false;
        var p = new SP_PROPCHANGE_PARAMS
        {
            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
            {
                cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                InstallFunction = DIF_PROPERTYCHANGE,
            },
            StateChange = enable ? DICS_ENABLE : DICS_DISABLE,
            Scope = DICS_FLAG_GLOBAL,
            HwProfile = 0,
        };
        if (!SetupDiSetClassInstallParams(set, ref data, ref p, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
            return $"Couldn't prepare the network adapter change (error {Marshal.GetLastWin32Error()}).";
        if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data))
            return $"Windows couldn't turn the network adapter {(enable ? "on" : "off")} (error {Marshal.GetLastWin32Error()}).";

        var installParams = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>() };
        if (SetupDiGetDeviceInstallParams(set, ref data, ref installParams))
            needsRestart = (installParams.Flags & (DI_NEEDREBOOT | DI_NEEDRESTART)) != 0;
        return null;
    }

    private static bool IsDisabled(uint devInst) =>
        CM_Get_DevNode_Status(out _, out uint problem, devInst, 0) == 0 && problem == CM_PROB_DISABLED;

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

    // Characteristics flags (netcfgx.h) and NDIS_PHYSICAL_MEDIUM values (*PhysicalMediaType).
    private const uint NCF_VIRTUAL = 0x1, NCF_PHYSICAL = 0x4, NCF_HIDDEN = 0x8;
    private const uint MEDIA_WWAN = 8, MEDIA_WIFI = 9, MEDIA_BLUETOOTH = 10;

    private const uint DIGCF_PRESENT = 0x2;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_FRIENDLYNAME = 0xC;
    private const uint DIF_PROPERTYCHANGE = 0x12;
    private const uint DICS_ENABLE = 1, DICS_DISABLE = 2, DICS_FLAG_GLOBAL = 1;
    private const uint DIREG_DRV = 2, KEY_READ = 0x20019;
    private const uint DI_NEEDRESTART = 0x80, DI_NEEDREBOOT = 0x100;
    private const uint CM_PROB_DISABLED = 22;
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
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data, uint scope, uint hwProfile,
        uint keyType, uint samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, uint size);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
