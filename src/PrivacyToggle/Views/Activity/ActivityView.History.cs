using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>"History": the toolbar's results as a timeline (one row per app, up to three lanes) or a list of sessions.</summary>
public partial class ActivityView
{
    private const int RowStep = 25, ListStep = 200, HistoryBins = 40, LiveBins = 12;

    /// <summary>Uploads over this size in one session get flagged "— large upload".</summary>
    private static long LargeUpload => 100L * 1024 * 1024;

    private int _rowCap = RowStep, _listCap = ListStep;
    private ActivityRange _cappedRange;
    private ActivityFilter _cappedFilter;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TimelineRow> _timelineRows = new();
    private readonly List<(TextBlock Text, DateTime Start)> _listTimers = new();
    private MicLevelMeter? _meter;
    private DispatcherTimer? _meterTimer;

    /// <summary>Everything one timeline row needs, across the three resources.</summary>
    private sealed class HistoryApp
    {
        public required string Key { get; init; }
        public required AppIdentity App { get; init; }
        public List<UsageSpan> Spans = new();
        public List<NetworkSession> NetSessions = new();
        public TimeSpan Mic, Camera;
        public long Sent, Received;
        public DateTime LastActive;
        public DateTime? MicLive, CamLive, NetLive; // start of what's live now
        public List<Lane> Lanes = new();
        public bool Live => MicLive != null || CamLive != null || NetLive != null;
        public long Bytes => Sent + Received;
    }

    /// <summary>A rendered timeline row, kept so the live parts can tick every second without rebuilding it.</summary>
    private sealed class TimelineRow
    {
        public required HistoryApp Data { get; init; }
        public required ActivityTrack Track { get; init; }
        public required Canvas Dots { get; init; }
        public readonly List<(TextBlock Text, DateTime Start)> Timers = new();
        public double NetScale; // bytes/s that fills the network lane
    }

    // ---------------- building ----------------

    private List<HistoryApp> BuildHistory(DateTime from, DateTime now)
    {
        var usage = _app!.Usage;
        var net = _app.NetworkUsage;
        var apps = new Dictionary<string, HistoryApp>(StringComparer.OrdinalIgnoreCase);
        HistoryApp For(string key) =>
            apps.TryGetValue(key, out var a) ? a : apps[key] = new HistoryApp { Key = key, App = AppIdentity.For(key) };

        bool wantMic = _filter is ActivityFilter.All or ActivityFilter.Microphone;
        bool wantCam = _filter is ActivityFilter.All or ActivityFilter.Camera;
        bool wantNet = _filter is ActivityFilter.All or ActivityFilter.Network;

        if (wantMic || wantCam)
        {
            var filter = wantMic && wantCam ? ActivityFilter.All : wantMic ? ActivityFilter.Microphone : ActivityFilter.Camera;
            foreach (var a in ActivityModel.Build(usage.Sessions, usage.Blocked, from, now, filter, ActivitySort.LiveFirst))
            {
                var h = For(a.App.Key);
                h.Spans = a.Spans;
                (h.Mic, h.Camera, h.LastActive) = (a.Mic, a.Camera, a.LastActive);
                h.MicLive = a.Spans.Where(s => s.Live && s.Device == UsageDevice.Microphone).Select(s => (DateTime?)s.Start).Min();
                h.CamLive = a.Spans.Where(s => s.Live && s.Device == UsageDevice.Camera).Select(s => (DateTime?)s.Start).Min();
            }
        }

        if (wantNet)
        {
            var totals = net.Totals(from, now);
            foreach (var group in net.Sessions(from, now).GroupBy(s => s.AppKey))
            {
                var h = For(group.Key);
                h.NetSessions = group.ToList();
                h.NetLive = group.Where(s => s.Live).Select(s => (DateTime?)s.Start).Min();
                if (h.LastActive < group.Max(s => s.End)) h.LastActive = group.Max(s => s.End);
                if (totals.TryGetValue(group.Key, out var t)) (h.Sent, h.Received) = (t.Sent, t.Received);
            }
        }

        foreach (var h in apps.Values)
        {
            if (wantCam && h.Spans.Any(s => s.Device == UsageDevice.Camera)) h.Lanes.Add(Lane.Camera);
            if (wantMic && h.Spans.Any(s => s.Device == UsageDevice.Microphone)) h.Lanes.Add(Lane.Microphone);
            if (wantNet && h.NetSessions.Count > 0) h.Lanes.Add(Lane.Network);
        }

        var list = apps.Values.Where(h => h.Lanes.Count > 0);
        return (_sort switch
        {
            ActivitySort.MostData => list.OrderByDescending(h => h.Bytes).ThenByDescending(h => h.LastActive),
            ActivitySort.LongestMic => list.OrderByDescending(h => h.Mic).ThenByDescending(h => h.LastActive),
            ActivitySort.Name => list.OrderBy(h => h.App.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderByDescending(h => h.Live).ThenByDescending(h => h.LastActive),
        }).ToList();
    }

    // ---------------- refresh ----------------

    private void RefreshHistory(DateTime now)
    {
        if (_cappedRange != _range || _cappedFilter != _filter)
        {
            (_rowCap, _listCap, _cappedRange, _cappedFilter) = (RowStep, ListStep, _range, _filter);
        }
        if (Legend.Children.Count == 0) BuildLegend();

        var from = now - ActivityModel.Length(_range);
        RefreshTotals(from, now);
        var apps = BuildHistory(from, now);
        bool list = ViewList.IsChecked == true;

        if (apps.Count == 0)
        {
            TimelineHost.Visibility = ListHost.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            MoreRowsButton.Visibility = Visibility.Collapsed;
            string what = _filter switch
            {
                ActivityFilter.Microphone => "microphone ",
                ActivityFilter.Camera => "camera ",
                ActivityFilter.Network => "network ",
                _ => "",
            };
            EmptyText.Text = $"No {what}activity in the last {ActivityModel.Label(_range)}.";
            EmptyButton.Visibility = _range == ActivityRange.Days7 ? Visibility.Collapsed : Visibility.Visible;
            EmptyButton.Content = $"Show the last {ActivityModel.Label(_range == ActivityRange.Days7 ? _range : _range + 1)}";
            _timelineRows.Clear();
            _listTimers.Clear();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        if (list) RefreshList(apps, from, now);
        else RefreshTimeline(apps, from, now);
    }

    /// <summary>
    /// "Last 7 days: Microphone 12h 30m · Camera 2h 10m · Network 4.2 GB (↓ 3.9 GB · ↑ 300 MB)". Microphone and camera
    /// time counts once however many apps used the device at the same time; network is every byte of every app.
    /// </summary>
    private void RefreshTotals(DateTime from, DateTime now)
    {
        var usage = _app!.Usage;
        var spans = ActivityModel.Build(usage.Sessions, usage.Blocked, from, now, ActivityFilter.All, ActivitySort.LiveFirst)
            .SelectMany(a => a.Spans).ToList();
        var mic = ActivityModel.Union(spans.Where(s => s.Device == UsageDevice.Microphone));
        var cam = ActivityModel.Union(spans.Where(s => s.Device == UsageDevice.Camera));
        var net = _app.NetworkUsage.Totals(from, now).Values;
        long received = net.Sum(t => t.Received), sent = net.Sum(t => t.Sent);

        RangeTotals.Children.Clear();
        string range = _range switch
        {
            ActivityRange.Hour1 => "Last hour",
            ActivityRange.Hours6 => "Last 6 hours",
            ActivityRange.Hours24 => "Last 24 hours",
            _ => "Last 7 days",
        };
        var label = new TextBlock { Text = range + ":", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        RangeTotals.Children.Add(label);

        void Total(Lane lane, string name, string value, string? detail)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var (chip, _) = ResourceChip(lane, name);
            chip.Margin = new Thickness(0, 0, 6, 0);
            item.Children.Add(chip);
            item.Children.Add(ResourceCard.Tabular(new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center }));
            if (detail != null)
            {
                var d = ResourceCard.Tabular(new TextBlock { Text = detail, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                d.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                item.Children.Add(d);
            }
            RangeTotals.Children.Add(item);
        }
        if (_filter is ActivityFilter.All or ActivityFilter.Microphone) Total(Lane.Microphone, "Microphone", Duration(mic), null);
        if (_filter is ActivityFilter.All or ActivityFilter.Camera) Total(Lane.Camera, "Camera", Duration(cam), null);
        if (_filter is ActivityFilter.All or ActivityFilter.Network)
            Total(Lane.Network, "Network", Bytes(received + sent), $"(↓ {Bytes(received)} · ↑ {Bytes(sent)})");
    }

    private void TickHistory(DateTime now)
    {
        var from = now - ActivityModel.Length(_range);
        if (TimelineHost.Visibility == Visibility.Visible)
        {
            Axis.SetWindow(from, now, _range);
            Backdrop.SetWindow(from, now, _range);
            foreach (var row in _timelineRows)
            {
                // Live sessions run up to now; the live zone's network bars shift left as time passes.
                var spans = row.Data.Spans.Select(s => s.Live ? s with { End = now } : s).ToList();
                row.Data.Spans = spans;
                row.Track.SetWindow(from, now, _range);
                row.Track.SetData(row.Data.App.Name, row.Data.Lanes, spans, NetworkHistory(row, from, now), NetworkLive(row, now));
                foreach (var (text, start) in row.Timers) text.Text = "● " + Clock(now - start);
            }
        }
        foreach (var (text, start) in _listTimers) text.Text = Clock(now - start);
    }

    // ---------------- timeline ----------------

    private void BuildLegend()
    {
        void Add(string brush, string text, double width, double height)
        {
            var swatch = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            swatch.SetResourceReference(Border.BackgroundProperty, brush);
            var label = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Legend.Children.Add(swatch);
            Legend.Children.Add(label);
        }
        Add("MicUsage", "Mic", 14, 5);
        Add("CamUsage", "Camera", 14, 5);
        Add("NetUsage", "Data", 5, 11);
    }

    private void RefreshTimeline(List<HistoryApp> apps, DateTime from, DateTime now)
    {
        ListHost.Visibility = Visibility.Collapsed;
        TimelineHost.Visibility = Visibility.Visible;
        _listTimers.Clear();
        Axis.SetWindow(from, now, _range);
        Backdrop.SetWindow(from, now, _range);

        Rows.Items.Clear();
        _timelineRows.Clear();
        var shown = apps.Take(_rowCap).ToList();
        bool grouped = _sort == ActivitySort.LiveFirst && shown.Any(a => a.Live) && shown.Any(a => !a.Live);
        bool dividerDone = false;
        foreach (var a in shown)
        {
            if (grouped && !a.Live && !dividerDone)
            {
                Rows.Items.Add(GroupDivider(_range == ActivityRange.Days7 ? "EARLIER" : "EARLIER TODAY"));
                dividerDone = true;
            }
            Rows.Items.Add(TimelineRowFor(a, from, now));
        }
        int more = apps.Count - shown.Count;
        MoreRowsButton.Content = $"Show {more} more app{(more == 1 ? "" : "s")}";
        MoreRowsButton.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement GroupDivider(string text)
    {
        var grid = new Grid { Margin = new Thickness(2, 12, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Black, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        grid.Children.Add(label);
        var line = new Border { Height = 1.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Border.BackgroundProperty, "SectionDivider");
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        return grid;
    }

    private FrameworkElement TimelineRowFor(HistoryApp a, DateTime from, DateTime now)
    {
        var container = new StackPanel();
        var row = new Border { Padding = new Thickness(0, 7, 0, 7), BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        row.SetResourceReference(Border.BorderBrushProperty, "RowLine");
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "RowHoverBg");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var track = new ActivityTrack { VerticalAlignment = VerticalAlignment.Center };
        var dots = new Canvas { IsHitTestVisible = false };
        var item = new TimelineRow { Data = a, Track = track, Dots = dots };
        item.NetScale = NetworkScale(a, from, now);

        grid.Children.Add(RowLabel(a, item, now));
        track.SetWindow(from, now, _range);
        track.SetData(a.App.Name, a.Lanes, a.Spans, NetworkHistory(item, from, now), NetworkLive(item, now));
        var lanes = new Grid { VerticalAlignment = VerticalAlignment.Center };
        lanes.Children.Add(track);
        lanes.Children.Add(dots);
        track.SizeChanged += (_, _) => PlaceDots(item);
        Grid.SetColumn(lanes, 1);
        grid.Children.Add(lanes);
        row.Child = grid;
        container.Children.Add(row);

        var detail = SessionDetail(a);
        detail.Visibility = _expanded.Contains(a.Key) ? Visibility.Visible : Visibility.Collapsed;
        container.Children.Add(detail);
        row.MouseLeftButtonUp += (_, _) =>
        {
            bool open = !_expanded.Remove(a.Key);
            if (open) _expanded.Add(a.Key);
            detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        };

        _timelineRows.Add(item);
        return container;
    }

    /// <summary>Icon, name, LIVE badge, and chips: running timers for what's live, otherwise totals for the range.</summary>
    private FrameworkElement RowLabel(HistoryApp a, TimelineRow item, DateTime now)
    {
        var grid = new Grid { Margin = new Thickness(4, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = AppIcon(a.App, 30);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        var name = new DockPanel();
        if (a.Live)
        {
            var live = SmallBadge("LIVE", "Accent", "AccentText");
            live.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(live, Dock.Right);
            name.Children.Add(live);
        }
        name.Children.Add(new TextBlock { Text = a.App.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = a.App.Name + "\n" + a.App.Detail });
        text.Children.Add(name);

        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void LiveChip(Lane lane, DateTime start)
        {
            var (chip, label) = ResourceChip(lane, "● " + Clock(now - start));
            item.Timers.Add((label, start));
            chips.Children.Add(chip);
        }
        if (a.Lanes.Contains(Lane.Microphone))
        {
            if (a.MicLive is { } m) LiveChip(Lane.Microphone, m);
            else chips.Children.Add(Tip(ResourceChip(Lane.Microphone, ActivityModel.Short(a.Mic)).Chip, "Microphone time in this range"));
        }
        if (a.Lanes.Contains(Lane.Camera))
        {
            if (a.CamLive is { } c) LiveChip(Lane.Camera, c);
            else chips.Children.Add(Tip(ResourceChip(Lane.Camera, ActivityModel.Short(a.Camera)).Chip, "Camera time in this range"));
        }
        if (a.Lanes.Contains(Lane.Network))
        {
            if (a.NetLive is { } n) LiveChip(Lane.Network, n);
            else chips.Children.Add(Tip(ResourceChip(Lane.Network, Bytes(a.Bytes)).Chip, $"Data in this range: ↓ {Bytes(a.Received)} · ↑ {Bytes(a.Sent)}"));
        }
        text.Children.Add(chips);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>A small pill in a resource's colour, with text in that colour's ink. Always text, never colour alone.</summary>
    private static (Border Chip, TextBlock Label) ResourceChip(Lane lane, string text)
    {
        var (fill, ink) = lane switch
        {
            Lane.Camera => ("CamUsage", "CamOnText"),
            Lane.Microphone => ("MicUsage", "MicOnText"),
            _ => ("NetUsage", "NetOnText"),
        };
        var label = ResourceCard.Tabular(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold });
        label.SetResourceReference(TextBlock.ForegroundProperty, ink);
        var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 5, 3), Child = label };
        chip.SetResourceReference(Border.BackgroundProperty, fill);
        return (chip, label);
    }

    private static Border Tip(Border chip, string tip)
    {
        chip.ToolTip = tip;
        return chip;
    }

    /// <summary>Pulsing dots at the right edge of each live lane, over the track.</summary>
    private void PlaceDots(TimelineRow row)
    {
        row.Dots.Children.Clear();
        var a = row.Data;
        void Dot(Lane lane, string brush)
        {
            var dot = new Ellipse { Width = 8, Height = 8 };
            dot.SetResourceReference(Shape.FillProperty, brush);
            Canvas.SetLeft(dot, Math.Max(0, row.Track.ActualWidth - 11));
            Canvas.SetTop(dot, row.Track.LaneCenter(lane) - 4);
            SetPulse(dot, true);
            row.Dots.Children.Add(dot);
        }
        if (a.CamLive != null && a.Lanes.Contains(Lane.Camera)) Dot(Lane.Camera, "CamUsage");
        if (a.MicLive != null && a.Lanes.Contains(Lane.Microphone)) Dot(Lane.Microphone, "MicUsage");
        if (a.NetLive != null && a.Lanes.Contains(Lane.Network)) Dot(Lane.Network, "NetUsage");
    }

    // ---------------- network lane data ----------------

    /// <summary>The app's busiest rate (bytes/s) over the range and the last minutes: the network lane's full height.</summary>
    private double NetworkScale(HistoryApp a, DateTime from, DateTime now)
    {
        if (!a.Lanes.Contains(Lane.Network)) return 0;
        var row = new TimelineRow { Data = a, Track = null!, Dots = null!, NetScale = 1 };
        double history = HistoryRates(row, from, now).DefaultIfEmpty().Max();
        double live = LiveRates(row, now).DefaultIfEmpty().Max();
        return Math.Max(history, live);
    }

    private double[] NetworkHistory(TimelineRow row, DateTime from, DateTime now) =>
        row.NetScale <= 0 ? Array.Empty<double>() : HistoryRates(row, from, now).Select(r => r / row.NetScale).ToArray();

    private double[] NetworkLive(TimelineRow row, DateTime now) =>
        row.NetScale <= 0 ? Array.Empty<double>() : LiveRates(row, now).Select(r => r / row.NetScale).ToArray();

    /// <summary>Average rate per bucket across the history area (the range minus the last five minutes).</summary>
    private double[] HistoryRates(TimelineRow row, DateTime from, DateTime now)
    {
        if (!row.Data.Lanes.Contains(Lane.Network)) return Array.Empty<double>();
        var zone = SplitScale.ZoneStart(now);
        double binSeconds = Math.Max(1, (zone - from).TotalSeconds / HistoryBins);
        var bytes = new double[HistoryBins];
        foreach (var slot in _app!.NetworkUsage.Slots(row.Data.Key, from, zone))
        {
            int i = (int)((slot.Start - from).TotalSeconds / binSeconds);
            if (i >= 0 && i < HistoryBins) bytes[i] += slot.Sent + slot.Received;
        }
        return bytes.Select(b => b / binSeconds).ToArray();
    }

    /// <summary>Rate per ~25-second bucket across the last five minutes, from the second-by-second record.</summary>
    private double[] LiveRates(TimelineRow row, DateTime now)
    {
        if (!row.Data.Lanes.Contains(Lane.Network)) return Array.Empty<double>();
        var zone = SplitScale.ZoneStart(now);
        double binSeconds = SplitScale.LiveSpan.TotalSeconds / LiveBins;
        var bytes = new double[LiveBins];
        var recent = _app!.NetworkUsage.Recent(row.Data.Key);
        if (recent.Count > 0)
        {
            foreach (var (time, sent, received) in recent)
            {
                int i = (int)((time - zone).TotalSeconds / binSeconds);
                if (i >= 0 && i < LiveBins) bytes[i] += sent + received;
            }
        }
        else
        {
            // Not seen live since PrivacyToggle started: fall back to the stored 30-second slots.
            foreach (var slot in _app.NetworkUsage.Slots(row.Data.Key, zone, now))
            {
                int i = (int)((slot.Start - zone).TotalSeconds / binSeconds);
                if (i >= 0 && i < LiveBins) bytes[i] += slot.Sent + slot.Received;
            }
        }
        return bytes.Select(b => b / binSeconds).ToArray();
    }

    // ---------------- row details ----------------

    /// <summary>Clicking a row: its sessions in the range, newest first, plus "Open file location".</summary>
    private FrameworkElement SessionDetail(HistoryApp a)
    {
        const int maxShown = 12;
        var panel = new StackPanel();
        var border = new Border
        {
            CornerRadius = new CornerRadius(8), Margin = new Thickness(4, 4, 2, 10), Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(1.5), Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "DetailBg");
        border.SetResourceReference(Border.BorderBrushProperty, "SectionDivider");

        var sessions = SessionsOf(a).Take(maxShown + 1).ToList();
        foreach (var s in sessions.Take(maxShown))
        {
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var (chip, _) = ResourceChip(s.Lane, s.Lane switch { Lane.Camera => "Camera", Lane.Microphone => "Mic", _ => "Network" });
            line.Children.Add(chip);
            var length = new TextBlock { Text = s.Live ? "ongoing" : ActivityModel.Long(s.End - s.Start), Margin = new Thickness(12, 0, 0, 0) };
            length.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            DockPanel.SetDock(length, Dock.Right);
            line.Children.Add(length);
            string day = s.Start.Date == DateTime.Today ? "" : s.Start.ToString("ddd ");
            line.Children.Add(new TextBlock { Text = s.Live ? $"{day}{s.Start:t} – now" : $"{day}{s.Start:t} – {s.End:t}", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(line);
        }
        if (sessions.Count > maxShown)
        {
            var more = new TextBlock { Text = "Switch to List for every session.", Margin = new Thickness(0, 4, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(more);
        }

        if (a.Key.StartsWith("np:"))
        {
            string path = a.Key[3..];
            var open = new Button { Style = (Style)FindResource("LinkButton"), Content = "Open file location", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-4, 8, 0, 0), ToolTip = path };
            open.Click += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                    else Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(path)!) { UseShellExecute = true });
                }
                catch { }
            };
            panel.Children.Add(open);
        }
        border.MouseLeftButtonUp += (_, e) => e.Handled = true; // clicks inside don't collapse the row
        return border;
    }

    // ---------------- list ----------------

    /// <summary>One continuous use of one resource by one app.</summary>
    private sealed record SessionRow(HistoryApp App, Lane Lane, DateTime Start, DateTime End, bool Live, long Sent, long Received);

    private IEnumerable<SessionRow> SessionsOf(HistoryApp a)
    {
        var net = _app!.NetworkUsage;
        var rows = new List<SessionRow>();
        foreach (var s in a.Spans)
        {
            var lane = s.Device == UsageDevice.Camera ? Lane.Camera : Lane.Microphone;
            if (!a.Lanes.Contains(lane)) continue;
            // Data the same app moved while it had the mic or camera: a useful privacy signal.
            long sent = 0, received = 0;
            foreach (var slot in net.Slots(a.Key, s.Start, s.End)) { sent += slot.Sent; received += slot.Received; }
            rows.Add(new SessionRow(a, lane, s.Start, s.End, s.Live, sent, received));
        }
        if (a.Lanes.Contains(Lane.Network))
            rows.AddRange(a.NetSessions.Select(s => new SessionRow(a, Lane.Network, s.Start, s.End, s.Live, s.Sent, s.Received)));
        return rows.OrderByDescending(r => r.Start);
    }

    private static readonly double[] ListColumns = { 112, 0, 96, 100, 230 }; // 0 = the stretching App column

    private Grid ListGrid()
    {
        var grid = new Grid();
        foreach (var w in ListColumns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        return grid;
    }

    private void RefreshList(List<HistoryApp> apps, DateTime from, DateTime now)
    {
        TimelineHost.Visibility = Visibility.Collapsed;
        ListHost.Visibility = Visibility.Visible;
        _timelineRows.Clear();
        _listTimers.Clear();

        if (ListHeader.Children.Count == 0)
        {
            var header = ListGrid();
            string[] titles = { "Started", "App", "Used", "Duration", "Data during session" };
            for (int i = 0; i < titles.Length; i++)
            {
                var t = new TextBlock { Text = titles[i], FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(i == 1 ? 34 : 0, 0, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                Grid.SetColumn(t, i);
                header.Children.Add(t);
            }
            ListHeader.Children.Add(header);
        }

        var sessions = apps.SelectMany(SessionsOf).Where(s => s.End >= from).OrderByDescending(s => s.Start).ToList();
        ListRows.Items.Clear();
        foreach (var s in sessions.Take(_listCap)) ListRows.Items.Add(ListRow(s, now));
        int more = sessions.Count - Math.Min(_listCap, sessions.Count);
        MoreRowsButton.Content = $"Show {more} more session{(more == 1 ? "" : "s")}";
        MoreRowsButton.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement ListRow(SessionRow s, DateTime now)
    {
        var row = new Border { Padding = new Thickness(0, 7, 0, 7), BorderThickness = new Thickness(0, 0, 0, 1) };
        row.SetResourceReference(Border.BorderBrushProperty, "RowLine");
        var grid = ListGrid();

        string day = s.Start.Date == now.Date ? "" : s.Start.ToString("ddd ");
        AddCell(grid, ResourceCard.Tabular(new TextBlock { Text = day + s.Start.ToString("t") }), 0);

        var app = new StackPanel { Orientation = Orientation.Horizontal };
        app.Children.Add(AppIcon(s.App.App, 24));
        app.Children.Add(new TextBlock { Text = s.App.App.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = s.App.App.Detail });
        AddCell(grid, app, 1);

        AddCell(grid, ResourceChip(s.Lane, s.Lane switch { Lane.Camera => "Camera", Lane.Microphone => "Microphone", _ => "Network" }).Chip, 2);

        var duration = ResourceCard.Tabular(new TextBlock());
        if (s.Live && s.Lane != Lane.Network)
        {
            duration.Text = Clock(now - s.Start);
            duration.FontWeight = FontWeights.Bold;
            _listTimers.Add((duration, s.Start));
        }
        else duration.Text = s.Live ? "ongoing" : ActivityModel.Short(s.End - s.Start);
        AddCell(grid, duration, 3);

        AddCell(grid, DataCell(s), 4);
        row.Child = grid;
        return row;
    }

    /// <summary>"↓ 42 MB · ↑ 31 MB", "↓ 640 MB so far", "↑ 180 MB — large upload", or "—".</summary>
    private static FrameworkElement DataCell(SessionRow s)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (s.Sent + s.Received == 0)
        {
            text.Text = "—";
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return text;
        }
        void Part(string value, string brush)
        {
            var run = new System.Windows.Documents.Run(value);
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brush);
            text.Inlines.Add(run);
        }
        bool down = s.Received >= 1024, up = s.Sent >= 1024;
        if (down) Part("↓ " + Bytes(s.Received), "DownloadText");
        if (down && up) Part(" · ", "TextSecondary");
        if (up) Part("↑ " + Bytes(s.Sent), "UploadText");
        if (!down && !up) Part("< 1 KB", "TextSecondary");
        if (s.Live) Part(" so far", "TextSecondary");
        if (s.Sent >= LargeUpload)
        {
            var flag = new System.Windows.Documents.Run(" — large upload") { FontWeight = FontWeights.Bold };
            flag.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "UploadText");
            text.Inlines.Add(flag);
        }
        return text;
    }

    private static void AddCell(Grid grid, FrameworkElement element, int column)
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private void MoreRows_Click(object sender, RoutedEventArgs e)
    {
        if (ViewList.IsChecked == true) _listCap += ListStep; else _rowCap += RowStep;
        RefreshHistory(DateTime.Now);
    }

    // ---------------- export ----------------

    /// <summary>Saves every session of the selected range and filter as CSV (what the List view shows, uncapped).</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;
        var now = DateTime.Now;
        var from = now - ActivityModel.Length(_range);
        var sessions = BuildHistory(from, now).SelectMany(SessionsOf).Where(s => s.End >= from).OrderBy(s => s.Start).ToList();

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export activity",
            FileName = $"PrivacyToggle activity {now:yyyy-MM-dd HHmm}.csv",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        static string Csv(string v) => v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder("Started,Ended,App,Path,Resource,Duration (s),Downloaded (bytes),Uploaded (bytes),Live\r\n");
        foreach (var s in sessions)
        {
            sb.Append(string.Join(",",
                s.Start.ToString("yyyy-MM-dd HH:mm:ss"), s.Live ? "" : s.End.ToString("yyyy-MM-dd HH:mm:ss"),
                Csv(s.App.App.Name), Csv(s.App.Key.StartsWith("np:") ? s.App.Key[3..] : s.App.Key),
                s.Lane, (int)(s.End - s.Start).TotalSeconds, s.Received, s.Sent, s.Live ? "yes" : "no"));
            sb.Append("\r\n");
        }
        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Couldn't save the file: " + ex.Message, "PrivacyToggle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- meters, pulsing, scrolling ----------------

    /// <summary>Mic levels about ten times a second, only while the page shows (and not with reduced motion).</summary>
    private void StartMeters()
    {
        if (ReduceMotion) return;
        _meter ??= new MicLevelMeter();
        _meterTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => TickMeters(), Dispatcher);
        _meterTimer.Start();
    }

    private void StopMeters() => _meterTimer?.Stop();

    private void TickMeters()
    {
        if (_meter == null || _micRows.Count == 0 || !Active) return;
        var levels = _meter.Sample();
        foreach (var row in _micRows)
        {
            row.Meter?.Push(levels.TryGetValue(row.AppKey, out var level) ? level : null);
        }
    }

    /// <summary>A gentle on/off pulse (about a second), or steady when reduced motion is on.</summary>
    private static void SetPulse(UIElement element, bool on)
    {
        if (on && !ReduceMotion)
        {
            if (element.HasAnimatedProperties) return;
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(950))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    /// <summary>The history box only scrolls sideways; pass the mouse wheel on so the page still scrolls.</summary>
    private void HistoryScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var parent = (UIElement)((FrameworkElement)sender).Parent;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    /// <summary>"03:10", "1:02:33".</summary>
    private static string Clock(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes:00}:{d.Seconds:00}";
    }
}
