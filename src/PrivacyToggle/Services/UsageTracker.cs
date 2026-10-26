using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PrivacyToggle.Services;

public enum UsageDevice { Camera, Microphone }

/// <summary>One stretch of an app using the camera or microphone. <see cref="End"/> is null while in use.</summary>
public sealed class UsageSession
{
    public UsageDevice Device { get; set; }

    /// <summary>"np:C:\path\app.exe" for desktop apps, "pkg:Publisher.App_hash" for Store apps.</summary>
    public string AppKey { get; set; } = "";

    public DateTime Start { get; set; }   // UTC
    public DateTime? End { get; set; }    // UTC; null = in use now

    /// <summary>Seen as an active audio capture session rather than in Windows' usage records.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FromAudio { get; set; }

    /// <summary>
    /// Seen connecting to Windows' camera Frame Server rather than in Windows' usage records. Its end is
    /// approximate: Windows doesn't say when one app stops, only when the app exits or the camera goes idle.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FromCameraLog { get; set; }

    /// <summary>
    /// A camera-log session that couldn't be confirmed: the app is one that also reconnects on its own when
    /// others use the camera, and another app kept the camera busy, so "the camera is in use" didn't prove
    /// this app was using it. Shown as "connected", not "in use".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Uncertain { get; set; }
}

/// <summary>
/// A time range during which a device was blocked (every microphone muted / every camera off).
/// Apps may still hold the device then, but they get silence or nothing, so it doesn't count as use.
/// </summary>
public sealed class BlockedRange
{
    public UsageDevice Device { get; set; }
    public DateTime Start { get; set; }   // UTC
    public DateTime? End { get; set; }    // UTC; null = still blocked
}

/// <summary>A time range during which PrivacyToggle was running and recording.</summary>
public sealed class RecordedRange
{
    public DateTime Start { get; set; }   // UTC
    public DateTime End { get; set; }     // UTC
}

/// <summary>
/// Records which apps use the camera and microphone. Windows keeps only each app's most recent use
/// (CapabilityAccessManager\ConsentStore: LastUsedTimeStart / LastUsedTimeStop, stop = 0 while in use),
/// so this watches those records for changes and keeps every session itself, for 7 days.
/// Windows sometimes stops writing those records, so microphone use is also read straight from
/// the active audio capture sessions (polled every second), and camera use from Frame Server's log of
/// apps connecting to a camera (see <see cref="UsageSession.FromCameraLog"/>).
/// </summary>
public sealed class UsageTracker : IDisposable
{
    private const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dispatcher _dispatcher;
    private readonly string _filePath;
    private readonly DispatcherTimer _scanDebounce;
    private readonly DispatcherTimer _heartbeat;
    private readonly DispatcherTimer _audioPoll;
    private readonly MMDeviceEnumerator _audio = new();
    private readonly DispatcherTimer _cameraPoll;
    private readonly Dictionary<UsageSession, int> _cameraClients = new(); // open camera-log session -> process id
    private readonly Dictionary<string, int> _cameraHolders = new(); // app key -> process id of apps that open every camera that appears
    private readonly HashSet<string> _cameraReactors = new(); // app keys seen reacting at least once (kept after the camera idles)
    private DateTime _lastCameraArrival = DateTime.MinValue; // UTC; when a camera last appeared (log event 62, or turned back on)
    private readonly List<CameraConnect> _recentConnects = new(); // last minute of connections, to spot several at once
    private readonly Queue<CameraConnect> _pendingConnects = new(); // waiting GroupWindow before they're classified
    private readonly DispatcherTimer _connectTimer;
    private EventLogWatcher? _cameraLog;
    private bool _cameraChecking;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _watcher;
    private History _history = new();
    private RecordedRange _currentRange;
    private bool _dirty;

    /// <summary>Raised on the UI thread whenever the sessions change.</summary>
    public event Action? Changed;

    public UsageTracker(Dispatcher dispatcher, string folder)
    {
        _dispatcher = dispatcher;
        _filePath = Path.Combine(folder, "usage.json");
        Load();

        // A block still open from last time ended when PrivacyToggle stopped watching (unknown after that).
        var lastSeen = _history.Recorded.Count > 0 ? _history.Recorded.Max(r => r.End) : DateTime.UtcNow;
        foreach (var b in _history.Blocked.Where(b => b.End == null)) b.End = lastSeen;
        // Same for audio sessions: they're only known while PrivacyToggle watches them.
        foreach (var s in _history.Sessions.Where(s => (s.FromAudio || s.FromCameraLog) && s.End == null)) s.End = lastSeen;

        var now = DateTime.UtcNow;
        _currentRange = new RecordedRange { Start = now, End = now };
        _history.Recorded.Add(_currentRange);

        _scanDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _scanDebounce.Tick += (_, _) => { _scanDebounce.Stop(); Scan(); };

        // Keeps "recorded until" fresh, catches anything the change watcher missed, and saves.
        _heartbeat = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Heartbeat(), dispatcher);

        _audioPoll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ScanAudio(), dispatcher);
        _cameraPoll = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, async (_, _) => await CheckCameraClientsAsync(), dispatcher);
        _connectTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => ClassifyPending(), dispatcher);
        _connectTimer.Stop();

        Scan();
        ScanAudio();
        _heartbeat.Start();
        _audioPoll.Start();
        WatchCameraLog();

        _watcher = new Thread(WatchRegistry) { IsBackground = true, Name = "UsageTracker" };
        _watcher.Start();
    }

    /// <summary>
    /// Asks whether an app is using a camera right now (true/false), or null when that can't be told.
    /// Ends camera sessions seen in Frame Server's log once the camera goes idle.
    /// </summary>
    public Func<Task<bool?>>? CameraInUse { get; set; }

    public IReadOnlyList<UsageSession> Sessions => _history.Sessions;
    public IReadOnlyList<RecordedRange> Recorded => _history.Recorded;
    public IReadOnlyList<BlockedRange> Blocked => _history.Blocked;

    /// <summary>Records that a device became blocked (all muted / all off) or available again.</summary>
    public void SetBlocked(UsageDevice device, bool blocked)
    {
        var open = _history.Blocked.FirstOrDefault(b => b.Device == device && b.End == null);
        if (blocked == (open != null)) return;
        if (blocked) _history.Blocked.Add(new BlockedRange { Device = device, Start = DateTime.UtcNow });
        else open!.End = DateTime.UtcNow;
        // A camera turned back on reappears to every app, and they reconnect to it. Frame Server only logs that
        // (event 62) when it starts up itself, so note it here too.
        if (device == UsageDevice.Camera && !blocked) _lastCameraArrival = DateTime.UtcNow;
        _dirty = true;
        Save();
        Changed?.Invoke();
    }

    // ---------------- scanning ----------------

    private void Scan()
    {
        var now = DateTime.UtcNow;
        bool changed = false;
        HashSet<string>? running = null; // exe paths of running processes, loaded only if needed

        foreach (var device in new[] { UsageDevice.Camera, UsageDevice.Microphone })
        {
            foreach (var (appKey, start, stop) in ReadStore(device))
            {
                var startUtc = DateTime.FromFileTimeUtc(start);
                DateTime? endUtc = stop == 0 ? null : DateTime.FromFileTimeUtc(stop);
                var session = _history.Sessions.FirstOrDefault(s => !s.FromAudio && !s.FromCameraLog && s.Device == device && s.AppKey == appKey && s.Start == startUtc);

                if (endUtc == null)
                {
                    // Windows says "in use", but that record can be stale: an app that crashed never writes a stop time.
                    bool gone = false;
                    if (appKey.StartsWith("np:"))
                    {
                        running ??= RunningExePaths();
                        gone = !running.Contains(appKey[3..]);
                    }

                    if (session == null)
                    {
                        // Never seen live: if the app isn't running (or the record is over a day old), it's a stale
                        // record and when it really ended is unknown, so don't stretch it to now.
                        if (gone || now - startUtc > TimeSpan.FromDays(1)) endUtc = startUtc;
                    }
                    else if (session.End != null)
                    {
                        endUtc = session.End; // already closed earlier
                    }
                    else if (gone)
                    {
                        endUtc = now; // watched it live; its app has just gone away
                    }
                }

                // An older session of this app still marked open must have ended before this one began.
                foreach (var old in _history.Sessions.Where(s => !s.FromAudio && !s.FromCameraLog && s.Device == device && s.AppKey == appKey && s.End == null && s.Start < startUtc))
                {
                    old.End = startUtc;
                    changed = true;
                }

                if (session == null)
                {
                    _history.Sessions.Add(new UsageSession { Device = device, AppKey = appKey, Start = startUtc, End = endUtc });
                    changed = true;
                }
                else if (session.End != endUtc)
                {
                    session.End = endUtc;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            _dirty = true;
            Changed?.Invoke();
        }
    }

    /// <summary>Opens and closes microphone sessions from the apps that are recording audio right now.</summary>
    private void ScanAudio()
    {
        var recording = RecordingApps();
        if (recording == null) return; // couldn't read the sessions this time; keep what we have
        var now = DateTime.UtcNow;
        bool changed = false;

        var open = _history.Sessions.Where(s => s.FromAudio && s.End == null).ToList();
        foreach (var s in open.Where(s => !recording.Contains(s.AppKey)))
        {
            s.End = now;
            changed = true;
        }
        foreach (var appKey in recording.Where(k => !open.Any(s => s.AppKey == k)))
        {
            _history.Sessions.Add(new UsageSession { Device = UsageDevice.Microphone, AppKey = appKey, Start = now, FromAudio = true });
            changed = true;
        }

        if (changed)
        {
            _dirty = true;
            Changed?.Invoke();
        }
    }

    // ---------------- camera: Frame Server log ----------------

    // Frame Server's log: event 7 is written by each app (in its own process) when it connects to a camera;
    // event 62 by Frame Server when it has (re)read the list of cameras, e.g. after one was turned on or plugged in.
    private const string CameraLogName = "Microsoft-Windows-MF-FrameServer/Camera_FrameServer";
    private const string CameraLogQuery = "*[System[Provider[@Name='Microsoft-Windows-MF-FrameServer'] and (EventID=7 or EventID=62)]]";
    private const int CameraConnectEvent = 7, CameraArrivalEvent = 62;

    private readonly record struct CameraConnect(int Pid, DateTime Time, bool AfterArrival);

    /// <summary>
    /// Connections this soon after a camera appears are apps reacting to the new camera (browsers read its
    /// formats, virtual-camera apps grab it again), not a person starting to use it. A little slack before
    /// the arrival too: PrivacyToggle may notice a camera came back slightly after the apps did.
    /// </summary>
    private static readonly TimeSpan ArrivalProbeWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArrivalSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When one app opens the camera, others that watch it (browsers' video services, virtual-camera apps)
    /// reconnect within this much. Those followers are reactions; the app that went first is the real one,
    /// unless it's an app already known to react like that.
    /// </summary>
    private static readonly TimeSpan GroupWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Apps known to react (browsers' video services) also check the camera as they start; that isn't someone
    /// using it. Only for known reactors: a camera app opening the camera as it starts is real use.
    /// </summary>
    private static readonly TimeSpan LaunchProbeWindow = TimeSpan.FromSeconds(10);

    /// <summary>Picks up apps already connected, then follows the log for new ones.</summary>
    private void WatchCameraLog()
    {
        try
        {
            // Apps that connected in the last hour and are still running may still be using a camera;
            // the first idle check drops them if not. PrivacyToggle wasn't watching before, so count from now.
            var query = new EventLogQuery(CameraLogName, PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-MF-FrameServer'] and (EventID=7 or EventID=62) and TimeCreated[timediff(@SystemTime) <= 3600000]]]");
            var connects = new List<CameraConnect>();
            using (var reader = new EventLogReader(query))
                for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    using (record)
                    {
                        var time = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
                        if (record.Id == CameraArrivalEvent) _lastCameraArrival = time;
                        else if (record.ProcessId is int pid) connects.Add(new CameraConnect(pid, time, IsAfterArrival(time)));
                    }
            // In order, so apps become known reactors as they go. A process counts only if one of its
            // connections wasn't a reaction.
            var counted = new HashSet<int>();
            foreach (var c in connects)
            {
                if (IsReaction(c, connects)) OnCameraClient(c.Pid, DateTime.UtcNow, reaction: true);
                else counted.Add(c.Pid);
            }
            foreach (var pid in counted) OnCameraClient(pid, DateTime.UtcNow, reaction: false);

            _cameraLog = new EventLogWatcher(new EventLogQuery(CameraLogName, PathType.LogName, CameraLogQuery));
            _cameraLog.EventRecordWritten += (_, e) =>
            {
                using var record = e.EventRecord;
                if (record == null) return;
                var time = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
                if (record.Id == CameraArrivalEvent)
                    _dispatcher.BeginInvoke(() => _lastCameraArrival = time);
                else if (record.Id == CameraConnectEvent && record.ProcessId is int pid)
                    _dispatcher.BeginInvoke(() => QueueConnect(pid, time));
            };
            _cameraLog.Enabled = true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            // Log missing or unreadable; camera use then comes only from Windows' usage records.
        }
    }

    private bool IsAfterArrival(DateTime connectUtc) =>
        _lastCameraArrival != DateTime.MinValue && // none seen yet (and MinValue - slack would throw)
        connectUtc >= _lastCameraArrival - ArrivalSlack && connectUtc - _lastCameraArrival <= ArrivalProbeWindow;

    /// <summary>Holds a new connection for <see cref="GroupWindow"/>, so connections right after it can be seen too.</summary>
    private void QueueConnect(int pid, DateTime time)
    {
        var connect = new CameraConnect(pid, time, IsAfterArrival(time));
        _recentConnects.Add(connect);
        _recentConnects.RemoveAll(c => time - c.Time > TimeSpan.FromMinutes(1));
        _pendingConnects.Enqueue(connect);
        _connectTimer.Start();
    }

    private void ClassifyPending()
    {
        var ready = DateTime.UtcNow - GroupWindow;
        while (_pendingConnects.TryPeek(out var c) && c.Time <= ready)
        {
            _pendingConnects.Dequeue();
            // The arrival may have been noted after the connection arrived (PrivacyToggle saw the camera come back late).
            var connect = c with { AfterArrival = c.AfterArrival || IsAfterArrival(c.Time) };
            OnCameraClient(connect.Pid, connect.Time, IsReaction(connect, _recentConnects));
        }
        if (_pendingConnects.Count == 0) _connectTimer.Stop();
    }

    /// <summary>
    /// True when a connection is the system or an app reacting to something rather than a person starting
    /// to use the camera: right after a camera appeared, right after another process connected, or an app
    /// known to react that is just starting or that others then followed (they're reacting together).
    /// A lone connection counts, even from a known reactor: that's how a browser starts a call.
    /// </summary>
    private bool IsReaction(CameraConnect c, IReadOnlyCollection<CameraConnect> all)
    {
        if (c.AfterArrival) return true;
        if (all.Any(o => o.Pid != c.Pid && o.Time < c.Time && c.Time - o.Time <= GroupWindow)) return true;
        if (ExePath(c.Pid) is not { } path || !_cameraReactors.Contains("np:" + path)) return false;
        if (AppStartUtc(c.Pid) is { } started && c.Time >= started && c.Time - started <= LaunchProbeWindow) return true;
        return all.Any(o => o.Pid != c.Pid && o.Time >= c.Time && o.Time - c.Time <= GroupWindow);
    }

    private void OnCameraClient(int pid, DateTime startUtc, bool reaction)
    {
        if (pid == Environment.ProcessId || ExePath(pid) is not { } path) return; // gone already
        string appKey = "np:" + path;
        if (reaction)
        {
            // Not counted as use. It may keep the camera busy, though (virtual-camera apps hold it all the time),
            // which makes "the camera is in use" unreliable for other apps until the camera goes idle.
            _cameraHolders[appKey] = pid;
            _cameraReactors.Add(appKey);
            _cameraPoll.Start();
            return;
        }
        if (_cameraClients.Keys.Any(s => s.AppKey == appKey)) return; // already counted (apps reconnect often)

        var session = new UsageSession
        {
            Device = UsageDevice.Camera,
            AppKey = appKey,
            Start = startUtc > DateTime.UtcNow ? DateTime.UtcNow : startUtc,
            FromCameraLog = true,
        };
        _history.Sessions.Add(session);
        _cameraClients[session] = pid;
        _dirty = true;
        Changed?.Invoke();
        _cameraPoll.Start();
    }

    /// <summary>
    /// Ends camera-log sessions whose app exited, or all of them once no app uses a camera. While an app that
    /// grabbed the camera when it appeared is still running, the camera being in use doesn't prove anything
    /// about the others, so their sessions are marked uncertain.
    /// </summary>
    private async Task CheckCameraClientsAsync()
    {
        if (_cameraChecking) return;
        foreach (var gone in _cameraHolders.Where(h => !IsRunning(h.Value)).Select(h => h.Key).ToList())
            _cameraHolders.Remove(gone);
        if (_cameraClients.Count == 0 && _cameraHolders.Count == 0)
        {
            _cameraPoll.Stop();
            return;
        }
        _cameraChecking = true;
        try
        {
            bool? inUse = CameraInUse == null ? null : await CameraInUse();
            inUse ??= FrameServerRunning(); // helper unavailable: Frame Server stops when no app needs it

            // Idle: whatever the holders did when the camera appeared, none of them keeps it open.
            if (inUse == false) _cameraHolders.Clear();

            var now = DateTime.UtcNow;
            bool changed = false;
            var ended = _cameraClients.Where(c => inUse == false || !IsRunning(c.Value)).Select(c => c.Key).ToList();
            foreach (var session in ended)
            {
                session.End = now;
                _cameraClients.Remove(session);
                changed = true;
            }
            // Only apps known to react can't be told apart from their reactions; an app that only ever
            // connected on its own (a camera app, a call) is real use even while a holder keeps the camera busy.
            if (_cameraHolders.Count > 0)
                foreach (var session in _cameraClients.Keys.Where(s => !s.Uncertain && _cameraReactors.Contains(s.AppKey)))
                {
                    session.Uncertain = true;
                    changed = true;
                }
            if (changed)
            {
                _dirty = true;
                Changed?.Invoke();
            }
        }
        finally
        {
            _cameraChecking = false;
        }
    }

    private static bool FrameServerRunning()
    {
        try
        {
            using var service = new ServiceController("FrameServer");
            return service.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return true; // unknown: keep sessions until their app exits
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- microphone: audio sessions ----------------

    /// <summary>App keys ("np:" + exe path) with an active capture session on any microphone; null on failure.</summary>
    private HashSet<string>? RecordingApps()
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        try
        {
            foreach (var device in _audio.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession) continue;
                        int pid = (int)session.GetProcessID;
                        if (pid == 0 || pid == self) continue;
                        if (ExePath(pid) is { } path) apps.Add("np:" + path);
                    }
                }
            }
            return apps;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ExePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// When the app a process belongs to started: the process itself, or for multi-process apps (a browser's
    /// video helper) its topmost parent with the same exe. Null if that can't be read.
    /// </summary>
    private static DateTime? AppStartUtc(int pid)
    {
        string? path = ExePath(pid);
        DateTime? started = null;
        for (int depth = 0; depth < 8 && path != null; depth++)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) break;
            int parent;
            try
            {
                if (!GetProcessTimes(h, out long created, out _, out _, out _)) break;
                var createdUtc = DateTime.FromFileTimeUtc(created);
                if (started != null && createdUtc > started) break; // a reused id, not the real parent
                started = createdUtc;
                var info = new PROCESS_BASIC_INFORMATION();
                if (NtQueryInformationProcess(h, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0) break;
                parent = (int)info.InheritedFromUniqueProcessId;
            }
            finally { CloseHandle(h); }
            if (parent == 0 || !string.Equals(ExePath(parent), path, StringComparison.OrdinalIgnoreCase)) break;
            pid = parent;
        }
        return started;
    }

    private static IEnumerable<(string AppKey, long Start, long Stop)> ReadStore(UsageDevice device)
    {
        var result = new List<(string, long, long)>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{StoreKey}\{(device == UsageDevice.Camera ? "webcam" : "microphone")}");
            if (root == null) return result;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var np = root.OpenSubKey(name);
                    if (np == null) continue;
                    foreach (var app in np.GetSubKeyNames())
                        Read(np, app, "np:" + app.Replace('#', '\\'), result);
                }
                else
                {
                    Read(root, name, "pkg:" + name, result);
                }
            }
        }
        catch { }
        return result;
    }

    private static void Read(RegistryKey parent, string name, string appKey, List<(string, long, long)> into)
    {
        using var key = parent.OpenSubKey(name);
        if (key?.GetValue("LastUsedTimeStart") is long start && start > 0)
            into.Add((appKey, start, key.GetValue("LastUsedTimeStop") is long stop ? stop : 0));
    }

    private static HashSet<string> RunningExePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (ExePath(p.Id) is { } path) set.Add(path);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    private void Heartbeat()
    {
        _currentRange.End = DateTime.UtcNow;
        _dirty = true;
        Scan();
        Save();
    }

    // ---------------- change watching ----------------

    /// <summary>Waits for Windows to change either usage store, then schedules a scan (debounced).</summary>
    private void WatchRegistry()
    {
        using var cam = Registry.CurrentUser.OpenSubKey($@"{StoreKey}\webcam");
        using var mic = Registry.CurrentUser.OpenSubKey($@"{StoreKey}\microphone");
        var keys = new[] { cam, mic }.Where(k => k != null).Cast<RegistryKey>().ToList();
        if (keys.Count == 0) return; // heartbeat still scans every 30 s

        var events = keys.Select(_ => new AutoResetEvent(false)).ToList();
        try
        {
            while (true)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    RegNotifyChangeKeyValue(keys[i].Handle, true,
                        REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, events[i].SafeWaitHandle, true);
                }
                var handles = events.Cast<WaitHandle>().Append(_stop).ToArray();
                int signaled = WaitHandle.WaitAny(handles);
                if (signaled == handles.Length - 1) return;
                _dispatcher.BeginInvoke(() => { _scanDebounce.Stop(); _scanDebounce.Start(); });
            }
        }
        catch
        {
            // Watching failed; the 30 s heartbeat keeps the history going.
        }
        finally
        {
            foreach (var e in events) e.Dispose();
        }
    }

    // ---------------- persistence ----------------

    private sealed class History
    {
        public List<UsageSession> Sessions { get; set; } = new();
        public List<RecordedRange> Recorded { get; set; } = new();
        public List<BlockedRange> Blocked { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _history = JsonSerializer.Deserialize<History>(File.ReadAllText(_filePath), JsonOptions) ?? new History();
        }
        catch
        {
            _history = new History();
        }
        Prune();
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Retention;
        _history.Sessions.RemoveAll(s => s.End != null && s.End < cutoff);
        _history.Recorded.RemoveAll(r => r.End < cutoff);
        _history.Blocked.RemoveAll(b => b.End != null && b.End < cutoff);
    }

    private void Save()
    {
        if (!_dirty) return;
        try
        {
            Prune();
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_history, JsonOptions));
            File.Move(tmp, _filePath, overwrite: true);
            _dirty = false;
        }
        catch { }
    }

    public void Dispose()
    {
        _stop.Set();
        _heartbeat.Stop();
        _audioPoll.Stop();
        _audio.Dispose();
        _cameraPoll.Stop();
        _cameraLog?.Dispose();
        _scanDebounce.Stop();
        _currentRange.End = DateTime.UtcNow;
        _dirty = true;
        Save();
    }

    // ---------------- native ----------------

    private const int REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter,
        SafeWaitHandle evt, bool asynchronous);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info,
        int size, out int returnLength);
}
