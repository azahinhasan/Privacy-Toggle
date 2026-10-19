using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

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

        var now = DateTime.UtcNow;
        _currentRange = new RecordedRange { Start = now, End = now };
        _history.Recorded.Add(_currentRange);

        _scanDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _scanDebounce.Tick += (_, _) => { _scanDebounce.Stop(); Scan(); };

        // Keeps "recorded until" fresh, catches anything the change watcher missed, and saves.
        _heartbeat = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Heartbeat(), dispatcher);

        Scan();
        _heartbeat.Start();

        _watcher = new Thread(WatchRegistry) { IsBackground = true, Name = "UsageTracker" };
        _watcher.Start();
    }

    public IReadOnlyList<UsageSession> Sessions => _history.Sessions;
    public IReadOnlyList<RecordedRange> Recorded => _history.Recorded;

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
                var session = _history.Sessions.FirstOrDefault(s => s.Device == device && s.AppKey == appKey && s.Start == startUtc);

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
                foreach (var old in _history.Sessions.Where(s => s.Device == device && s.AppKey == appKey && s.End == null && s.Start < startUtc))
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
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) set.Add(sb.ToString());
                }
                finally { CloseHandle(h); }
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
}
