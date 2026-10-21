using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>The Activity page: today's stats, what's in use now, and a timeline or list of app usage.</summary>
public partial class ActivityView : UserControl
{
    private const double LabelColumn = 240;

    private readonly DispatcherTimer _clock;    // 1 s: live counters
    private readonly DispatcherTimer _refresh;  // 30 s: the window slides with "now"
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(TextBlock Text, DateTime Start)> _liveCounters = new();
    private UsageTracker? _usage;
    private ActivityRange _range = ActivityRange.Hours24;
    private ActivityFilter _filter = ActivityFilter.All;
    private ActivitySort _sort = ActivitySort.MostRecent;
    private bool _rangeChosen;
    private bool _loading = true;

    public ActivityView()
    {
        InitializeComponent();
        MicTileGlyph.Content = Glyphs.Device(UsageDevice.Microphone, 14);
        CamTileGlyph.Content = Glyphs.Device(UsageDevice.Camera, 14);
        MicChipGlyph.Content = Glyphs.Device(UsageDevice.Microphone, 13);
        CamChipGlyph.Content = Glyphs.Device(UsageDevice.Camera, 13);
        MicLinkGlyph.Content = Glyphs.Device(UsageDevice.Microphone, 13, "Ink");
        CamLinkGlyph.Content = Glyphs.Device(UsageDevice.Camera, 13, "Ink");

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _clock.Stop();
        _refresh.Stop();

        // Four tiles in a row, two rows when narrow.
        SizeChanged += (_, e) => Tiles.Columns = e.NewSize.Width < 680 ? 2 : 4;
        _loading = false;
    }

    public void Attach(UsageTracker usage)
    {
        _usage = usage;
        _usage.Changed += OnUsageChanged;
    }

    public void Detach()
    {
        if (_usage != null) _usage.Changed -= OnUsageChanged;
        _clock.Stop();
        _refresh.Stop();
    }

    /// <summary>The page became visible (or hidden).</summary>
    public void SetActive(bool active)
    {
        if (!active)
        {
            _clock.Stop();
            _refresh.Stop();
            return;
        }
        if (_usage == null) return;
        if (!_rangeChosen)
        {
            // Open on the smallest range that holds all of today's activity.
            _range = ActivityModel.DefaultRange(_usage.Sessions, _usage.Blocked, DateTime.Now);
            _rangeChosen = true;
        }
        SyncRangeButtons();
        Refresh();
        _clock.Start();
        _refresh.Start();
    }

    private bool Active => _usage != null && IsVisible;

    private void OnUsageChanged()
    {
        if (Active) Refresh();
    }

    // ---------------- toolbar ----------------

    private void SyncRangeButtons()
    {
        _loading = true;
        Range1h.IsChecked = _range == ActivityRange.Hour1;
        Range6h.IsChecked = _range == ActivityRange.Hours6;
        Range24h.IsChecked = _range == ActivityRange.Hours24;
        Range7d.IsChecked = _range == ActivityRange.Days7;
        _loading = false;
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _range = sender == Range1h ? ActivityRange.Hour1
               : sender == Range6h ? ActivityRange.Hours6
               : sender == Range7d ? ActivityRange.Days7
               : ActivityRange.Hours24;
        _rangeChosen = true;
        Refresh(animate: true);
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _filter = sender == FilterMic ? ActivityFilter.Microphone : sender == FilterCam ? ActivityFilter.Camera : ActivityFilter.All;
        Refresh(animate: true);
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SortBox == null) return;
        _sort = SortBox.SelectedIndex switch { 1 => ActivitySort.MostUsed, 2 => ActivitySort.Name, _ => ActivitySort.MostRecent };
        Refresh(animate: true);
    }

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Refresh(animate: true);
    }

    private void EmptyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_range == ActivityRange.Days7) return;
        _range = _range + 1;
        SyncRangeButtons();
        Refresh(animate: true);
    }

    private void MicPrivacy_Click(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-microphone");
    private void CamPrivacy_Click(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-webcam");

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ---------------- refresh ----------------

    private void Refresh(bool animate = false)
    {
        if (_usage == null) return;
        var now = DateTime.Now;
        var sessions = _usage.Sessions;

        RefreshTiles(sessions, now);
        RefreshLive(sessions, now);

        var from = now - ActivityModel.Length(_range);
        var apps = ActivityModel.Build(sessions, _usage.Blocked, from, now, _filter, _sort);
        bool list = ViewList.IsChecked == true;

        if (apps.Count == 0)
        {
            TimelineHost.Visibility = Visibility.Collapsed;
            ListRows.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            string what = _filter switch
            {
                ActivityFilter.Microphone => "microphone ",
                ActivityFilter.Camera => "camera ",
                _ => "microphone or camera ",
            };
            EmptyText.Text = $"No {what}activity in the last {ActivityModel.Label(_range)}.";
            EmptyButton.Visibility = _range == ActivityRange.Days7 ? Visibility.Collapsed : Visibility.Visible;
            EmptyButton.Content = $"Show the last {ActivityModel.Label(_range + 1 > ActivityRange.Days7 ? ActivityRange.Days7 : _range + 1)}";
        }
        else if (list)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            TimelineHost.Visibility = Visibility.Collapsed;
            ListRows.Visibility = Visibility.Visible;
            ListRows.Items.Clear();
            foreach (var a in apps) ListRows.Items.Add(ListRow(a, now));
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            ListRows.Visibility = Visibility.Collapsed;
            TimelineHost.Visibility = Visibility.Visible;
            Axis.SetWindow(from, now, _range);
            Gridlines.SetWindow(from, now, _range);
            NowLine.SetWindow(from, now, _range);
            Rows.Items.Clear();
            foreach (var a in apps) Rows.Items.Add(TimelineRow(a, from, now));
        }

        if (animate)
        {
            ContentHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() });
        }
    }

    private void RefreshTiles(IReadOnlyList<UsageSession> sessions, DateTime now)
    {
        var t = ActivityModel.Today(sessions, _usage!.Blocked, now);
        MicToday.Text = t.Mic == TimeSpan.Zero ? "None" : ActivityModel.Short(t.Mic);
        CamToday.Text = t.Camera == TimeSpan.Zero ? "None" : ActivityModel.Short(t.Camera);
        AppsToday.Text = t.Apps.ToString();
        if (t.LastApp == null || t.LastTime == null || t.LastDevice == null)
        {
            LastApp.Text = "None yet";
            LastWhen.Text = "";
            LastApp.ToolTip = null;
        }
        else
        {
            LastApp.Text = t.LastApp.Name;
            LastApp.ToolTip = t.LastApp.Name;
            LastWhen.Text = $"{ActivityModel.Name(t.LastDevice.Value)} · " +
                            (t.LastLive ? "in use now" : ActivityModel.Ago(t.LastTime.Value, now));
        }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        foreach (var (text, start) in _liveCounters) text.Text = ActivityModel.Clock(now - start);
    }

    // ---------------- in use now ----------------

    private void RefreshLive(IReadOnlyList<UsageSession> sessions, DateTime now)
    {
        LiveList.Items.Clear();
        _liveCounters.Clear();
        // Only real use: apps holding a muted mic / disabled camera are left out.
        var live = ActivityModel.LiveNow(sessions, _usage!.Blocked, now);
        foreach (var (appKey, spans) in live)
        {
            var app = spans;
            var identity = AppIdentity.For(appKey);
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool camera = app.Any(s => s.Device == UsageDevice.Camera);
            grid.Children.Add(PulsingDot(camera ? "CamUsage" : "MicUsage"));

            var icon = AppIcon(identity, 24);
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            var text = new StackPanel { Margin = new Thickness(10, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = identity.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = identity.Name });
            var detail = new TextBlock { Text = identity.Detail, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = identity.Detail };
            detail.Style = (Style)FindResource("Hint");
            text.Children.Add(detail);
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var s in app.OrderBy(s => s.Device))
            {
                var start = s.Start; // already local; after an unmute this is when the app could hear again
                var chip = Chip(s.Device, ActivityModel.Clock(now - start), out var counter, large: true);
                chip.ToolTip = $"{ActivityModel.Name(s.Device)} in use since {start:t}";
                _liveCounters.Add((counter, start));
                chips.Children.Add(chip);
            }
            Grid.SetColumn(chips, 3);
            grid.Children.Add(chips);
            LiveList.Items.Add(grid);
        }
        LiveEmpty.Visibility = live.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Apps that hold a blocked device: say so, so an empty list isn't confusing.
        var liveKeys = live.Select(l => l.AppKey).ToHashSet();
        var notes = new List<string>();
        foreach (var device in new[] { UsageDevice.Microphone, UsageDevice.Camera })
        {
            bool blocked = _usage.Blocked.Any(b => b.Device == device && b.End == null);
            if (!blocked) continue;
            var holders = sessions.Where(s => s.End == null && s.Device == device)
                .Select(s => AppIdentity.For(s.AppKey).Name).Distinct().ToList();
            if (holders.Count == 0) continue;
            string who = holders.Count == 1 ? holders[0] : $"{holders[0]} and {holders.Count - 1} other app{(holders.Count > 2 ? "s" : "")}";
            string verb = holders.Count == 1 ? "has" : "have";
            notes.Add(device == UsageDevice.Microphone
                ? $"{who} {verb} the microphone open, but it's muted, so they hear nothing."
                : $"{who} {verb} the camera open, but it's off, so they see nothing.");
        }
        BlockedNote.Text = string.Join("\n", notes);
        BlockedNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement PulsingDot(string brushKey)
    {
        var dot = new Ellipse { Width = 11, Height = 11, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        dot.SetResourceReference(Shape.StrokeProperty, "Ink");
        dot.SetResourceReference(Shape.FillProperty, brushKey);
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        return dot;
    }

    // ---------------- timeline rows ----------------

    private FrameworkElement TimelineRow(AppActivity a, DateTime from, DateTime now)
    {
        bool expanded = _expanded.Contains(a.App.Key);
        var container = new StackPanel();

        var row = new Border { CornerRadius = new CornerRadius(8), MinHeight = 44, Padding = new Thickness(0, 4, 0, 4), BorderThickness = new Thickness(2), Cursor = System.Windows.Input.Cursors.Hand };
        SetRowBackground(row, expanded);
        row.MouseEnter += (_, _) => SetRowBackground(row, true);
        row.MouseLeave += (_, _) => SetRowBackground(row, _expanded.Contains(a.App.Key));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumn) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(AppLabel(a));

        var track = new UsageTrack { VerticalAlignment = VerticalAlignment.Center };
        track.SetWindow(from, now, _range);
        track.SetData(a.App.Name, a.Spans, _filter);
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        row.Child = grid;
        container.Children.Add(row);

        var detail = SessionDetail(a);
        detail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        container.Children.Add(detail);

        row.MouseLeftButtonUp += (_, _) =>
        {
            bool open = !_expanded.Remove(a.App.Key);
            if (open) _expanded.Add(a.App.Key);
            detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SetRowBackground(row, open || row.IsMouseOver);
        };
        return container;
    }

    private static void SetRowBackground(Border row, bool highlighted)
    {
        if (highlighted)
        {
            row.SetResourceReference(Border.BackgroundProperty, "RowHoverBg");
            row.SetResourceReference(Border.BorderBrushProperty, "Ink");
        }
        else
        {
            row.Background = Brushes.Transparent;
            row.BorderBrush = Brushes.Transparent;
        }
    }

    /// <summary>Icon, full name (up to two lines, never cut mid-word) and per-device duration chips.</summary>
    private FrameworkElement AppLabel(AppActivity a)
    {
        var grid = new Grid { Margin = new Thickness(8, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = AppIcon(a.App, 24);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        var nameRow = new DockPanel();
        if (a.Live)
        {
            var dot = PulsingDot(a.Spans.Any(s => s.Live && s.Device == UsageDevice.Camera) ? "CamUsage" : "MicUsage");
            dot.Margin = new Thickness(0, 0, 6, 0);
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 5, 6, 0);
            DockPanel.SetDock(dot, Dock.Left);
            nameRow.Children.Add(dot);
        }
        nameRow.Children.Add(new TextBlock
        {
            Text = a.App.Name,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.WordEllipsis,
            LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 34,
            ToolTip = a.App.Name + "\n" + a.App.Detail,
        });
        text.Children.Add(nameRow);

        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (a.Spans.Any(s => s.Device == UsageDevice.Microphone)) chips.Children.Add(Chip(UsageDevice.Microphone, ActivityModel.Short(a.Mic), out _));
        if (a.Spans.Any(s => s.Device == UsageDevice.Camera)) chips.Children.Add(Chip(UsageDevice.Camera, ActivityModel.Short(a.Camera), out _));
        text.Children.Add(chips);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Expanded row: the app's sessions in the window, newest first, plus "Open file location".</summary>
    private FrameworkElement SessionDetail(AppActivity a)
    {
        const int maxShown = 12;
        var panel = new StackPanel();
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(8, 4, 2, 10),
            Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(2),
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "DetailBg");
        border.SetResourceReference(Border.BorderBrushProperty, "Ink");

        var sessions = a.Spans.OrderByDescending(s => s.Start).ToList();
        foreach (var s in sessions.Take(maxShown))
        {
            var line = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = Glyphs.Device(s.Device, 13);
            glyph.HorizontalAlignment = HorizontalAlignment.Left;
            line.Children.Add(glyph);

            string day = s.Start.Date == DateTime.Today ? "" : s.Start.ToString("ddd ");
            var when = new TextBlock
            {
                Text = s.Live ? $"{day}{s.Start:t} – now" : $"{day}{s.Start:t} – {s.End:t}",
                ToolTip = ActivityModel.Name(s.Device),
            };
            Grid.SetColumn(when, 1);
            line.Children.Add(when);

            var length = new TextBlock { Text = s.Live ? "in use" : ActivityModel.Long(s.Duration) };
            length.Style = (Style)FindResource("Hint");
            length.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(length, 2);
            line.Children.Add(length);
            panel.Children.Add(line);
        }
        if (sessions.Count > maxShown)
        {
            var more = new TextBlock { Text = $"+ {sessions.Count - maxShown} earlier sessions", Margin = new Thickness(22, 4, 0, 0) };
            more.Style = (Style)FindResource("Hint");
            panel.Children.Add(more);
        }

        if (a.App.Key.StartsWith("np:"))
        {
            string path = a.App.Key[3..];
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
        // Clicks inside the details shouldn't collapse the row.
        border.MouseLeftButtonUp += (_, e) => e.Handled = true;
        return border;
    }

    // ---------------- list rows ----------------

    private FrameworkElement ListRow(AppActivity a, DateTime now)
    {
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 8, 8, 8), BorderThickness = new Thickness(2) };
        SetRowBackground(row, false);
        row.MouseEnter += (_, _) => SetRowBackground(row, true);
        row.MouseLeave += (_, _) => SetRowBackground(row, false);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });                  // mic
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });                  // camera
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });                 // last used
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // sparkline

        var icon = AppIcon(a.App, 24);
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = a.App.Name, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis,
            LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 34,
            Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = a.App.Name + "\n" + a.App.Detail,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        if (a.Spans.Any(s => s.Device == UsageDevice.Microphone))
            AddCell(grid, Chip(UsageDevice.Microphone, ActivityModel.Short(a.Mic), out _), 2);
        if (a.Spans.Any(s => s.Device == UsageDevice.Camera))
            AddCell(grid, Chip(UsageDevice.Camera, ActivityModel.Short(a.Camera), out _), 3);

        var last = new TextBlock { Text = a.Live ? "in use now" : ActivityModel.Ago(a.LastActive, now), VerticalAlignment = VerticalAlignment.Center };
        last.Style = (Style)FindResource("Hint");
        last.VerticalAlignment = VerticalAlignment.Center;
        AddCell(grid, last, 4);

        var spark = new Sparkline { VerticalAlignment = VerticalAlignment.Center, ToolTip = "Last 24 hours" };
        spark.SetData(a.Last24h, _filter, now);
        AddCell(grid, spark, 5);

        row.Child = grid;
        return row;
    }

    private static void AddCell(Grid grid, FrameworkElement element, int column)
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    // ---------------- building blocks ----------------

    /// <summary>Solid chip in the device colour with an ink outline, black text and glyph: never colour alone.</summary>
    private static Border Chip(UsageDevice device, string text, out TextBlock label, bool large = false)
    {
        string key = Glyphs.BrushKey(device);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Glyphs.Device(device, large ? 13 : 11, "BadgeText"));
        label = new TextBlock
        {
            Text = text,
            FontSize = large ? 12 : 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (large) label.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); // tabular-ish digits for the counter
        label.SetResourceReference(TextBlock.ForegroundProperty, "BadgeText");
        panel.Children.Add(label);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            Padding = large ? new Thickness(8, 2, 9, 2) : new Thickness(6, 1, 7, 1),
            Margin = new Thickness(0, 0, 6, 0),
            Child = panel,
            ToolTip = ActivityModel.Name(device),
        };
        chip.SetResourceReference(Border.BackgroundProperty, key);
        chip.SetResourceReference(Border.BorderBrushProperty, "Ink");
        return chip;
    }

    private static FrameworkElement AppIcon(AppIdentity app, double size)
    {
        if (app.Icon != null)
            return new Image { Source = app.Icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(2),
            Child = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)), FontWeight = FontWeights.Black, FontSize = size * 0.45,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        badge.SetResourceReference(Border.BorderBrushProperty, "Ink");
        return badge;
    }
}
