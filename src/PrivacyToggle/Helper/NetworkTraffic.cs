using System.Runtime.InteropServices;
using System.Text;

namespace PrivacyToggle.Helper;

/// <summary>
/// Counts the bytes every process sends and receives over the network, from Windows' own
/// Microsoft-Windows-Kernel-Network event tracing (the source Resource Monitor uses). Every TCP and UDP
/// send/receive is reported with the owning process and its size, so the counts are exact, not guessed.
/// Loopback traffic (an app talking to itself or another app on this PC) isn't counted.
/// A real-time trace session needs admin rights, so this runs inside the helper service.
/// </summary>
internal static class NetworkTraffic
{
    private const string SessionName = "PrivacyToggle-NetworkUsage";
    private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private const ulong KeywordIPv4 = 0x10, KeywordIPv6 = 0x20;

    // Event ids: TCP and UDP data sent / received, IPv4 and IPv6. Payload starts with PID, size, then addresses.
    private static readonly HashSet<ushort> SendIds = new() { 10, 26, 42, 58 };
    private static readonly HashSet<ushort> ReceiveIds = new() { 11, 27, 43, 59 };
    private static readonly HashSet<ushort> IPv6Ids = new() { 26, 27, 58, 59 };

    /// <summary>Changes every time the helper starts counting, so a client knows the counters started over.</summary>
    public static string SessionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Why counting couldn't start, if it couldn't.</summary>
    public static string? Error { get; private set; }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Counter> ByPid = new();
    private static long _nextSerial;
    private static ulong _session;
    private static ulong _trace = InvalidTrace;
    private static Thread? _thread;
    private static EventRecordCallback? _callback; // kept alive: native code calls it
    private static IntPtr _loggerName;

    private sealed class Counter
    {
        public long Serial;
        public int Pid;
        public string App = "";
        public string? Services;
        public long Sent, Received;
        public DateTime? GoneSince; // the process exited; dropped a minute later

        // Bytes per quarter second of event time (ring of RateSlots), for steady, exact speeds.
        public readonly long[] SlotOf = new long[RateSlots], SentIn = new long[RateSlots], ReceivedIn = new long[RateSlots];
    }

    /// <summary>Speeds are counted in quarter seconds of the packets' own timestamps.</summary>
    private const long SlotTicks = TimeSpan.TicksPerSecond / 4;

    /// <summary>Quarter seconds of history kept per process (the averaging window plus delivery delay, with room).</summary>
    private const int RateSlots = 32;

    /// <summary>Speeds average the last 3 seconds: steady for a steady download, without hiding changes for long.</summary>
    private const int AverageSlots = 12;

    /// <summary>
    /// Windows is asked to hand over its buffered events every <see cref="FlushInterval"/>, so everything older than
    /// half a second has arrived; speeds use the quarter seconds before that. (Without asking, events arrived up to a
    /// second late, and speeds had to wait 2 seconds, which put them noticeably behind the taskbar's.)
    /// </summary>
    private const int SettleSlots = 2;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static Timer? _flushTimer;

    /// <summary>The trace session's name: the helper's own, or another so a diagnostic run doesn't stop the helper's.</summary>
    private static string _sessionName = SessionName;

    public static void Start(string? sessionName = null)
    {
        try
        {
            _sessionName = sessionName ?? SessionName;
            StopSession(); // one left over from a crash would block starting a new one
            var props = NewProperties();
            try
            {
                uint rc = StartTrace(out _session, _sessionName, props);
                if (rc != 0) { Error = $"Couldn't start network counting (error {rc})."; return; }
            }
            finally { Marshal.FreeHGlobal(props); }

            var provider = KernelNetwork;
            uint enable = EnableTraceEx2(_session, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION,
                KeywordIPv4 | KeywordIPv6, 0, 0, IntPtr.Zero);
            if (enable != 0) { Error = $"Couldn't turn on network counting (error {enable})."; StopSession(); return; }

            _callback = OnEvent;
            _loggerName = Marshal.StringToHGlobalUni(_sessionName);
            var logfile = new EVENT_TRACE_LOGFILE
            {
                LoggerName = _loggerName,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
            };
            _trace = OpenTrace(ref logfile);
            if (_trace == InvalidTrace) { Error = $"Couldn't read network counting (error {Marshal.GetLastWin32Error()})."; StopSession(); return; }

            _thread = new Thread(() => ProcessTrace(new[] { _trace }, 1, IntPtr.Zero, IntPtr.Zero))
            {
                IsBackground = true,
                Name = "NetworkTraffic",
            };
            _thread.Start();
            _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
            Error = null;
        }
        catch (Exception ex)
        {
            Error = "Couldn't start network counting: " + ex.Message;
        }
    }

    public static void Stop()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        if (_trace != InvalidTrace) CloseTrace(_trace);
        _trace = InvalidTrace;
        StopSession();
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
        _loggerName = IntPtr.Zero;
    }

    /// <summary>
    /// Every process seen sending or receiving since the helper started, with its running totals.
    /// Processes that exited are reported for another minute, then dropped (their totals are final by then).
    /// </summary>
    public static List<ProcessTraffic> Snapshot()
    {
        List<Counter> counters;
        lock (Gate) counters = ByPid.Values.ToList();

        var now = DateTime.UtcNow;
        Dictionary<int, string>? services = null;
        foreach (var c in counters)
        {
            if (c.GoneSince == null && !ProcessInfo.IsRunning(c.Pid)) c.GoneSince = now;
            if (c.Services == null && c.App.EndsWith(@"\svchost.exe", StringComparison.OrdinalIgnoreCase))
            {
                services ??= ProcessInfo.ServicesByPid();
                c.Services = services.GetValueOrDefault(c.Pid) ?? "";
            }
        }
        lock (Gate)
            foreach (var gone in counters.Where(c => c.GoneSince is { } t && now - t > TimeSpan.FromMinutes(1)))
                if (ByPid.TryGetValue(gone.Pid, out var current) && current == gone) ByPid.Remove(gone.Pid);

        long slot = now.ToFileTimeUtc() / SlotTicks;
        return counters.Select(c =>
        {
            var (down, up) = Rates(c, slot);
            return new ProcessTraffic(c.Serial, c.Pid, c.App, string.IsNullOrEmpty(c.Services) ? null : c.Services,
                Interlocked.Read(ref c.Sent), Interlocked.Read(ref c.Received), down, up);
        }).ToList();
    }

    /// <summary>
    /// Bytes per second down and up, from the event timestamps: the average of the last 3 settled seconds, so a
    /// download held at 30 Mbps reads 30 rather than jumping with how the events were batched. The moment a whole
    /// settled second has no traffic the speed is 0, so a stopped download doesn't linger.
    /// </summary>
    private static (double Down, double Up) Rates(Counter c, long now)
    {
        lock (c)
        {
            long newest = now - SettleSlots;
            long In(long[] bytes, long s) => c.SlotOf[(int)(s % RateSlots)] == s ? bytes[(int)(s % RateSlots)] : 0;
            long lastSecond = 0;
            for (long s = newest - 3; s <= newest; s++) lastSecond += In(c.ReceivedIn, s) + In(c.SentIn, s);
            if (lastSecond == 0) return (0, 0);
            long down = 0, up = 0;
            for (long s = newest - AverageSlots + 1; s <= newest; s++)
            {
                down += In(c.ReceivedIn, s);
                up += In(c.SentIn, s);
            }
            double seconds = AverageSlots * SlotTicks / (double)TimeSpan.TicksPerSecond;
            return (down / seconds, up / seconds);
        }
    }

    /// <summary>Has Windows hand over the events it has buffered so far, so speeds are current.</summary>
    private static void Flush()
    {
        ulong session = _session;
        if (session == 0) return;
        var props = NewProperties();
        try { ControlTrace(session, null, props, EVENT_TRACE_CONTROL_FLUSH); }
        catch { }
        finally { Marshal.FreeHGlobal(props); }
    }

    private static void OnEvent(IntPtr record)
    {
        try
        {
            ushort id = (ushort)Marshal.ReadInt16(record, 40);   // EVENT_HEADER.EventDescriptor.Id
            bool send = SendIds.Contains(id);
            if (!send && !ReceiveIds.Contains(id)) return;
            int length = (ushort)Marshal.ReadInt16(record, 86);  // EVENT_RECORD.UserDataLength
            IntPtr data = Marshal.ReadIntPtr(record, 96);        // EVENT_RECORD.UserData
            bool v6 = IPv6Ids.Contains(id);
            int addressSize = v6 ? 16 : 4;
            if (data == IntPtr.Zero || length < 8 + 2 * addressSize) return;

            // Destination and source address follow PID and size; either being loopback means local traffic.
            if (IsLoopback(data + 8, v6) || IsLoopback(data + 8 + addressSize, v6)) return;
            int pid = Marshal.ReadInt32(data, 0);
            long size = (uint)Marshal.ReadInt32(data, 4);

            Counter? counter;
            lock (Gate)
            {
                // A pid whose process exited may be reused by a new process: start a new counter for it.
                if (!ByPid.TryGetValue(pid, out counter) || counter.GoneSince != null)
                {
                    counter = new Counter { Serial = ++_nextSerial, Pid = pid, App = ProcessInfo.AppPath(pid) };
                    ByPid[pid] = counter;
                }
            }
            if (send) Interlocked.Add(ref counter.Sent, size);
            else Interlocked.Add(ref counter.Received, size);

            // File the bytes under the quarter second the packet was logged (EVENT_HEADER.TimeStamp, FILETIME), not
            // when the batch happened to reach us: that's what keeps the speed steady.
            long quarter = Marshal.ReadInt64(record, 16) / SlotTicks;
            int slot = (int)(quarter % RateSlots);
            lock (counter)
            {
                if (counter.SlotOf[slot] != quarter)
                {
                    counter.SlotOf[slot] = quarter;
                    counter.SentIn[slot] = counter.ReceivedIn[slot] = 0;
                }
                if (send) counter.SentIn[slot] += size;
                else counter.ReceivedIn[slot] += size;
            }
        }
        catch
        {
            // Never let an exception escape into the native trace thread.
        }
    }

    private static bool IsLoopback(IntPtr address, bool v6)
    {
        if (!v6) return Marshal.ReadByte(address) == 127;
        for (int i = 0; i < 15; i++)
            if (Marshal.ReadByte(address, i) != 0) return false;
        return Marshal.ReadByte(address, 15) == 1; // ::1
    }

    private static void StopSession()
    {
        var props = NewProperties();
        try { ControlTrace(0, _sessionName, props, EVENT_TRACE_CONTROL_STOP); }
        finally { Marshal.FreeHGlobal(props); }
        _session = 0;
    }

    /// <summary>EVENT_TRACE_PROPERTIES for a real-time session, followed by room for the session name.</summary>
    private static IntPtr NewProperties()
    {
        const int size = PropertiesSize + 1024;
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);                        // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                          // Wnode.ClientContext: QPC (ProcessTrace hands us FILETIME)
        Marshal.WriteInt32(p, 44, WNODE_FLAG_TRACED_GUID);     // Wnode.Flags
        // Roomy buffers (KB each, and how many): a fast download logs thousands of events a second, and small
        // default buffers delay or drop them, which made speeds lag and read low.
        Marshal.WriteInt32(p, 48, 256);                        // BufferSize
        Marshal.WriteInt32(p, 52, 16);                         // MinimumBuffers
        Marshal.WriteInt32(p, 56, 64);                         // MaximumBuffers
        Marshal.WriteInt32(p, 64, EVENT_TRACE_REAL_TIME_MODE); // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                          // FlushTimer: deliver at least every second
        Marshal.WriteInt32(p, 116, PropertiesSize);            // LoggerNameOffset
        return p;
    }

    // ---------------- ETW ----------------

    private const int PropertiesSize = 120; // sizeof(EVENT_TRACE_PROPERTIES) on x64
    private const int WNODE_FLAG_TRACED_GUID = 0x20000;
    private const int EVENT_TRACE_REAL_TIME_MODE = 0x100;
    private const uint EVENT_TRACE_CONTROL_STOP = 1, EVENT_TRACE_CONTROL_FLUSH = 3;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_INFORMATION = 4;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ulong InvalidTrace = ulong.MaxValue;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    /// <summary>EVENT_TRACE_LOGFILEW on x64 (448 bytes); only the fields set here are named.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EVENT_TRACE_LOGFILE
    {
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);
}

/// <summary>Process details the helper (running as SYSTEM) can read for any process.</summary>
internal static class ProcessInfo
{
    /// <summary>The process's exe path; "System" for the kernel and processes that can't be read.</summary>
    public static string AppPath(int pid)
    {
        if (pid is 0 or 4) return "System";
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "System";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "System";
        }
        finally { CloseHandle(h); }
    }

    public static bool IsRunning(int pid)
    {
        if (pid is 0 or 4) return true;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out uint code) && code == STILL_ACTIVE; }
        finally { CloseHandle(h); }
    }

    /// <summary>Running services by hosting process: pid → "Windows Update, Background Intelligent Transfer Service".</summary>
    public static Dictionary<int, string> ServicesByPid()
    {
        var result = new Dictionary<int, List<string>>();
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return new();
        try
        {
            uint resume = 0;
            EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            IntPtr buffer = Marshal.AllocHGlobal((int)needed + 4096);
            try
            {
                resume = 0;
                if (!EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buffer, needed + 4096,
                        out _, out uint count, ref resume, null))
                    return new();
                int entrySize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                for (int i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + i * entrySize);
                    string name = Marshal.PtrToStringUni(entry.lpDisplayName) ?? Marshal.PtrToStringUni(entry.lpServiceName) ?? "";
                    int pid = (int)entry.ServiceStatusProcess.dwProcessId;
                    if (pid == 0 || name.Length == 0) continue;
                    if (!result.TryGetValue(pid, out var names)) result[pid] = names = new List<string>();
                    names.Add(name);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseServiceHandle(scm); }
        return result.ToDictionary(r => r.Key, r => string.Join(", ", r.Value.OrderBy(n => n)));
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x4, SC_ENUM_PROCESS_INFO = 0, SERVICE_WIN32 = 0x30, SERVICE_ACTIVE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode,
            dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusEx(IntPtr scm, uint infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
