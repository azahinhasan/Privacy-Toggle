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

    private static Path Filled(Geometry g, string brushKey)
    {
        var p = new Path { Data = g };
        p.SetResourceReference(Shape.FillProperty, brushKey);
        return p;
    }
}

/// <summary>Where the time labels and gridlines go for a range.</summary>
public static class TimeScale
{
    public static double X(DateTime t, DateTime from, DateTime to, double width)
    {
        double f = (t - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds);
        return Math.Clamp(f, 0, 1) * Math.Max(0, width - 2); // 2px inset so the "now" line stays visible
    }

    /// <summary>Tick times; midnight ticks are flagged (stronger line + date label).</summary>
    public static IEnumerable<(DateTime Time, bool Midnight)> Ticks(ActivityRange range, DateTime from, DateTime to)
    {
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
    protected DateTime From, To;
    protected ActivityRange Range;

    protected TimeElement()
    {
        Loaded += (_, _) => { ThemeService.Changed -= InvalidateVisual; ThemeService.Changed += InvalidateVisual; };
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
    }

    public void SetWindow(DateTime from, DateTime to, ActivityRange range)
    {
        (From, To, Range) = (from, to, range);
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

    protected double X(DateTime t) => TimeScale.X(t, From, To, ActualWidth);
}

/// <summary>Time labels above the tracks; "Now" at the right end, date labels at midnight.</summary>
public sealed class TimeAxis : TimeElement
{
    protected override Size MeasureOverride(Size available) => new(0, 22);

    protected override void OnRender(DrawingContext dc)
    {
        if (To <= From) return;
        var secondary = Res("TextSecondary");
        var now = TimeScale.Text(this, "Now", 11, Res("Accent"), bold: true);
        double nowLeft = ActualWidth - now.Width;
        dc.DrawText(now, new Point(nowLeft, 2));

        double lastRight = double.NegativeInfinity;
        foreach (var (t, midnight) in TimeScale.Ticks(Range, From, To))
        {
            // A midnight inside an hourly view gets an emphasised date; in the 7-day view all labels are days.
            bool strong = midnight && Range != ActivityRange.Days7;
            var label = TimeScale.Text(this, TimeScale.Label(t, Range, midnight), 11,
                strong ? Res("TextPrimary") : secondary, bold: strong);
            double x = X(t);
            // Day labels sit just right of their line; time labels are centred on it.
            double left = Range == ActivityRange.Days7 ? x + 4 : x - label.Width / 2;
            if (left < 0 || left < lastRight + 8 || left + label.Width > nowLeft - 8) continue;
            dc.DrawText(label, new Point(left, 2));
            lastRight = left + label.Width;
        }
    }
}

/// <summary>Gridlines behind the tracks (stronger at midnight), or the "now" line in front of them.</summary>
public sealed class TimeGrid : TimeElement
{
    public bool NowLine { get; init; }

    public TimeGrid() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        if (To <= From) return;
        if (NowLine)
        {
            double x = X(To) + 0.75;
            dc.DrawLine(new Pen(Res("Accent"), 1.5), new Point(x, 0), new Point(x, ActualHeight));
            return;
        }
        var grid = new Pen(Faded(Res("TextPrimary"), 0.07), 1);
        var midnightPen = new Pen(Faded(Res("TextPrimary"), 0.28), 1.5);
        foreach (var (t, midnight) in TimeScale.Ticks(Range, From, To))
        {
            double x = Math.Round(X(t)) + 0.5;
            // In the 7-day view every line is a midnight; only shorter ranges need the stronger day divider.
            dc.DrawLine(midnight && Range != ActivityRange.Days7 ? midnightPen : grid, new Point(x, 0), new Point(x, ActualHeight));
        }
    }
}

/// <summary>
/// One app's usage across the window: camera lane on top, microphone lane below (or one lane when filtered).
/// Bars are as wide as the real use, at least 4px; hover one for details.
/// </summary>
public sealed class UsageTrack : TimeElement
{
    private const double LaneHeight = 6, LaneGap = 4, MinBar = 4;

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse };
    private List<UsageSpan> _spans = new();
    private string _app = "";
    private bool _showCam = true, _showMic = true;
    private UsageSpan? _hovered;

    public UsageTrack()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        _tip.Opened += (_, _) => { if (_hovered == null) _tip.IsOpen = false; };
        _tip.SetResourceReference(StyleProperty, "ThemedToolTip");
        Height = 40;
    }

    public void SetData(string app, List<UsageSpan> spans, ActivityFilter filter)
    {
        _app = app;
        _spans = spans;
        _showCam = filter != ActivityFilter.Microphone;
        _showMic = filter != ActivityFilter.Camera;
        InvalidateVisual();
    }

    private (double Top, bool Visible) Lane(UsageDevice d)
    {
        double mid = ActualHeight / 2;
        if (_showCam && _showMic)
            return d == UsageDevice.Camera ? (mid - LaneHeight - LaneGap / 2, true) : (mid + LaneGap / 2, true);
        bool visible = d == UsageDevice.Camera ? _showCam : _showMic;
        return (mid - LaneHeight / 2, visible);
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
        if (To <= From) return;
        var laneFill = Faded(Res("TextPrimary"), 0.07);
        foreach (var d in new[] { UsageDevice.Camera, UsageDevice.Microphone })
        {
            var (top, visible) = Lane(d);
            if (visible)
                dc.DrawRoundedRectangle(laneFill, null, new Rect(0, top, Math.Max(0, ActualWidth - 2), LaneHeight), 3, 3);
        }
        var highlight = new Pen(Res("TextPrimary"), 1.2);
        foreach (var s in _spans)
        {
            var (top, visible) = Lane(s.Device);
            if (!visible) continue;
            var (x1, x2) = Bar(s);
            dc.DrawRoundedRectangle(Res(Glyphs.BrushKey(s.Device)), ReferenceEquals(s, _hovered) ? highlight : null,
                new Rect(x1, top, x2 - x1, LaneHeight), 3, 3);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        UsageSpan? hit = null;
        foreach (var s in _spans)
        {
            var (top, visible) = Lane(s.Device);
            if (!visible) continue;
            var (x1, x2) = Bar(s);
            if (p.X >= x1 - 3 && p.X <= x2 + 3 && p.Y >= top - 5 && p.Y <= top + LaneHeight + 5) hit = s;
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
        string range = s.Live ? $"since {s.Start:t} · in use now" : $"{s.Start:t} – {s.End:t}";
        if (s.Start.Date != DateTime.Today) range = $"{s.Start:ddd} " + range;
        return $"{app} · {ActivityModel.Name(s.Device)}\n{range} · {ActivityModel.Long(s.Duration)}";
    }
}

/// <summary>Tiny 24-hour two-lane sparkline for the list view.</summary>
public sealed class Sparkline : TimeElement
{
    private List<UsageSpan> _spans = new();
    private bool _showCam = true, _showMic = true;

    public Sparkline()
    {
        Width = 96;
        Height = 20;
        IsHitTestVisible = false;
    }

    public void SetData(List<UsageSpan> spans, ActivityFilter filter, DateTime now)
    {
        _spans = spans;
        _showCam = filter != ActivityFilter.Microphone;
        _showMic = filter != ActivityFilter.Camera;
        SetWindow(now.AddHours(-24), now, ActivityRange.Hours24);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (To <= From) return;
        dc.DrawRoundedRectangle(Faded(Res("TextPrimary"), 0.06), null, new Rect(RenderSize), 4, 4);
        bool both = _showCam && _showMic;
        foreach (var s in _spans)
        {
            if (s.Device == UsageDevice.Camera ? !_showCam : !_showMic) continue;
            double top = !both ? 7 : s.Device == UsageDevice.Camera ? 4 : 11;
            double x1 = X(s.Start), x2 = Math.Max(X(s.End), x1 + 2);
            dc.DrawRoundedRectangle(Res(Glyphs.BrushKey(s.Device)), null, new Rect(x1, top, x2 - x1, 5), 1.5, 1.5);
        }
    }
}
