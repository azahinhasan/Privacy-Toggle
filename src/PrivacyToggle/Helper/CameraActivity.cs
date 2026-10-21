using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace PrivacyToggle.Helper;

/// <summary>
/// Tells whether an app is using a camera through Windows' camera Frame Server right now. Apps don't
/// open the camera themselves: Frame Server opens it for them and closes it when the last one is done,
/// so "Frame Server holds an open handle to a camera's device object" means "some app is using it".
/// Reading another process's handles needs the helper's SYSTEM rights. Programs that open the camera
/// directly (some virtual-camera services keep it open all the time) are deliberately not counted.
/// </summary>
internal static class CameraActivity
{
    // A few handles (synchronous pipes) block forever when asked for their name; give up on those.
    private static readonly TimeSpan NameTimeout = TimeSpan.FromMilliseconds(100);

    public static bool InUse()
    {
        var devices = CameraDevices.DeviceObjectNames();
        if (devices.Count == 0) return false;
        int frameServer = FrameServerProcessId();
        if (frameServer == 0) return false; // not running: nobody is using a camera through it

        IntPtr process = OpenProcess(PROCESS_DUP_HANDLE, false, frameServer);
        if (process == IntPtr.Zero) return false;
        var typeNames = new Dictionary<ushort, string>();
        try
        {
            foreach (var entry in Handles().Where(h => h.Pid == frameServer))
            {
                if (typeNames.TryGetValue(entry.Type, out var knownType) && knownType != "File") continue;
                if (!DuplicateHandle(process, entry.Handle, GetCurrentProcess(), out var handle, 0, false, DUPLICATE_SAME_ACCESS)) continue;
                try
                {
                    if (!typeNames.TryGetValue(entry.Type, out var type))
                        typeNames[entry.Type] = type = QueryName(handle, ObjectTypeInformation) ?? "";
                    if (type != "File") continue;

                    string? name = NameWithTimeout(handle);
                    if (name != null && devices.Any(d => name.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                finally { CloseHandle(handle); }
            }
            return false;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>Process id of the FrameServer service, or 0 when it isn't running.</summary>
    private static int FrameServerProcessId()
    {
        try
        {
            using var service = new ServiceController("FrameServer");
            if (service.Status != ServiceControllerStatus.Running) return 0;
            using var handle = service.ServiceHandle;
            return QueryServiceStatusEx(handle, SC_STATUS_PROCESS_INFO, out var status, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _)
                ? (int)status.dwProcessId : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string? NameWithTimeout(IntPtr handle)
    {
        string? name = null;
        var worker = new Thread(() => name = QueryName(handle, ObjectNameInformation)) { IsBackground = true };
        worker.Start();
        return worker.Join(NameTimeout) ? name : null;
    }

    private readonly record struct HandleEntry(int Pid, IntPtr Handle, ushort Type);

    /// <summary>Every open handle in the system (SystemExtendedHandleInformation).</summary>
    private static List<HandleEntry> Handles()
    {
        int size = 4 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + (1 << 20));
                    continue;
                }
                if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation failed (0x{status:X8})");

                // SYSTEM_HANDLE_INFORMATION_EX: count, reserved, then 40-byte entries
                // (object, pid, handle, access, backtrace index, type index, attributes, reserved).
                long count = Marshal.ReadInt64(buffer);
                var list = new List<HandleEntry>((int)count);
                IntPtr p = buffer + 16;
                for (long i = 0; i < count; i++, p += 40)
                    list.Add(new HandleEntry((int)Marshal.ReadInt64(p + 8), Marshal.ReadIntPtr(p + 16), (ushort)Marshal.ReadInt16(p + 30)));
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    /// <summary>Object name or type name (both start with a UNICODE_STRING).</summary>
    private static string? QueryName(IntPtr handle, int infoClass)
    {
        const int size = 2048;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryObject(handle, infoClass, buffer, size, out _) != 0) return null;
            int bytes = (ushort)Marshal.ReadInt16(buffer);
            IntPtr text = Marshal.ReadIntPtr(buffer + 8);
            return bytes == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---------------- native ----------------

    private const int SystemExtendedHandleInformation = 0x40;
    private const int ObjectNameInformation = 1, ObjectTypeInformation = 2;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint PROCESS_DUP_HANDLE = 0x40, DUPLICATE_SAME_ACCESS = 0x2;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const int SC_STATUS_PROCESS_INFO = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode,
            dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeHandle service, int infoLevel, out SERVICE_STATUS_PROCESS status,
        int size, out int needed);
}
