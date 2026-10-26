using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using PrivacyToggle.Helper;

namespace PrivacyToggle.Services;

/// <summary>One app's network totals for a day. ActiveSeconds counts the time it was sending or receiving.</summary>
public sealed class AppTraffic
{
    public long Sent { get; set; }
    public long Received { get; set; }
    public int ActiveSeconds { get; set; }
    public long Total => Sent + Received;
}

/// <summary>An app transferring data right now, in bytes per second.</summary>
public sealed record LiveTraffic(string AppKey, double DownRate, double UpRate);

/// <summary>
/// Which apps use the network and how much. The helper service counts every byte each process sends and
/// receives (see <see cref="NetworkTraffic"/>); this reads those running totals every 2 seconds, turns the
/// differences into live speeds, and adds them up per app and day. History stays on this PC
/// (%AppData%\PrivacyToggle\network-usage.json) for 30 days.
/// App keys match the Activity page's: "np:C:\…\app.exe"; plus "sys:System" for Windows itself and
/// "svc:Windows Update, …" for Windows services sharing a svchost process.
/// </summary>
public sealed class NetworkUsageService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const int KeepDays = 30;

    private readonly string _filePath;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, (long Sent, long Received)> _last = new(); // per process serial
    private string? _session;
    private DateTime _lastPoll;
    private bool _polling, _dirty, _disposed;
    private DateTime _lastSave = DateTime.UtcNow;
    private Dictionary<string, Dictionary<string, AppTraffic>> _days = new(); // "yyyy-MM-dd" -> app key -> totals

    /// <summary>The helper is counting (it's installed, up to date, and could start the trace).</summary>
    public bool Available { get; private set; }

    /// <summary>Why it isn't available, for the Network page.</summary>
    public string? Problem { get; private set; }

    public IReadOnlyList<LiveTraffic> Live { get; private set; } = Array.Empty<LiveTraffic>();

    /// <summary>Raised on the UI thread after every poll.</summary>
    public event Action? Changed;

    public NetworkUsageService(Dispatcher dispatcher, string folder)
    {
        _filePath = Path.Combine(folder, "network-usage.json");
        Load();
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, async (_, _) => await PollAsync(), dispatcher);
        _ = PollAsync();
    }

    /// <summary>Totals per app over the last <paramref name="days"/> days (1 = today), biggest first.</summary>
    public List<(string AppKey, AppTraffic Traffic)> Totals(int days)
    {
        var sum = new Dictionary<string, AppTraffic>();
        for (int i = 0; i < days; i++)
        {
            if (!_days.TryGetValue(DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"), out var day)) continue;
            foreach (var (key, t) in day)
            {
                if (!sum.TryGetValue(key, out var total)) sum[key] = total = new AppTraffic();
                total.Sent += t.Sent;
                total.Received += t.Received;
                total.ActiveSeconds += t.ActiveSeconds;
            }
        }
        return sum.Select(s => (s.Key, s.Value)).OrderByDescending(s => s.Value.Total).ToList();
    }

    private async Task PollAsync()
    {
        if (_disposed || _polling) return;
        _polling = true;
        try
        {
            var response = await CameraService.SendAsync(new CameraRequest { Command = "net-usage" });
            var now = DateTime.UtcNow;
            if (response?.Traffic == null)
            {
                Available = false;
                Problem = response == null ? "The PrivacyToggle helper service isn't running. The installer sets it up."
                    : "The helper service is from an older version. Run the latest PrivacyToggle installer to update it.";
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
                Absorb(response.TrafficSession ?? "", response.Traffic, now);
            }
            _lastPoll = now;
            if (_dirty && now - _lastSave > TimeSpan.FromSeconds(30)) Save();
            Changed?.Invoke();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Turns the helper's running totals into per-app additions since the last poll.</summary>
    private void Absorb(string session, List<ProcessTraffic> traffic, DateTime now)
    {
        // First answer, or the helper restarted: its totals are a new starting point, not new traffic.
        bool baselineOnly = _session != session;
        if (baselineOnly) _last.Clear();
        _session = session;

        double seconds = _lastPoll == default ? Interval.TotalSeconds : Math.Max(0.5, (now - _lastPoll).TotalSeconds);
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

        Live = deltas.Select(d => new LiveTraffic(d.Key, d.Value.Received / seconds, d.Value.Sent / seconds))
            .OrderByDescending(l => l.DownRate + l.UpRate).ToList();
        if (deltas.Count == 0) return;

        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!_days.TryGetValue(today, out var day)) _days[today] = day = new Dictionary<string, AppTraffic>();
        foreach (var (key, (sent, received)) in deltas)
        {
            if (!day.TryGetValue(key, out var t)) day[key] = t = new AppTraffic();
            t.Sent += sent;
            t.Received += received;
            t.ActiveSeconds += (int)Math.Round(seconds);
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

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _days = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AppTraffic>>>(File.ReadAllText(_filePath)) ?? new();
        }
        catch
        {
            _days = new(); // unreadable: start over rather than fail
        }
        string oldest = DateTime.Today.AddDays(-(KeepDays - 1)).ToString("yyyy-MM-dd");
        foreach (var old in _days.Keys.Where(d => string.CompareOrdinal(d, oldest) < 0).ToList()) _days.Remove(old);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_days));
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
