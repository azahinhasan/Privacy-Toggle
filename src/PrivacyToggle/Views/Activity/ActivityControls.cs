using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>Small vector 🎤 / 📷 icons (WPF can't draw colour emoji), coloured from the theme.</summary>
public static class Glyphs
{
    private static readonly Geometry MicBody = Geometry.Parse("M9,4 a3,3 0 0 1 6,0 v7 a3,3 0 0 1 -6,0 z");
    private static readonly Geometry MicStand = Geometry.Parse("M5.5,11 a6.5,6.5 0 0 0 13,0 M12,17.5 v3.5 M8.5,21 h7");
    private static readonly Geometry CamBody = Geometry.Parse("M4.5,6.5 h8 a2.5,2.5 0 0 1 2.5,2.5 v6 a2.5,2.5 0 0 1 -2.5,2.5 h-8 a2.5,2.5 0 0 1 -2.5,-2.5 v-6 a2.5,2.5 0 0 1 2.5,-2.5 z");
    private static readonly Geometry CamLens = Geometry.Parse("M16,10.5 L22,7 V17 L16,13.5 Z");

    public static string BrushKey(UsageDevice d) => d == UsageDevice.Camera ? "CamUsage" : "MicUsage";

    public static FrameworkElement Device(UsageDevice device, double size, string? brushKey = null)
    {
        brushKey ??= BrushKey(device);
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (device == UsageDevice.Microphone)
        {
            canvas.Children.Add(Filled(MicBody, brushKey));
            var stand = new Path { Data = MicStand, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            stand.SetResourceReference(Shape.StrokeProperty, brushKey);
            canvas.Children.Add(stand);
        }
        else
        {
            canvas.Children.Add(Filled(CamBody, brushKey));
            canvas.Children.Add(Filled(CamLens, brushKey));
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    private static readonly Geometry Globe = Geometry.Parse(
        "M3.5,12 a8.5,8.5 0 1 0 17,0 a8.5,8.5 0 1 0 -17,0 M8.5,12 a3.5,8.5 0 1 0 7,0 a3.5,8.5 0 1 0 -7,0 M3.5,12 h17");

    /// <summary>Globe outline for the network, in <paramref name="brushKey"/> (network teal by default).</summary>
    public static FrameworkElement Network(double size, string brushKey = "NetUsage")
    {
        var path = new Path { Data = Globe, StrokeThickness = 2.2 };
        path.SetResourceReference(Shape.StrokeProperty, brushKey);
        var canvas = new Canvas { Width = 24, Height = 24, Children = { path } };
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Path Filled(Geometry g, string brushKey)
    {
        var p = new Path { Data = g };
        p.SetResourceReference(Shape.FillProperty, brushKey);
        return p;
    }
}


/// <summary>
/// The History timeline's time mapping. The selected range minus its last five minutes fills the left 80%
/// (the history area); the last five minutes fill the right 20% (the "Last 5 min" live zone), so what's
/// happening now is always readable however long the range is.
/// </summary>
public static class SplitScale
{
    public static readonly TimeSpan LiveSpan = TimeSpan.FromMinutes(5);
    public const double HistoryShare = 0.8;

    public static DateTime ZoneStart(DateTime now) => now - LiveSpan;

    public static double X(DateTime t, DateTime from, DateTime now, double width)
    {
        double w = Math.Max(0, width - 2); // 2px inset so the right edge stays visible
        var zone = ZoneStart(now);
        if (t <= zone)
        {
            double f = (t - from).TotalSeconds / Math.Max(1, (zone - from).TotalSeconds);
            return Math.Clamp(f, 0, 1) * w * HistoryShare;
        }
        double g = (t - zone).TotalSeconds / LiveSpan.TotalSeconds;
        return (HistoryShare + Math.Clamp(g, 0, 1) * (1 - HistoryShare)) * w;
    }

    public static double Divider(double width) => Math.Max(0, width - 2) * HistoryShare;

    /// <summary>Tick times inside the history area; midnight ticks are flagged (date label).</summary>
    public static IEnumerable<(DateTime Time, bool Midnight)> Ticks(ActivityRange range, DateTime from, DateTime now)
    {
        var to = ZoneStart(now);
        if (range == ActivityRange.Days7)
        {
            for (var d = from.Date.AddDays(1); d < to; d = d.AddDays(1)) yield return (d, true);
            yield break;
        }
        var step = range switch
        {
            ActivityRange.Hour1 => TimeSpan.FromMinutes(10),
            ActivityRange.Hours6 => TimeSpan.FromHours(1),
            _ => TimeSpan.FromHours(3),
        };
        var t = from.Date;
        while (t <= from) t += step;
        for (; t < to; t += step) yield return (t, t.TimeOfDay == TimeSpan.Zero);
    }

    public static string Label(DateTime t, ActivityRange range, bool midnight)
    {
        if (midnight || range == ActivityRange.Days7) return t.ToString("ddd d", CultureInfo.CurrentCulture);
        bool h24 = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        if (h24) return t.ToString("HH:mm", CultureInfo.CurrentCulture);
        return t.ToString(t.Minute == 0 ? "h tt" : "h:mm", CultureInfo.CurrentCulture);
    }

    public static FormattedText Text(Visual v, string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);
}

/// <summary>Base for the drawn timeline parts: shared window and theme-brush lookup, redraw on theme change.</summary>
public abstract class TimeElement : FrameworkElement
{
    protected DateTime From, Now;
    protected ActivityRange Range;

    protected TimeElement()
    {
        Loaded += (_, _) => { ThemeService.Changed -= InvalidateVisual; ThemeService.Changed += InvalidateVisual; };
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
    }

    public void SetWindow(DateTime from, DateTime now, ActivityRange range)
    {
        (From, Now, Range) = (from, now, range);
        InvalidateVisual();
    }

    protected Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected static Brush Faded(Brush b, double opacity)
    {
        var c = b.Clone();
        c.Opacity = opacity;
        c.Freeze();
        return c;
    }

    protected double X(DateTime t) => SplitScale.X(t, From, Now, ActualWidth);
}

/// <summary>Time labels over the history area; "Last 5 min" and a NOW badge over the live zone.</summary>
public sealed class HistoryAxis : TimeElement
{
    protected override Size MeasureOverride(Size available) => new(0, 24);

    protected override void OnRender(DrawingContext dc)
    {
        if (Now <= From) return;
        double divider = SplitScale.Divider(ActualWidth);

        var now = SplitScale.Text(this, "NOW", 10, Res("AccentText"), bold: true);
        var tag = new Rect(ActualWidth - now.Width - 12, 1, now.Width + 10, 18);
        dc.DrawRoundedRectangle(Res("Accent"), null, tag, 4, 4);
        dc.DrawText(now, new Point(tag.X + 5, tag.Y + (tag.Height - now.Height) / 2));

        var zone = SplitScale.Text(this, "Last 5 min", 11, Res("AccentSoft"), bold: true);
        if (divider + 6 + zone.Width < tag.X - 4) dc.DrawText(zone, new Point(divider + 6, 3));

        var secondary = Res("TextSecondary");
        double lastRight = double.NegativeInfinity;
        foreach (var (t, midnight) in SplitScale.Ticks(Range, From, Now))
        {
            // The current day's date label is bold, like the design.
            bool strong = midnight && t.Date == Now.Date;
            var label = SplitScale.Text(this, SplitScale.Label(t, Range, midnight), 11, strong ? Res("TextPrimary") : secondary, bold: strong);
            double x = X(t);
            double left = Range == ActivityRange.Days7 ? x + 4 : x - label.Width / 2;
            if (left < 0 || left < lastRight + 8 || left + label.Width > divider - 6) continue;
            dc.DrawText(label, new Point(left, 3));
            lastRight = left + label.Width;
        }
    }
}

/// <summary>Behind the rows: gridlines in the history area, the shaded live zone and its dashed orange divider.</summary>
public sealed class HistoryBackdrop : TimeElement
{
    public HistoryBackdrop() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        if (Now <= From) return;
        double divider = SplitScale.Divider(ActualWidth);
        dc.DrawRectangle(Res("LiveZoneBg"), null, new Rect(divider, 0, Math.Max(0, ActualWidth - divider), ActualHeight));

        var grid = new Pen(Faded(Res("TextSecondary"), 0.18), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        var midnight = new Pen(Faded(Res("TextSecondary"), 0.5), 1.5);
        foreach (var (t, isMidnight) in SplitScale.Ticks(Range, From, Now))
        {
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(isMidnight && Range != ActivityRange.Days7 ? midnight : grid, new Point(x, 0), new Point(x, ActualHeight));
        }
        var dashed = new Pen(Res("Accent"), 2) { DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0) };
        dc.DrawLine(dashed, new Point(divider, 0), new Point(divider, ActualHeight));
    }
}

/// <summary>Which lanes a row shows, top to bottom: Camera, Microphone, Network.</summary>
public enum Lane { Camera, Microphone, Network }

/// <summary>
/// One app's lanes on the timeline. Camera and microphone sessions are rounded bars (at least 6px wide so short
/// ones stay visible); the network lane is a bar histogram of the app's data rate, with the live zone drawn
/// from its last few minutes second by second. Hover a bar for details.
/// </summary>
public sealed class ActivityTrack : TimeElement
{
    public const double BarLane = 9, NetLane = 20, Gap = 6, Pad = 4, MinBar = 6;

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse };
    private List<UsageSpan> _spans = new();
    private double[] _history = Array.Empty<double>(), _live = Array.Empty<double>(); // network rate per bucket, 0..1
    private List<Lane> _lanes = new();
    private string _app = "";
    private UsageSpan? _hovered;

    public ActivityTrack()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        _tip.Opened += (_, _) => { if (_hovered == null) _tip.IsOpen = false; };
        _tip.SetResourceReference(StyleProperty, "ThemedToolTip");
    }

    public IReadOnlyList<Lane> Lanes => _lanes;

    /// <summary>Height for a set of lanes.</summary>
    public static double HeightFor(IReadOnlyList<Lane> lanes) =>
        2 * Pad + lanes.Sum(l => l == Lane.Network ? NetLane : BarLane) + Gap * Math.Max(0, lanes.Count - 1);

    /// <summary>Vertical centre of a lane (for the pulsing "live" dot drawn over the track).</summary>
    public double LaneCenter(Lane lane)
    {
        double y = Pad;
        foreach (var l in _lanes)
        {
            double h = l == Lane.Network ? NetLane : BarLane;
            if (l == lane) return y + h / 2;
            y += h + Gap;
        }
        return ActualHeight / 2;
    }

    private double LaneTop(Lane lane) => LaneCenter(lane) - (lane == Lane.Network ? NetLane : BarLane) / 2;

    public void SetData(string app, List<Lane> lanes, List<UsageSpan> spans, double[] history, double[] live)
    {
        (_app, _lanes, _spans, _history, _live) = (app, lanes, spans, history, live);
        Height = HeightFor(lanes);
        InvalidateVisual();
    }

    public void SetLive(double[] live)
    {
        _live = live;
        InvalidateVisual();
    }

    private (double X1, double X2) Bar(UsageSpan s)
    {
        double x1 = X(s.Start), x2 = X(s.End);
        if (x2 - x1 < MinBar)
        {
            x2 = x1 + MinBar;
            if (x2 > ActualWidth) { x2 = ActualWidth; x1 = x2 - MinBar; }
        }
        return (x1, x2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // whole row area is hoverable
        if (Now <= From) return;
        double w = Math.Max(0, ActualWidth - 2), divider = SplitScale.Divider(ActualWidth);
        var track = Res("TrackBg");
        foreach (var lane in _lanes)
        {
            double top = LaneTop(lane);
            if (lane == Lane.Network)
            {
                var fill = Res("NetUsage");
                var empty = Faded(fill, 0.22);
                Histogram(dc, _history, 0, divider, top, fill, empty);
                Histogram(dc, _live, divider, w, top, fill, empty);
                continue;
            }
            dc.DrawRoundedRectangle(track, null, new Rect(0, top, w, BarLane), 4, 4);
            var device = lane == Lane.Camera ? UsageDevice.Camera : UsageDevice.Microphone;
            var brush = Res(Glyphs.BrushKey(device));
            var highlight = new Pen(Res("TextPrimary"), 1.5);
            foreach (var s in _spans.Where(s => s.Device == device))
            {
                var (x1, x2) = Bar(s);
                dc.DrawRoundedRectangle(brush, ReferenceEquals(s, _hovered) ? highlight : null, new Rect(x1, top, x2 - x1, BarLane), 4, 4);
            }
        }
    }

    /// <summary>Bars for buckets spread across [x1, x2]; empty buckets show as a faint stub.</summary>
    private static void Histogram(DrawingContext dc, double[] values, double x1, double x2, double top, Brush fill, Brush empty)
    {
        if (values.Length == 0 || x2 <= x1) return;
        double step = (x2 - x1) / values.Length, bar = Math.Max(1.5, step - 1.5);
        for (int i = 0; i < values.Length; i++)
        {
            double v = Math.Clamp(values[i], 0, 1);
            double h = v > 0 ? Math.Max(3, v * NetLane) : 2.5;
            dc.DrawRoundedRectangle(v > 0 ? fill : empty, null, new Rect(x1 + i * step + 0.75, top + NetLane - h, bar, h), 1, 1);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        UsageSpan? hit = null;
        foreach (var s in _spans)
        {
            var lane = s.Device == UsageDevice.Camera ? Lane.Camera : Lane.Microphone;
            if (!_lanes.Contains(lane)) continue;
            double top = LaneTop(lane);
            var (x1, x2) = Bar(s);
            if (p.X >= x1 - 3 && p.X <= x2 + 3 && p.Y >= top - 4 && p.Y <= top + BarLane + 4) hit = s;
        }
        if (!ReferenceEquals(hit, _hovered))
        {
            _hovered = hit;
            InvalidateVisual();
        }
        if (hit != null)
        {
            _tip.Content = Describe(_app, hit);
            _tip.IsOpen = true;
        }
        else
        {
            _tip.IsOpen = false;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    public static string Describe(string app, UsageSpan s)
    {
        string range = s.Live ? $"since {s.Start:t} · {s.LiveWord} now" : $"{s.Start:t} – {s.End:t}";
        if (s.Start.Date != DateTime.Today) range = $"{s.Start:ddd} " + range;
        return $"{app} · {ActivityModel.Name(s.Device)}\n{range} · {ActivityModel.Long(s.Duration)}";
    }
}

/// <summary>
/// A small microphone level meter: about 22 thin bars, newest on the right, from real peak levels. With no level
/// (the app's audio session can't be matched) it stays flat and low rather than faking movement.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public const int Bars = 22;
    private readonly Queue<double> _levels = new();

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush),
        typeof(LevelMeter), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public LevelMeter()
    {
        Width = Bars * 3;
        Height = 10;
        IsHitTestVisible = false;
    }

    /// <summary>Adds the latest peak (0..1), or null when unknown.</summary>
    public void Push(double? level)
    {
        _levels.Enqueue(level ?? -1);
        while (_levels.Count > Bars) _levels.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var levels = _levels.ToArray();
        for (int i = 0; i < Bars; i++)
        {
            int j = i - (Bars - levels.Length); // right-aligned: the newest level is the last bar
            double v = j >= 0 ? levels[j] : -1;
            bool known = v >= 0;
            // Speech peaks are small; a square-root curve keeps quiet talking visible.
            double h = known ? Math.Clamp(Math.Sqrt(v) * Height, 1.5, Height) : 1.5;
            var brush = BarBrush.Clone();
            brush.Opacity = known && v > 0.01 ? 1 : 0.35;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * 3, Height - h, 2, h), 0.8, 0.8);
        }
    }
}
