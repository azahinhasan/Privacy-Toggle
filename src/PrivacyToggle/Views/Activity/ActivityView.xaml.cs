using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>
/// The Activity page: "Right now" (one card each for the microphone, camera and network) and "History"
/// (a timeline or a list of sessions). This file holds the page plumbing; the cards and the history
/// live in ActivityView.Cards.cs and ActivityView.History.cs.
/// </summary>
public partial class ActivityView : UserControl
{
    private readonly DispatcherTimer _clock;    // 1 s: timers, rates, the live zone
    private readonly DispatcherTimer _refresh;  // 30 s: the history window slides with "now"
    private AppController? _app;
    private ActivityRange _range = ActivityRange.Hours24;
    private ActivityFilter _filter = ActivityFilter.All;
    private ActivitySort _sort = ActivitySort.LiveFirst;
    private bool _rangeChosen;
    private bool _loading = true;

    public ActivityView()
    {
        InitializeComponent();
        MicChipGlyph.Content = Glyphs.Device(UsageDevice.Microphone, 13);
        CamChipGlyph.Content = Glyphs.Device(UsageDevice.Camera, 13);
        NetChipGlyph.Content = Glyphs.Network(13);

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _clock.Stop();
        _refresh.Stop();

        BuildCards();
        SizeChanged += (_, _) => LayoutCards();
        _loading = false;
    }

    public void Attach(AppController app)
    {
        _app = app;
        _app.Usage.Changed += OnUsageChanged;
    }

    public void Detach()
    {
        if (_app != null) _app.Usage.Changed -= OnUsageChanged;
        Stop();
    }

    /// <summary>The page became visible (or hidden). Everything that ticks only runs while it's showing.</summary>
    public void SetActive(bool active)
    {
        if (!active)
        {
            Stop();
            CollapseCards(); // expanded cards reset when the page is left
            return;
        }
        if (_app == null) return;
        if (!_rangeChosen)
        {
            // Open on the smallest range that holds all of today's activity.
            _range = ActivityModel.DefaultRange(_app.Usage.Sessions, _app.Usage.Blocked, DateTime.Now);
            _rangeChosen = true;
        }
        SyncRangeButtons();
        Refresh();
        _clock.Start();
        _refresh.Start();
        StartMeters();
    }

    private void Stop()
    {
        _clock.Stop();
        _refresh.Stop();
        StopMeters();
    }

    /// <summary>Showing and not minimized (a minimized window still counts as visible to WPF).</summary>
    private bool Active => _app != null && IsVisible && Window.GetWindow(this)?.WindowState != WindowState.Minimized;

    private void OnUsageChanged()
    {
        if (Active) Refresh();
    }

    /// <summary>Windows' "Animation effects" setting is off: no pulsing dots or moving meters (timers still tick).</summary>
    private static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

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
        _filter = sender == FilterMic ? ActivityFilter.Microphone
                : sender == FilterCam ? ActivityFilter.Camera
                : sender == FilterNet ? ActivityFilter.Network
                : ActivityFilter.All;
        Refresh(animate: true);
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SortBox == null) return;
        _sort = SortBox.SelectedIndex switch
        {
            1 => ActivitySort.MostData,
            2 => ActivitySort.LongestMic,
            3 => ActivitySort.Name,
            _ => ActivitySort.LiveFirst,
        };
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

    /// <summary>
    /// The active filter chip takes its resource's colour ("All" stays orange). Its glyph, normally in that colour,
    /// switches to ink while selected so it doesn't vanish into the chip.
    /// </summary>
    private void ColorFilterChips()
    {
        void Tint(RadioButton chip, string key)
        {
            chip.Resources.Remove(key);
            if (TryFindResource(key) is { } brush) chip.Resources["ChipSelectedBg"] = brush;
            if (chip.IsChecked == true && TryFindResource("AccentText") is { } ink) chip.Resources[key] = ink;
        }
        Tint(FilterMic, "MicUsage");
        Tint(FilterCam, "CamUsage");
        Tint(FilterNet, "NetUsage");
    }

    // ---------------- header ----------------

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        var menu = PrivacyButton.ContextMenu;
        menu.PlacementTarget = PrivacyButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MicPrivacy_Click(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-microphone");
    private void CamPrivacy_Click(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-webcam");
    private void NetPrivacy_Click(object sender, RoutedEventArgs e) => Open("ms-settings:network-status");

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ---------------- refresh ----------------

    private void Refresh(bool animate = false)
    {
        if (_app == null) return;
        var now = DateTime.Now;
        ColorFilterChips();
        RefreshCards(now);
        RefreshHistory(now);

        if (animate && !ReduceMotion)
        {
            ContentHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() });
        }
    }

    private void Tick()
    {
        if (!Active) return;
        var now = DateTime.Now;
        RefreshCards(now);
        TickHistory(now);
    }
}
