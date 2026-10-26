using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

public enum ActivityRange { Hour1, Hours6, Hours24, Days7 }
public enum ActivityFilter { All, Microphone, Camera }
public enum ActivitySort { MostRecent, MostUsed, Name }

/// <summary>
/// A stretch of use as drawn: local times, clipped to the window, near-touching sessions merged.
/// <see cref="Uncertain"/>: the app was connected to the camera, but it can't be told whether it used it.
/// </summary>
public sealed record UsageSpan(DateTime Start, DateTime End, UsageDevice Device, bool Live, bool Uncertain = false)
{
    public TimeSpan Duration => End - Start;

    /// <summary>"in use" for a confirmed live span, "connected" for an uncertain one.</summary>
    public string LiveWord => Uncertain ? "connected" : "in use";
}

/// <summary>Everything the Activity page shows for one app within the selected window.</summary>
public sealed class AppActivity
{
    public required AppIdentity App { get; init; }
    public required List<UsageSpan> Spans { get; init; }   // in window, filtered, merged
    public required List<UsageSpan> Last24h { get; init; } // for the sparkline (filtered, merged)
    public TimeSpan Mic { get; init; }
    public TimeSpan Camera { get; init; }
    public DateTime LastActive { get; init; }
    public bool Live { get; init; }
    public TimeSpan Total => Mic + Camera;
}

public sealed record TodayStats(TimeSpan Mic, TimeSpan Camera, int Apps, AppIdentity? LastApp, DateTime? LastTime,
    UsageDevice? LastDevice, bool LastLive, bool LastUncertain);

/// <summary>Turns recorded sessions into what the Activity page draws. No UI code.</summary>
public static class ActivityModel
{
    /// <summary>Sessions closer than this are drawn as one bar.</summary>
    public static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(30);

    public static TimeSpan Length(ActivityRange range) => range switch
    {
        ActivityRange.Hour1 => TimeSpan.FromHours(1),
        ActivityRange.Hours6 => TimeSpan.FromHours(6),
        ActivityRange.Hours24 => TimeSpan.FromHours(24),
        _ => TimeSpan.FromDays(7),
    };

    public static string Label(ActivityRange range) => range switch
    {
        ActivityRange.Hour1 => "hour",
        ActivityRange.Hours6 => "6 hours",
        ActivityRange.Hours24 => "24 hours",
        _ => "7 days",
    };

    /// <summary>The smallest of 1h / 6h / 24h that contains all of today's activity.</summary>
    public static ActivityRange DefaultRange(IEnumerable<UsageSession> sessions, IReadOnlyList<BlockedRange> blocked, DateTime now)
    {
        var starts = Clip(sessions, blocked, now.Date, now).Select(s => s.Start).ToList();
        if (starts.Count == 0) return ActivityRange.Hours24;
        var span = now - starts.Min();
        return span <= TimeSpan.FromHours(1) ? ActivityRange.Hour1
             : span <= TimeSpan.FromHours(6) ? ActivityRange.Hours6
             : ActivityRange.Hours24;
    }

    public static List<AppActivity> Build(IEnumerable<UsageSession> sessions, IReadOnlyList<BlockedRange> blocked,
        DateTime from, DateTime now, ActivityFilter filter, ActivitySort sort)
    {
        var dayAgo = now.AddHours(-24);
        var result = new List<AppActivity>();
        foreach (var app in sessions.Where(s => Matches(s.Device, filter)).GroupBy(s => s.AppKey))
        {
            var spans = Merge(Clip(app, blocked, from, now));
            if (spans.Count == 0) continue;
            result.Add(new AppActivity
            {
                App = AppIdentity.For(app.Key),
                Spans = spans,
                Last24h = Merge(Clip(app, blocked, dayAgo, now)),
                Mic = Union(spans.Where(s => s.Device == UsageDevice.Microphone)),
                Camera = Union(spans.Where(s => s.Device == UsageDevice.Camera)),
                LastActive = spans.Max(s => s.End),
                Live = spans.Any(s => s.Live),
            });
        }

        return sort switch
        {
            ActivitySort.MostUsed => result.OrderByDescending(a => a.Live).ThenByDescending(a => a.Total).ToList(),
            ActivitySort.Name => result.OrderBy(a => a.App.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => result.OrderByDescending(a => a.Live).ThenByDescending(a => a.LastActive).ToList(),
        };
    }

    public static TodayStats Today(IReadOnlyList<UsageSession> sessions, IReadOnlyList<BlockedRange> blocked, DateTime now)
    {
        var spans = sessions
            .SelectMany(s => Clip(new[] { s }, blocked, now.Date, now).Select(sp => (s.AppKey, Span: sp)))
            .ToList();

        // Most recent real access (blocked time excluded): anything in use now wins (confirmed use before a
        // mere camera connection), then the latest end.
        var recent = sessions
            .SelectMany(s => Clip(new[] { s }, blocked, now - Length(ActivityRange.Days7), now).Select(sp => (s.AppKey, Span: sp)))
            .OrderByDescending(x => x.Span.Live && !x.Span.Uncertain)
            .ThenByDescending(x => x.Span.Live)
            .ThenByDescending(x => x.Span.End)
            .Select(x => ((string AppKey, UsageSpan Span)?)x)
            .FirstOrDefault();

        return new TodayStats(
            Union(spans.Where(x => x.Span.Device == UsageDevice.Microphone).Select(x => x.Span)),
            Union(spans.Where(x => x.Span.Device == UsageDevice.Camera).Select(x => x.Span)),
            spans.Select(x => x.AppKey).Distinct().Count(),
            recent == null ? null : AppIdentity.For(recent.Value.AppKey),
            recent?.Span.End,
            recent?.Span.Device,
            recent?.Span.Live == true,
            recent?.Span.Uncertain == true);
    }

    /// <summary>
    /// What is really in use right now: per app, its live spans (blocked time removed). An app holding a
    /// muted microphone isn't listed; once the mic is unmuted it is, timed from the unmute.
    /// </summary>
    public static List<(string AppKey, List<UsageSpan> Spans)> LiveNow(IReadOnlyList<UsageSession> sessions,
        IReadOnlyList<BlockedRange> blocked, DateTime now) =>
        sessions.Where(s => s.End == null)
            .GroupBy(s => s.AppKey)
            .Select(g => (g.Key, Spans: Clip(g, blocked, now - Length(ActivityRange.Days7), now).Where(s => s.Live).ToList()))
            .Where(x => x.Spans.Count > 0)
            .OrderBy(x => x.Spans.Min(s => s.Start))
            .ToList();

    public static bool Matches(UsageDevice device, ActivityFilter filter) =>
        filter == ActivityFilter.All ||
        (filter == ActivityFilter.Microphone) == (device == UsageDevice.Microphone);

    /// <summary>
    /// Sessions as local spans inside [from, now], minus the time their device was blocked
    /// (an app holding a muted mic or a disabled camera gets nothing, so it isn't using it).
    /// </summary>
    private static IEnumerable<UsageSpan> Clip(IEnumerable<UsageSession> sessions, IReadOnlyList<BlockedRange> blocked,
        DateTime from, DateTime now)
    {
        foreach (var s in sessions)
        {
            var start = s.Start.ToLocalTime();
            var end = s.End?.ToLocalTime() ?? now;
            if (end <= from || start >= now) continue;
            start = Later(start, from);
            if (end > now) end = now;

            // Cut out every blocked stretch of this device.
            var pieces = new List<(DateTime S, DateTime E)> { (start, end) };
            foreach (var b in blocked.Where(b => b.Device == s.Device))
            {
                var bs = b.Start.ToLocalTime();
                var be = b.End?.ToLocalTime() ?? now;
                if (be <= start || bs >= end) continue;
                pieces = pieces.SelectMany(p => Cut(p, bs, be)).ToList();
            }

            foreach (var (ps, pe) in pieces)
            {
                if (pe - ps < TimeSpan.FromSeconds(1)) continue;
                // Still live only if this piece runs up to now (not cut short by a block).
                yield return new UsageSpan(ps, pe, s.Device, s.End == null && pe >= now, s.Uncertain);
            }
        }
    }

    private static IEnumerable<(DateTime S, DateTime E)> Cut((DateTime S, DateTime E) p, DateTime bs, DateTime be)
    {
        if (be <= p.S || bs >= p.E) { yield return p; yield break; }
        if (bs > p.S) yield return (p.S, bs);
        if (be < p.E) yield return (be, p.E);
    }

    /// <summary>Per device, joins sessions that overlap or are less than <see cref="MergeGap"/> apart.</summary>
    public static List<UsageSpan> Merge(IEnumerable<UsageSpan> spans)
    {
        var merged = new List<UsageSpan>();
        foreach (var device in spans.GroupBy(s => s.Device))
        {
            UsageSpan? current = null;
            foreach (var s in device.OrderBy(s => s.Start))
            {
                if (current != null && s.Start - current.End <= MergeGap)
                {
                    // Uncertain only if every joined piece is: one confirmed session makes the bar real use.
                    current = current with
                    {
                        End = s.End > current.End ? s.End : current.End,
                        Live = current.Live || s.Live,
                        Uncertain = current.Uncertain && s.Uncertain,
                    };
                }
                else
                {
                    if (current != null) merged.Add(current);
                    current = s;
                }
            }
            if (current != null) merged.Add(current);
        }
        return merged.OrderBy(s => s.Start).ToList();
    }

    /// <summary>Time covered by at least one span (overlaps counted once).</summary>
    public static TimeSpan Union(IEnumerable<UsageSpan> spans)
    {
        var total = TimeSpan.Zero;
        DateTime? curStart = null, curEnd = null;
        foreach (var s in spans.OrderBy(s => s.Start))
        {
            if (curEnd == null || s.Start > curEnd)
            {
                if (curEnd != null) total += curEnd.Value - curStart!.Value;
                (curStart, curEnd) = (s.Start, s.End);
            }
            else if (s.End > curEnd)
            {
                curEnd = s.End;
            }
        }
        if (curEnd != null) total += curEnd.Value - curStart!.Value;
        return total;
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    // ---------------- formatting ----------------

    /// <summary>Compact duration for chips: "&lt;1m", "3m", "1h 20m".</summary>
    public static string Short(TimeSpan d) =>
        d.TotalMinutes < 1 ? "<1m"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m"
        : d.Minutes == 0 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalHours}h {d.Minutes}m";

    /// <summary>Readable duration: "under 1 min", "35 min", "1 h 20 min".</summary>
    public static string Long(TimeSpan d) =>
        d.TotalMinutes < 1 ? "under 1 min"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min"
        : d.Minutes == 0 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalHours} h {d.Minutes} min";

    /// <summary>Running clock for live use: "4:05", "1:02:33".</summary>
    public static string Clock(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

    public static string Ago(DateTime when, DateTime now)
    {
        var d = now - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (when.Date == now.Date.AddDays(-1)) return "yesterday";
        return $"{(int)Math.Ceiling(d.TotalDays)} days ago";
    }

    public static string Name(UsageDevice d) => d == UsageDevice.Camera ? "Camera" : "Microphone";
}
