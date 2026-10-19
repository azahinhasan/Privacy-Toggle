using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

public enum ActivityRange { Hour1, Hours6, Hours24, Days7 }
public enum ActivityFilter { All, Microphone, Camera }
public enum ActivitySort { MostRecent, MostUsed, Name }

/// <summary>A stretch of use as drawn: local times, clipped to the window, near-touching sessions merged.</summary>
public sealed record UsageSpan(DateTime Start, DateTime End, UsageDevice Device, bool Live)
{
    public TimeSpan Duration => End - Start;
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
    UsageDevice? LastDevice, bool LastLive);

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
    public static ActivityRange DefaultRange(IEnumerable<UsageSession> sessions, DateTime now)
    {
        var today = now.Date;
        var starts = sessions
            .Where(s => (s.End?.ToLocalTime() ?? now) > today)
            .Select(s => Later(s.Start.ToLocalTime(), today))
            .ToList();
        if (starts.Count == 0) return ActivityRange.Hours24;
        var span = now - starts.Min();
        return span <= TimeSpan.FromHours(1) ? ActivityRange.Hour1
             : span <= TimeSpan.FromHours(6) ? ActivityRange.Hours6
             : ActivityRange.Hours24;
    }

    public static List<AppActivity> Build(IEnumerable<UsageSession> sessions, DateTime from, DateTime now,
        ActivityFilter filter, ActivitySort sort)
    {
        var dayAgo = now.AddHours(-24);
        var result = new List<AppActivity>();
        foreach (var app in sessions.Where(s => Matches(s.Device, filter)).GroupBy(s => s.AppKey))
        {
            var spans = Merge(Clip(app, from, now));
            if (spans.Count == 0) continue;
            result.Add(new AppActivity
            {
                App = AppIdentity.For(app.Key),
                Spans = spans,
                Last24h = Merge(Clip(app, dayAgo, now)),
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

    public static TodayStats Today(IReadOnlyList<UsageSession> sessions, DateTime now)
    {
        var today = now.Date;
        var spans = sessions.SelectMany(s => Clip(new[] { s }, today, now).Select(sp => (s.AppKey, Span: sp))).ToList();
        // Most recent access: anything in use now wins, then the latest end time.
        var last = sessions.OrderByDescending(s => s.End == null)
            .ThenByDescending(s => s.End ?? DateTime.MaxValue)
            .ThenByDescending(s => s.Start)
            .FirstOrDefault();
        DateTime? lastTime = last == null ? null : last.End == null ? now : last.End.Value.ToLocalTime();

        return new TodayStats(
            Union(spans.Where(x => x.Span.Device == UsageDevice.Microphone).Select(x => x.Span)),
            Union(spans.Where(x => x.Span.Device == UsageDevice.Camera).Select(x => x.Span)),
            spans.Select(x => x.AppKey).Distinct().Count(),
            last == null ? null : AppIdentity.For(last.AppKey),
            lastTime,
            last?.Device,
            last != null && last.End == null);
    }

    public static bool Matches(UsageDevice device, ActivityFilter filter) =>
        filter == ActivityFilter.All ||
        (filter == ActivityFilter.Microphone) == (device == UsageDevice.Microphone);

    private static IEnumerable<UsageSpan> Clip(IEnumerable<UsageSession> sessions, DateTime from, DateTime now)
    {
        foreach (var s in sessions)
        {
            var start = s.Start.ToLocalTime();
            var end = s.End?.ToLocalTime() ?? now;
            if (end <= from || start >= now) continue;
            yield return new UsageSpan(Later(start, from), end > now ? now : end, s.Device, s.End == null);
        }
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
                    current = current with { End = s.End > current.End ? s.End : current.End, Live = current.Live || s.Live };
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
