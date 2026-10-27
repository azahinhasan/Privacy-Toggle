using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using PrivacyToggle.Helper;

namespace PrivacyToggle.Services;

/// <summary>One app's network totals for a period.</summary>
public sealed class AppTraffic
{
    public long Sent { get; set; }
    public long Received { get; set; }
    public long Total => Sent + Received;
}

/// <summary>An app transferring data right now, in bytes per second (smoothed over a few seconds).</summary>
public sealed record LiveTraffic(string AppKey, double DownRate, double UpRate);

/// <summary>A stretch of one app using the network: above <see cref="NetworkUsageService.SessionRate"/>, ending after a minute below it.</summary>
public sealed record NetworkSession(string AppKey, DateTime Start, DateTime End, long Sent, long Received, bool Live);

/// <summary>Bytes an app moved in a time slot.</summary>
public readonly record struct TrafficSlot(DateTime Start, TimeSpan Length, long Sent, long Received);

/// <summary>
/// Which apps use the network and how much. The helper service counts every byte each process sends and
/// receives (see <see cref="NetworkTraffic"/>); this reads those running totals every second and keeps:
/// <list type="bullet">
/// <item>live, smoothed rates per app (<see cref="Live"/>) and the last 6 minutes second by second, for the
/// Activity page's "Last 5 min" zone;</item>
/// <item>history in 30-second slots per app, rolled up to 5-minute slots after a day, kept 7 days like the
/// microphone and camera history (%AppData%\PrivacyToggle\network-history.json).</item>
/// </list>
/// App keys match the Activity page's: "np:C:\…\app.exe"; plus "sys:System" for Windows itself and
/// "svc:Windows Update, …" for Windows services sharing a svchost process.
/// </summary>
public sealed class NetworkUsageService : IDisposable
{
    /// <summary>A session runs while the app moves more than this many bytes per second (1 KB/s).</summary>
    public const double SessionRate = 1024;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Smoothing = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan SessionGap = TimeSpan.FromSeconds(60);
    private const int SlotSeconds = 30, RolledSlotSeconds = 300;
    private static readonly TimeSpan RollUpAfter = TimeSpan.FromHours(24), Keep = TimeSpan.FromDays(7);

    private readonly string _filePath;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, (long Sent, long Received)> _last = new(); // per process serial
    private readonly Dictionary<string, Queue<(DateTime Time, long Sent, long Received)>> _recent = new(); // nonzero seconds only
    private string? _session;
    private bool _polling, _dirty, _disposed;
    private DateTime _lastSave = DateTime.UtcNow;

    /// <summary>App key → slots: [start (unix seconds, UTC), length (s), sent, received], oldest first.</summary>
    private Dictionary<string, List<long[]>> _slots = new();

    /// <summary>The helper is counting (it's installed, up to date, and could start the trace).</summary>
    public bool Available { get; private set; }

    /// <summary>Why it isn't available.</summary>
    public string? Problem { get; private set; }

    /// <summary>Apps that moved any data in the last few seconds, fastest first.</summary>
    public IReadOnlyList<LiveTraffic> Live { get; private set; } = Array.Empty<LiveTraffic>();

    /// <summary>
    /// The whole PC's speed in bytes per second (the sum of <see cref="Live"/>), or null when the helper can't give
    /// exact per-app speeds. It's the data apps move, the same figure downloaders like Steam or IDM show; Windows'
    /// adapter counters read ~4–5% higher, as they also count the network's own packet headers.
    /// </summary>
    public (double Down, double Up)? LiveTotal { get; private set; }

    /// <summary>Raised on the UI thread after every poll (every second).</summary>
    public event Action? Changed;

    public NetworkUsageService(Dispatcher dispatcher, string folder)
    {
        _filePath = Path.Combine(folder, "network-history.json");
        Load();
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, async (_, _) => await PollAsync(), dispatcher);
        _ = PollAsync();
    }

    // ---------------- queries ----------------

    /// <summary>Totals per app over the last <paramref name="days"/> calendar days (1 = today), biggest first.</summary>
    public List<(string AppKey, AppTraffic Traffic)> Totals(int days) =>
        Totals(DateTime.Today.AddDays(-(days - 1)), DateTime.Now)
            .Select(t => (t.Key, t.Value)).OrderByDescending(t => t.Value.Total).ToList();

    /// <summary>Totals per app for slots that start inside [from, to] (local times).</summary>
    public Dictionary<string, AppTraffic> Totals(DateTime from, DateTime to)
    {
        var result = new Dictionary<string, AppTraffic>();
        foreach (var (key, slot) in AllSlots(from, to))
        {
            if (!result.TryGetValue(key, out var t)) result[key] = t = new AppTraffic();
            t.Sent += slot.Sent;
            t.Received += slot.Received;
        }
        return result;
    }

    /// <summary>One app's slots inside [from, to], oldest first.</summary>
    public IEnumerable<TrafficSlot> Slots(string appKey, DateTime from, DateTime to)
    {
        if (!_slots.TryGetValue(appKey, out var list)) yield break;
        long a = Unix(from), b = Unix(to);
        foreach (var s in list)
            if (s[0] + s[1] > a && s[0] <= b) yield return ToSlot(s);
    }

    /// <summary>
    /// One app's traffic second by second over the last few minutes (only seconds with traffic), for the live zone.
    /// </summary>
    public IReadOnlyList<(DateTime Time, long Sent, long Received)> Recent(string appKey) =>
        _recent.TryGetValue(appKey, out var q) ? q.ToList() : new();

    /// <summary>
    /// Network sessions overlapping [from, to], newest first: runs of slots above <see cref="SessionRate"/>, joined
    /// across gaps up to a minute. A session still going now is Live and ends now.
    /// </summary>
    public List<NetworkSession> Sessions(DateTime from, DateTime to)
    {
        var now = DateTime.Now;
        var liveApps = Live.Where(l => l.DownRate + l.UpRate > SessionRate).Select(l => l.AppKey).ToHashSet();
        var result = new List<NetworkSession>();
        foreach (var (key, list) in _slots)
        {
            DateTime? start = null, end = null;
            long sent = 0, received = 0;
            foreach (var raw in list)
            {
                var s = ToSlot(raw);
                if (s.Start + s.Length < from - SessionGap || s.Start > to) continue;
                if ((s.Sent + s.Received) / s.Length.TotalSeconds <= SessionRate) continue;
                if (start != null && s.Start - end!.Value > SessionGap)
                {
                    result.Add(new NetworkSession(key, start.Value, end.Value, sent, received, false));
                    (sent, received) = (0, 0);
                    start = null;
                }
                start ??= s.Start;
                end = s.Start + s.Length;
                sent += s.Sent;
                received += s.Received;
            }
            if (start == null) continue;
            bool live = liveApps.Contains(key) && now - end!.Value < SessionGap;
            result.Add(new NetworkSession(key, start.Value, live ? now : (end!.Value > now ? now : end.Value), sent, received, live));
        }
        return result.Where(s => s.End >= from && s.Start <= to).OrderByDescending(s => s.Start).ToList();
    }

    /// <summary>The app whose network session ended most recently (for "Last: Steam, 4 min ago (↓ 1.1 GB)").</summary>
    public NetworkSession? LastSession() =>
        Sessions(DateTime.Now - Keep, DateTime.Now).Where(s => !s.Live).OrderByDescending(s => s.End).FirstOrDefault();

    private IEnumerable<(string Key, TrafficSlot Slot)> AllSlots(DateTime from, DateTime to)
    {
        long a = Unix(from), b = Unix(to);
        foreach (var (key, list) in _slots)
            foreach (var s in list)
                if (s[0] >= a && s[0] <= b) yield return (key, ToSlot(s));
    }

    private static TrafficSlot ToSlot(long[] s) =>
        new(DateTimeOffset.FromUnixTimeSeconds(s[0]).LocalDateTime, TimeSpan.FromSeconds(s[1]), s[2], s[3]);

    private static long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    // ---------------- polling ----------------

    private async Task PollAsync()
    {
        if (_disposed || _polling) return;
        _polling = true;
        try
        {
            var response = await CameraService.SendAsync(new CameraRequest { Command = "net-usage" });
            var now = DateTime.Now;
            if (response?.Traffic == null)
            {
                Available = false;
                Problem = response == null ? "Per-app network use needs the PrivacyToggle helper service, which the installer sets up."
                    : "The helper service needs updating to count network use. Run the latest PrivacyToggle installer.";
                Live = Array.Empty<LiveTraffic>();
            }
            else if (response.TrafficError != null)
            {
                Available = false;
                Problem = response.TrafficError;
                Live = Array.Empty<LiveTraffic>();
            }
            else
            {
                Available = true;
                Problem = null;
                Absorb(response.TrafficSession ?? "", response.Traffic, now, response.TrafficRates == true);
            }
            LiveTotal = Available && response?.TrafficRates == true
                ? (Live.Sum(l => l.DownRate), Live.Sum(l => l.UpRate))
                : null;
            if (_dirty && DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(30)) Save();
            Changed?.Invoke();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Turns the helper's running totals into per-app additions since the last poll.</summary>
    private void Absorb(string session, List<ProcessTraffic> traffic, DateTime now, bool rates)
    {
        // First answer, or the helper restarted: its totals are a new starting point, not new traffic.
        bool baselineOnly = _session != session;
        if (baselineOnly) _last.Clear();
        _session = session;

        var deltas = new Dictionary<string, (long Sent, long Received)>();
        var seen = new HashSet<long>();
        foreach (var p in traffic)
        {
            seen.Add(p.Serial);
            // A process first seen now started (or first used the network) since the last poll: all of it is new.
            var before = _last.TryGetValue(p.Serial, out var b) ? b : (0, 0);
            _last[p.Serial] = (p.Sent, p.Received);
            if (baselineOnly) continue;
            long sent = Math.Max(0, p.Sent - before.Sent), received = Math.Max(0, p.Received - before.Received);
            if (sent == 0 && received == 0) continue;
            string key = AppKey(p);
            var d = deltas.GetValueOrDefault(key);
            deltas[key] = (d.Sent + sent, d.Received + received);
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);

        // Recent seconds (for smoothing and the live zone), trimmed to the window.
        foreach (var (key, (sent, received)) in deltas)
        {
            if (!_recent.TryGetValue(key, out var q)) _recent[key] = q = new Queue<(DateTime, long, long)>();
            q.Enqueue((now, sent, received));
        }
        foreach (var (key, q) in _recent.ToList())
        {
            while (q.Count > 0 && now - q.Peek().Time > RecentWindow) q.Dequeue();
            if (q.Count == 0) _recent.Remove(key);
        }

        if (rates)
        {
            // The helper's speeds come from the packets' own timestamps: steady while an app's rate is steady,
            // and 0 as soon as it stops.
            Live = traffic.Where(p => p.DownRate + p.UpRate > 0)
                .GroupBy(AppKey)
                .Select(g => new LiveTraffic(g.Key, g.Sum(p => p.DownRate), g.Sum(p => p.UpRate)))
                .OrderByDescending(l => l.DownRate + l.UpRate).ToList();
        }
        else
        {
            // An older helper: estimate from the bytes of the last few polls (jumpier, and slower to reach 0).
            double window = Smoothing.TotalSeconds;
            Live = _recent.Select(r =>
                {
                    var last = r.Value.Where(x => now - x.Time < Smoothing).ToList();
                    return new LiveTraffic(r.Key, last.Sum(x => x.Received) / window, last.Sum(x => x.Sent) / window);
                })
                .Where(l => l.DownRate + l.UpRate > 0)
                .OrderByDescending(l => l.DownRate + l.UpRate).ToList();
        }

        if (deltas.Count == 0) return;
        long slot = Unix(now) / SlotSeconds * SlotSeconds;
        foreach (var (key, (sent, received)) in deltas)
        {
            if (!_slots.TryGetValue(key, out var list)) _slots[key] = list = new List<long[]>();
            if (list.Count > 0 && list[^1][0] == slot) { list[^1][2] += sent; list[^1][3] += received; }
            else list.Add(new[] { slot, SlotSeconds, sent, received });
        }
        _dirty = true;
    }

    private static string AppKey(ProcessTraffic p)
    {
        if (p.App == "System") return "sys:System";
        if (p.App.EndsWith(@"\svchost.exe", StringComparison.OrdinalIgnoreCase))
            return p.Services is { Length: > 0 } services ? "svc:" + services : "sys:Windows services";
        return "np:" + p.App;
    }

    // ---------------- storage ----------------

    /// <summary>Rolls slots older than a day into 5-minute slots and drops anything older than 7 days.</summary>
    private void Compact()
    {
        long now = Unix(DateTime.Now);
        long rollBefore = now - (long)RollUpAfter.TotalSeconds, dropBefore = now - (long)Keep.TotalSeconds;
        foreach (var key in _slots.Keys.ToList())
        {
            var compacted = new List<long[]>();
            foreach (var s in _slots[key])
            {
                if (s[0] < dropBefore) continue;
                if (s[0] < rollBefore && s[1] < RolledSlotSeconds)
                {
                    long start = s[0] / RolledSlotSeconds * RolledSlotSeconds;
                    if (compacted.Count > 0 && compacted[^1][0] == start) { compacted[^1][2] += s[2]; compacted[^1][3] += s[3]; }
                    else compacted.Add(new[] { start, RolledSlotSeconds, s[2], s[3] });
                }
                else compacted.Add(s);
            }
            if (compacted.Count == 0) _slots.Remove(key);
            else _slots[key] = compacted;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _slots = JsonSerializer.Deserialize<Dictionary<string, List<long[]>>>(File.ReadAllText(_filePath)) ?? new();
        }
        catch
        {
            _slots = new(); // unreadable: start over rather than fail
        }
        Compact();
    }

    private void Save()
    {
        try
        {
            Compact();
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_slots));
            File.Move(temp, _filePath, overwrite: true);
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        catch
        {
            // Try again on the next save.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        if (_dirty) Save();
    }
}
