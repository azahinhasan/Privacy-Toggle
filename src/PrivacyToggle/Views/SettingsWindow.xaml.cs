using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using PrivacyToggle.Models;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _app;
    private readonly Dictionary<string, ImageSource> _images = new();
    private bool _loading;

    private AppSettings S => _app.Settings.Current;

    public SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        string v = version == null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        VersionText.Text = $"Version {v}";
        SidebarVersion.Text = $"PrivacyToggle {v}";
        AboutIcon.Source = BrandIcon.Source = Image("app.ico");

        LoadValues();
        RefreshDevices();
        RefreshCameras();
        RefreshNetwork();
        RefreshStatus();

        Recorder.HotkeyRecorded += h => OnHotkeyRecorded(h, PrivacyDevice.Microphone);
        Recorder.RecordingChanged += recording => _app.SuspendHotkey(recording);
        CamRecorder.HotkeyRecorded += h => OnHotkeyRecorded(h, PrivacyDevice.Camera);
        CamRecorder.RecordingChanged += recording => _app.SuspendHotkey(recording);
        NetRecorder.HotkeyRecorded += h => OnHotkeyRecorded(h, PrivacyDevice.Network);
        NetRecorder.RecordingChanged += recording => _app.SuspendHotkey(recording);

        ActivityPage.Attach(_app);
        ActivityPage.NetworkDetailsRequested += () => ShowPage(PrivacyDevice.Network);
        _ = CheckForUpdatesAsync(); // quietly, so the About item can show "NEW"

        _app.StateChanged += RefreshStatus;
        _app.Usage.Changed += RefreshSidebar;
        _app.NetworkUsage.Changed += RefreshSidebar;
        _app.DevicesChanged += RefreshDevices;
        _app.CameraDevicesChanged += RefreshCameras;
        _app.NetworkAdaptersChanged += RefreshNetwork;
        ThemeService.Changed += OnThemeChanged;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Closed += (_, _) =>
        {
            ActivityPage.Detach();
            _app.StateChanged -= RefreshStatus;
            _app.Usage.Changed -= RefreshSidebar;
            _app.NetworkUsage.Changed -= RefreshSidebar;
            _app.DevicesChanged -= RefreshDevices;
            _app.CameraDevicesChanged -= RefreshCameras;
            _app.NetworkAdaptersChanged -= RefreshNetwork;
            ThemeService.Changed -= OnThemeChanged;
        };
    }

    private void OnThemeChanged() => ThemeService.ApplyTitleBar(this);

    // ---------------- custom title bar ----------------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        // A maximized custom-chrome window hangs a few pixels past the screen edges; pull the content in.
        Frame.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Data = System.Windows.Media.Geometry.Parse(max
            ? "M3,0.5 H11.5 V9 M0.5,3 H9 V11.5 H0.5 Z"   // restore: two overlapping boxes
            : "M0.5,0.5 H11.5 V11.5 H0.5 Z");
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
    }

    /// <summary>The largest frame of an .ico (an Image given the .ico directly would upscale the 16px one).</summary>
    private ImageSource Image(string name)
    {
        if (!_images.TryGetValue(name, out var source))
        {
            var decoder = BitmapDecoder.Create(new Uri($"pack://application:,,,/Assets/{name}"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            _images[name] = source;
        }
        return source;
    }

    private void LoadValues()
    {
        _loading = true;

        // microphone
        StartKeepLast.IsChecked = S.OnStartState == OnStartState.KeepLast;
        StartUnmuted.IsChecked = S.OnStartState == OnStartState.Unmuted;
        StartMuted.IsChecked = S.OnStartState == OnStartState.Muted;
        DevAll.IsChecked = S.DeviceMode == DeviceMode.All;
        DevSelected.IsChecked = S.DeviceMode == DeviceMode.Selected;
        EnforceBox.IsChecked = S.EnforceMute;

        // camera
        CamStartKeepLast.IsChecked = S.CameraOnStartState == CameraStartState.KeepLast;
        CamStartOn.IsChecked = S.CameraOnStartState == CameraStartState.On;
        CamStartOff.IsChecked = S.CameraOnStartState == CameraStartState.Off;
        CamDevAll.IsChecked = S.CameraDeviceMode == DeviceMode.All;
        CamDevSelected.IsChecked = S.CameraDeviceMode == DeviceMode.Selected;
        CamEnforceBox.IsChecked = S.EnforceCameraOff;

        // network
        NetStartKeepLast.IsChecked = S.NetworkOnStartState == CameraStartState.KeepLast;
        NetStartOn.IsChecked = S.NetworkOnStartState == CameraStartState.On;
        NetStartOff.IsChecked = S.NetworkOnStartState == CameraStartState.Off;
        NetDevAll.IsChecked = S.NetworkDeviceMode == DeviceMode.All;
        NetDevSelected.IsChecked = S.NetworkDeviceMode == DeviceMode.Selected;
        NetEnforceBox.IsChecked = S.EnforceNetworkOff;
        NetTrayBox.IsChecked = S.ShowNetworkTrayIcon;
        TaskbarSpeedBox.IsChecked = S.TaskbarSpeed;
        TaskbarShowBoth.IsChecked = S.TaskbarSpeedShow == TaskbarSpeedShow.Both;
        TaskbarShowDown.IsChecked = S.TaskbarSpeedShow == TaskbarSpeedShow.Download;
        TaskbarShowUp.IsChecked = S.TaskbarSpeedShow == TaskbarSpeedShow.Upload;

        // shortcuts
        Recorder.Hotkey = S.Hotkey;
        CamRecorder.Hotkey = S.CameraHotkey;
        NetRecorder.Hotkey = S.NetworkHotkey;
        ModeToggle.IsChecked = S.HotkeyMode == HotkeyMode.Toggle;
        ModePtt.IsChecked = S.HotkeyMode == HotkeyMode.PushToTalk;

        // alerts (per device) + sound
        PlaySoundBox.IsChecked = S.PlaySound;
        MicNotifyBox.IsChecked = S.MicNotify;
        MicNotifyWindows.IsChecked = S.MicNotifyStyle == NotifyStyle.Windows;
        MicNotifyBadge.IsChecked = S.MicNotifyStyle == NotifyStyle.Badge;
        SetCorner(MicNotifyCorners, S.MicNotifyCorner);
        MicOverlayBox.IsChecked = S.MicOverlay;
        SetCorner(MicCorners, S.MicOverlayCorner);
        CamNotifyBox.IsChecked = S.CamNotify;
        CamNotifyWindows.IsChecked = S.CamNotifyStyle == NotifyStyle.Windows;
        CamNotifyBadge.IsChecked = S.CamNotifyStyle == NotifyStyle.Badge;
        SetCorner(CamNotifyCorners, S.CamNotifyCorner);
        CamOverlayBox.IsChecked = S.CamOverlay;
        SetCorner(CamCorners, S.CamOverlayCorner);
        NetNotifyBox.IsChecked = S.NetNotify;
        NetNotifyWindows.IsChecked = S.NetNotifyStyle == NotifyStyle.Windows;
        NetNotifyBadge.IsChecked = S.NetNotifyStyle == NotifyStyle.Badge;
        SetCorner(NetNotifyCorners, S.NetNotifyCorner);
        NetOverlayBox.IsChecked = S.NetOverlay;
        SetCorner(NetCorners, S.NetOverlayCorner);

        // general
        StartWithWindowsBox.IsChecked = S.StartWithWindows;
        StartMinimizedBox.IsChecked = S.StartMinimized;
        ThemeSystem.IsChecked = S.Theme == ThemeMode.System;
        ThemeLight.IsChecked = S.Theme == ThemeMode.Light;
        ThemeDark.IsChecked = S.Theme == ThemeMode.Dark;
        MicTrayBox.IsChecked = S.ShowMicTrayIcon;
        CamTrayBox.IsChecked = S.ShowCameraTrayIcon;
        DoubleClickBox.IsChecked = S.DoubleClickOpensSettings;

        _loading = false;
        UpdateDependentControls();
    }

    private void UpdateDependentControls()
    {
        MicCornerPanel.IsEnabled = MicOverlayBox.IsChecked == true;
        CamCornerPanel.IsEnabled = CamOverlayBox.IsChecked == true;
        MicNotifyStylePanel.IsEnabled = MicNotifyBox.IsChecked == true;
        CamNotifyStylePanel.IsEnabled = CamNotifyBox.IsChecked == true;
        MicNotifyCornerPanel.Visibility = MicNotifyBadge.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CamNotifyCornerPanel.Visibility = CamNotifyBadge.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NetCornerPanel.IsEnabled = NetOverlayBox.IsChecked == true;
        TaskbarSpeedShowPanel.IsEnabled = TaskbarSpeedBox.IsChecked == true;
        NetNotifyStylePanel.IsEnabled = NetNotifyBox.IsChecked == true;
        NetNotifyCornerPanel.Visibility = NetNotifyBadge.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DeviceListPanel.IsEnabled = DevSelected.IsChecked == true;
        CamListPanel.IsEnabled = CamDevSelected.IsChecked == true;
        NetListPanel.IsEnabled = NetDevSelected.IsChecked == true;
        NetSelectNoneWarning.Visibility = S.NetworkDeviceMode == DeviceMode.Selected && S.SelectedNetworkIds.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        NetHotkeyHint.Text = $"Press {S.NetworkHotkey} to disconnect, and again to reconnect.";
        SelectNoneWarning.Visibility = S.DeviceMode == DeviceMode.Selected && S.SelectedDeviceIds.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        CamSelectNoneWarning.Visibility = S.CameraDeviceMode == DeviceMode.Selected && S.SelectedCameraIds.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Text = S.HotkeyMode == HotkeyMode.Toggle
            ? $"Press {S.Hotkey} once to mute and again to unmute."
            : $"Microphones stay muted and are live only while you hold {S.Hotkey}. The key press is not passed on to other apps.";
        CamHotkeyHint.Text = $"Press {S.CameraHotkey} to turn the camera off, and again to turn it back on.";
        MicNoTrayWarning.Visibility = CamNoTrayWarning.Visibility =
            !S.ShowMicTrayIcon && !S.ShowCameraTrayIcon && !S.ShowNetworkTrayIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- status ----------------

    /// <summary>
    /// The sidebar status box with live counts: "Mic live · 2" / "Mic off", "Camera live · 1" / "Camera off",
    /// "Network · 7 apps" / "Network quiet". Runs on every usage change (the network one ticks every second).
    /// </summary>
    private void RefreshSidebar()
    {
        var live = ActivityModel.LiveNow(_app.Usage.Sessions, _app.Usage.Blocked, DateTime.Now);
        int micApps = live.Count(l => l.Spans.Any(s => s.Device == UsageDevice.Microphone));
        int camApps = live.Count(l => l.Spans.Any(s => s.Device == UsageDevice.Camera));
        int netApps = _app.NetworkUsage.Live.Count(l => l.DownRate + l.UpRate > ActivityView.NetworkActiveRate);

        void Set(System.Windows.Shapes.Ellipse dot, TextBlock chip, string text, string color)
        {
            chip.Text = text;
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, color);
        }

        switch (_app.Mic.State)
        {
            case MicState.Muted: Set(MicDot, MicChip, "Mic off", "StatusOff"); break;
            case MicState.NoDevice: Set(MicDot, MicChip, "No microphone", "NoneColor"); break;
            default: Set(MicDot, MicChip, micApps > 0 ? $"Mic live · {micApps}" : "Mic on", "StatusOn"); break;
        }
        switch (_app.Camera.State)
        {
            case CameraState.Off: Set(CamDot, CamChip, "Camera off", "StatusOff"); break;
            case CameraState.On: Set(CamDot, CamChip, camApps > 0 ? $"Camera live · {camApps}" : "Camera on", "StatusOn"); break;
            case CameraState.NoDevice: Set(CamDot, CamChip, "No camera", "NoneColor"); break;
            default: Set(CamDot, CamChip, "Camera control not installed", "NoneColor"); break;
        }
        if (_app.Network.State == NetworkState.Off) Set(NetDot, NetChip, "Network off", "StatusOff");
        else if (netApps > 0) Set(NetDot, NetChip, $"Network · {netApps} app{(netApps == 1 ? "" : "s")}", "NetUsage");
        else Set(NetDot, NetChip, "Network quiet", "NetUsage");
    }

    private void RefreshStatus()
    {
        RefreshSidebar();

        // microphone
        var mic = _app.Mic.State;
        string micColor = mic switch { MicState.Muted => "MutedColor", MicState.Live => "LiveColor", _ => "NoneColor" };
        MicStateBadge.SetResourceReference(Border.BackgroundProperty, micColor.Replace("Color", "Fill"));
        MicCardIcon.Source = Image(mic switch { MicState.Muted => "mic-off.ico", MicState.Live => "mic-on.ico", _ => "mic-none.ico" });
        MicCardState.Text = mic switch { MicState.Muted => "Muted", MicState.Live => "Live", _ => "Not found" };
        MicCardHint.Text = mic == MicState.NoDevice
            ? "Plug in a microphone."
            : S.HotkeyMode == HotkeyMode.PushToTalk ? $"Hold {S.Hotkey} to talk" : $"{S.Hotkey} to switch";
        int micCount = _app.Mic.Devices.Count;
        MicCardDevices.Text = micCount == 1 ? "1 microphone" : $"{micCount} microphones";
        MicCardButton.Content = mic == MicState.Muted ? "Unmute" : "Mute";
        MicCardButton.IsEnabled = mic != MicState.NoDevice;

        // camera
        var camService = _app.Camera;
        var cam = camService.State;
        string camColor = cam switch { CameraState.Off => "MutedColor", CameraState.On => "LiveColor", _ => "NoneColor" };
        CamStateBadge.SetResourceReference(Border.BackgroundProperty, camColor.Replace("Color", "Fill"));
        CamCardIcon.Source = Image(cam switch { CameraState.Off => "cam-off.ico", CameraState.On => "cam-on.ico", _ => "cam-none.ico" });
        CamCardState.Text = camService.Busy ? (camService.DesiredOff ? "Turning off…" : "Turning on…") : cam switch
        {
            CameraState.Off => "Off",
            CameraState.On => "On",
            CameraState.NoDevice => "Not found",
            _ => "Unavailable",
        };
        CamCardHint.Text = cam switch
        {
            CameraState.Unavailable => "Needs the helper service — see the Camera page.",
            CameraState.NoDevice => "Plug in a camera.",
            _ when camService.LastError != null => "Last switch failed: " + camService.LastError,
            _ when camService.NeedsRestart => "Windows needs a restart to finish the switch.",
            _ => $"{S.CameraHotkey} to switch",
        };
        int camCount = camService.Devices.Count;
        CamCardDevices.Text = cam == CameraState.Unavailable ? "Helper not installed" : camCount == 1 ? "1 camera" : $"{camCount} cameras";
        CamCardButton.Content = cam == CameraState.Off ? "Turn on" : "Turn off";
        CamCardButton.IsEnabled = !camService.Busy && cam is CameraState.On or CameraState.Off;
        CamHelperBanner.Visibility = cam == CameraState.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        HelperStatusText.Text = cam == CameraState.Unavailable
            ? "Helper service: not installed"
            : _app.Network.HelperOutdated ? "Helper service: running (an older version, without network control)"
            : "Helper service: running";

        // network
        var netService = _app.Network;
        var net = netService.State;
        string netColor = net switch { NetworkState.Off => "MutedColor", NetworkState.On => "LiveColor", _ => "NoneColor" };
        NetStateBadge.SetResourceReference(Border.BackgroundProperty, netColor.Replace("Color", "Fill"));
        NetCardIcon.Source = Image(net switch { NetworkState.Off => "net-off.ico", NetworkState.On => "net-on.ico", _ => "net-none.ico" });
        NetCardState.Text = netService.Busy ? (netService.DesiredOff ? "Disconnecting…" : "Reconnecting…") : net switch
        {
            NetworkState.Off => "Off",
            NetworkState.On => "On",
            NetworkState.NoDevice => "Not found",
            _ => "Unavailable",
        };
        NetCardHint.Text = net switch
        {
            NetworkState.Unavailable => netService.HelperOutdated
                ? "Needs the latest installer — see the Network page."
                : "Needs the helper service — see the Network page.",
            NetworkState.NoDevice => "No Wi-Fi or wired adapter found.",
            _ when netService.LastError != null => "Last switch failed: " + netService.LastError,
            _ when netService.NeedsRestart => "Windows needs a restart to finish the switch.",
            _ => $"{S.NetworkHotkey} to switch",
        };
        var targets = netService.Targets;
        int connected = targets.Count(a => a.Connected);
        NetCardDevices.Text = net == NetworkState.Unavailable ? "Helper needs updating"
            : targets.Count == 1 ? (connected == 1 ? "1 adapter · connected" : "1 adapter")
            : $"{targets.Count} adapters" + (connected > 0 ? $" · {connected} connected" : "");
        NetCardButton.Content = net == NetworkState.Off ? "Reconnect" : "Disconnect";
        NetCardButton.IsEnabled = !netService.Busy && net is NetworkState.On or NetworkState.Off;
        NetHelperBanner.Visibility = net == NetworkState.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        NetHelperBannerTitle.Text = netService.HelperOutdated ? "The helper service needs updating" : "Network control isn't installed";
        NetHelperBannerText.Text = netService.HelperOutdated
            ? "The PrivacyToggle helper service on this PC is from an older version that can't switch the network. Run the latest PrivacyToggle-Setup to update it."
            : "Turning the network off needs the PrivacyToggle helper service, which the installer sets up (it asks for admin rights once). Run PrivacyToggle-Setup to enable it.";

        // shortcuts
        HotkeyWarning.Text = _app.HotkeyError ?? "";
        HotkeyWarning.Visibility = _app.HotkeyError == null ? Visibility.Collapsed : Visibility.Visible;
        CamHotkeyWarning.Text = _app.CameraHotkeyError ?? "";
        CamHotkeyWarning.Visibility = _app.CameraHotkeyError == null ? Visibility.Collapsed : Visibility.Visible;
        NetHotkeyWarning.Text = _app.NetworkHotkeyError ?? "";
        NetHotkeyWarning.Visibility = _app.NetworkHotkeyError == null ? Visibility.Collapsed : Visibility.Visible;

        _loading = true;
        StartWithWindowsBox.IsChecked = S.StartWithWindows;
        _loading = false;
    }

    private void RefreshDevices()
    {
        FillChecklist(DeviceList, _app.Mic.Devices.Select(d => (d.Id, d.Name)), S.SelectedDeviceIds, DeviceCheck_Changed);
        NoDevicesText.Visibility = _app.Mic.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatus();
    }

    private void RefreshCameras()
    {
        FillChecklist(CamList, _app.Camera.Devices.Select(d => (d.Id, d.Name)), S.SelectedCameraIds, CamCheck_Changed);
        NoCamerasText.Visibility = _app.Camera.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatus();
    }

    private void RefreshNetwork()
    {
        FillChecklist(NetList, _app.Network.Adapters.Select(a => (a.Id, AdapterLabel(a))), S.SelectedNetworkIds, NetCheck_Changed);
        NoAdaptersText.Visibility = _app.Network.Adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatus();
    }

    /// <summary>"Wi-Fi · Intel(R) Wi-Fi 6E AX211 — connected".</summary>
    private static string AdapterLabel(Helper.NetworkAdapter a)
    {
        string kind = a.Kind switch { Helper.NetworkKind.WiFi => "Wi-Fi", Helper.NetworkKind.Mobile => "Mobile", _ => "Wired" };
        string state = !a.Enabled ? "off" : a.Connected ? "connected" : "not connected";
        string name = a.Alias.Equals(a.Name, StringComparison.OrdinalIgnoreCase) ? a.Name : $"{a.Alias} · {a.Name}";
        return $"{kind}: {name} — {state}";
    }

    private void FillChecklist(ItemsControl list, IEnumerable<(string Id, string Name)> devices, List<string> selectedIds,
        RoutedEventHandler changed)
    {
        var selected = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.Items.Clear();
        foreach (var (id, name) in devices.OrderBy(d => d.Name))
        {
            var box = new CheckBox
            {
                Style = (Style)FindResource("CheckItem"),
                Content = name,
                Tag = id,
                IsChecked = selected.Contains(id),
            };
            box.Checked += changed;
            box.Unchecked += changed;
            list.Items.Add(box);
        }
    }

    /// <summary>Switches to a device's page (e.g. the taskbar speed opens Network).</summary>
    public void ShowPage(PrivacyDevice device) =>
        (device switch { PrivacyDevice.Camera => NavCamera, PrivacyDevice.Network => NavNetwork, _ => NavMic }).IsChecked = true;

    /// <summary>Opens the Network page scrolled to the Taskbar speed settings (clicking the taskbar speed lands here).</summary>
    public void ShowTaskbarSpeedSettings()
    {
        ShowPage(PrivacyDevice.Network);
        // After the page has been laid out, so the section's position is known.
        Dispatcher.BeginInvoke(() =>
        {
            var top = TaskbarSpeedSection.TransformToAncestor(PageNetwork).Transform(new Point(0, 0)).Y;
            PageNetwork.ScrollToVerticalOffset(PageNetwork.VerticalOffset + top - 12);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MicCardButton_Click(object sender, RoutedEventArgs e) => _app.UserToggle();
    private void CamCardButton_Click(object sender, RoutedEventArgs e) => _app.UserToggleCamera();
    private void NetCardButton_Click(object sender, RoutedEventArgs e) => _app.UserToggleNetwork();

    // ---------------- navigation ----------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview == null) return; // during InitializeComponent
        var pages = new (RadioButton Nav, FrameworkElement Page)[]
        {
            (NavOverview, PageOverview), (NavActivity, PageActivity), (NavMic, PageMic), (NavCamera, PageCamera),
            (NavNetwork, PageNetwork), (NavGeneral, PageGeneral), (NavAbout, PageAbout),
        };
        foreach (var (nav, page) in pages)
            page.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.SetActive(NavActivity.IsChecked == true);
        if (NavAbout.IsChecked == true && !_updateBusy && DateTime.Now - UpdateService.LastChecked > TimeSpan.FromMinutes(10))
            _ = CheckForUpdatesAsync();
    }

    // ---------------- microphone ----------------

    private void OnStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.OnStartState = StartMuted.IsChecked == true ? OnStartState.Muted
            : StartUnmuted.IsChecked == true ? OnStartState.Unmuted
            : OnStartState.KeepLast;
        _app.Save();
    }

    private void Devices_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveDeviceTargets();
    }

    private void DeviceCheck_Changed(object sender, RoutedEventArgs e) => SaveDeviceTargets();

    private void SaveDeviceTargets()
    {
        var ids = SelectedIds(DeviceList, S.SelectedDeviceIds);
        _app.SetDeviceTargets(DevSelected.IsChecked == true ? DeviceMode.Selected : DeviceMode.All, ids);
        UpdateDependentControls();
    }

    /// <summary>Checked items, plus earlier selections for devices that are currently unplugged.</summary>
    private static List<string> SelectedIds(ItemsControl list, List<string> previous)
    {
        var present = list.Items.OfType<CheckBox>().ToList();
        var presentIds = present.Select(b => (string)b.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return previous.Where(id => !presentIds.Contains(id))
            .Concat(present.Where(b => b.IsChecked == true).Select(b => (string)b.Tag))
            .ToList();
    }

    private void Enforce_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetEnforceMute(EnforceBox.IsChecked == true);
    }

    // ---------------- camera ----------------

    private void CamOnStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CameraOnStartState = CamStartOff.IsChecked == true ? CameraStartState.Off
            : CamStartOn.IsChecked == true ? CameraStartState.On
            : CameraStartState.KeepLast;
        _app.Save();
    }

    private void CamDevices_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveCameraTargets();
    }

    private void CamCheck_Changed(object sender, RoutedEventArgs e) => SaveCameraTargets();

    private void SaveCameraTargets()
    {
        var ids = SelectedIds(CamList, S.SelectedCameraIds);
        _app.SetCameraTargets(CamDevSelected.IsChecked == true ? DeviceMode.Selected : DeviceMode.All, ids);
        UpdateDependentControls();
    }

    private void CamEnforce_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetEnforceCameraOff(CamEnforceBox.IsChecked == true);
    }

    // ---------------- network ----------------

    private void TaskbarSpeed_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetTaskbarSpeed(TaskbarSpeedBox.IsChecked == true,
            TaskbarShowDown.IsChecked == true ? TaskbarSpeedShow.Download
            : TaskbarShowUp.IsChecked == true ? TaskbarSpeedShow.Upload
            : TaskbarSpeedShow.Both);
        UpdateDependentControls();
    }

    private void NetOnStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.NetworkOnStartState = NetStartOff.IsChecked == true ? CameraStartState.Off
            : NetStartOn.IsChecked == true ? CameraStartState.On
            : CameraStartState.KeepLast;
        _app.Save();
    }

    private void NetDevices_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveNetworkTargets();
    }

    private void NetCheck_Changed(object sender, RoutedEventArgs e) => SaveNetworkTargets();

    private void SaveNetworkTargets()
    {
        var ids = SelectedIds(NetList, S.SelectedNetworkIds);
        _app.SetNetworkTargets(NetDevSelected.IsChecked == true ? DeviceMode.Selected : DeviceMode.All, ids);
        UpdateDependentControls();
    }

    private void NetEnforce_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetEnforceNetworkOff(NetEnforceBox.IsChecked == true);
    }

    private void NetTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetShowNetworkTrayIcon(NetTrayBox.IsChecked == true);
        UpdateDependentControls();
    }

    // ---------------- shortcuts ----------------

    private void OnHotkeyRecorded(HotkeySetting hotkey, PrivacyDevice target)
    {
        var error = _app.TryChangeHotkey(hotkey, target);
        var (recorder, warning) = target switch
        {
            PrivacyDevice.Camera => (CamRecorder, CamHotkeyWarning),
            PrivacyDevice.Network => (NetRecorder, NetHotkeyWarning),
            _ => (Recorder, HotkeyWarning),
        };
        recorder.Hotkey = _app.HotkeyFor(target);
        if (error == null)
        {
            warning.Visibility = Visibility.Collapsed;
        }
        else
        {
            warning.Text = $"{error} Keeping {recorder.Hotkey}.";
            warning.Visibility = Visibility.Visible;
        }
        UpdateDependentControls();
        RefreshStatus();
        if (error != null) warning.Visibility = Visibility.Visible; // RefreshStatus shows only runtime errors
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e) => OnHotkeyRecorded(HotkeySetting.Default, PrivacyDevice.Microphone);
    private void ResetCamHotkey_Click(object sender, RoutedEventArgs e) => OnHotkeyRecorded(HotkeySetting.CameraDefault, PrivacyDevice.Camera);
    private void ResetNetHotkey_Click(object sender, RoutedEventArgs e) => OnHotkeyRecorded(HotkeySetting.NetworkDefault, PrivacyDevice.Network);

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetHotkeyMode(ModePtt.IsChecked == true ? HotkeyMode.PushToTalk : HotkeyMode.Toggle);
        UpdateDependentControls();
    }

    // ---------------- alerts (Microphone and Camera pages) ----------------

    private (RadioButton Button, OverlayCorner Corner)[] MicCorners => new[]
    {
        (MicCornerTL, OverlayCorner.TopLeft), (MicCornerTR, OverlayCorner.TopRight),
        (MicCornerBL, OverlayCorner.BottomLeft), (MicCornerBR, OverlayCorner.BottomRight),
    };

    private (RadioButton Button, OverlayCorner Corner)[] CamCorners => new[]
    {
        (CamCornerTL, OverlayCorner.TopLeft), (CamCornerTR, OverlayCorner.TopRight),
        (CamCornerBL, OverlayCorner.BottomLeft), (CamCornerBR, OverlayCorner.BottomRight),
    };

    // The switch badge only offers the top corners.
    private (RadioButton Button, OverlayCorner Corner)[] MicNotifyCorners => new[]
    {
        (MicNotifyCornerTL, OverlayCorner.TopLeft), (MicNotifyCornerTR, OverlayCorner.TopRight),
    };

    private (RadioButton Button, OverlayCorner Corner)[] CamNotifyCorners => new[]
    {
        (CamNotifyCornerTL, OverlayCorner.TopLeft), (CamNotifyCornerTR, OverlayCorner.TopRight),
    };

    private (RadioButton Button, OverlayCorner Corner)[] NetCorners => new[]
    {
        (NetCornerTL, OverlayCorner.TopLeft), (NetCornerTR, OverlayCorner.TopRight),
        (NetCornerBL, OverlayCorner.BottomLeft), (NetCornerBR, OverlayCorner.BottomRight),
    };

    private (RadioButton Button, OverlayCorner Corner)[] NetNotifyCorners => new[]
    {
        (NetNotifyCornerTL, OverlayCorner.TopLeft), (NetNotifyCornerTR, OverlayCorner.TopRight),
    };

    private static void SetCorner((RadioButton Button, OverlayCorner Corner)[] buttons, OverlayCorner corner)
    {
        foreach (var (button, c) in buttons) button.IsChecked = c == corner;
    }

    private static OverlayCorner GetCorner((RadioButton Button, OverlayCorner Corner)[] buttons) =>
        buttons.FirstOrDefault(b => b.Button.IsChecked == true) is { Button: not null } chosen ? chosen.Corner : OverlayCorner.TopRight;

    /// <summary>Any alert switch or badge corner changed on any page: save every device's choices.</summary>
    private void Alerts_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetAlerts(PrivacyDevice.Microphone, MicNotifyBox.IsChecked == true,
            MicNotifyBadge.IsChecked == true ? NotifyStyle.Badge : NotifyStyle.Windows, GetCorner(MicNotifyCorners),
            MicOverlayBox.IsChecked == true, GetCorner(MicCorners));
        _app.SetAlerts(PrivacyDevice.Camera, CamNotifyBox.IsChecked == true,
            CamNotifyBadge.IsChecked == true ? NotifyStyle.Badge : NotifyStyle.Windows, GetCorner(CamNotifyCorners),
            CamOverlayBox.IsChecked == true, GetCorner(CamCorners));
        _app.SetAlerts(PrivacyDevice.Network, NetNotifyBox.IsChecked == true,
            NetNotifyBadge.IsChecked == true ? NotifyStyle.Badge : NotifyStyle.Windows, GetCorner(NetNotifyCorners),
            NetOverlayBox.IsChecked == true, GetCorner(NetCorners));
        UpdateDependentControls();
    }

    // ---------------- general ----------------

    /// <summary>Flags that only need saving.</summary>
    private void Simple_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.StartMinimized = StartMinimizedBox.IsChecked == true;
        S.PlaySound = PlaySoundBox.IsChecked == true;
        _app.Save();
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetStartWithWindows(StartWithWindowsBox.IsChecked == true);
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetTheme(ThemeLight.IsChecked == true ? ThemeMode.Light : ThemeDark.IsChecked == true ? ThemeMode.Dark : ThemeMode.System);
    }

    private void MicTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetShowMicTrayIcon(MicTrayBox.IsChecked == true);
        UpdateDependentControls();
    }

    private void CamTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetShowCameraTrayIcon(CamTrayBox.IsChecked == true);
        UpdateDependentControls();
    }

    private void DoubleClick_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetDoubleClickOpensSettings(DoubleClickBox.IsChecked == true);
    }

    // ---------------- updates ----------------

    private bool _updateBusy;

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync();

    /// <summary>Asks GitHub for the latest release and shows either "up to date" or an Update button.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateStatus.Text = "Checking for updates…";
        UpdateDetail.Text = "";
        CheckUpdatesButton.IsEnabled = false;
        UpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            ShowUpdateResult(await UpdateService.CheckAsync());
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Couldn't check for updates";
            UpdateDetail.Text = ex is HttpRequestException or TaskCanceledException
                ? "Check your internet connection and try again."
                : ex.Message;
            ReleaseLinkText.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void ShowUpdateResult(UpdateInfo info)
    {
        var current = UpdateService.Current;
        ReleaseLink.NavigateUri = new Uri(info.PageUrl);
        ReleaseLinkText.Visibility = Visibility.Visible;
        if (info.IsNewer)
        {
            UpdateStatus.Text = $"Version {info.Latest} is available";
            UpdateDetail.Text = info.InstallerUrl != null
                ? $"You have {current}. Update downloads the installer from GitHub ({info.InstallerSize / 1048576.0:0} MB), checks it, and installs it; PrivacyToggle restarts afterwards."
                : $"You have {current}. This release has no installer attached; download it from the link below.";
            // Without an installer there's nothing to install here; the "What's new" link below opens the release.
            UpdateButton.Content = $"Update to {info.Latest}";
            UpdateButton.Visibility = info.InstallerUrl != null ? Visibility.Visible : Visibility.Collapsed;
            ReleaseLinkRun.Text = $"What's new in {info.Tag}";
            AboutUpdateBadge.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatus.Text = "You're up to date";
            UpdateDetail.Text = $"PrivacyToggle {current} is the latest version (checked {UpdateService.LastChecked:t}).";
            UpdateButton.Visibility = Visibility.Collapsed;
            ReleaseLinkRun.Text = $"Release notes for {info.Tag}";
            AboutUpdateBadge.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Downloads the new installer, verifies it, starts it, and closes PrivacyToggle so it can be replaced.</summary>
    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        var info = UpdateService.LastResult;
        if (info?.InstallerUrl == null || _updateBusy) return;

        _updateBusy = true;
        UpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatus.Text = $"Downloading {info.Latest}…";
        UpdateDetail.Text = "";
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateDetail.Text = $"{p * 100:0}% of {info.InstallerSize / 1048576.0:0} MB";
            });
            string installer = await UpdateService.DownloadAsync(info, progress);

            UpdateStatus.Text = "Starting the installer…";
            UpdateDetail.Text = "Windows will ask for permission. PrivacyToggle closes now and restarts when the update is done.";
            if (UpdateService.StartInstaller(installer))
            {
                await Task.Delay(800); // let the installer window appear first
                Application.Current.Shutdown();
                return;
            }
            UpdateStatus.Text = "Update cancelled";
            UpdateDetail.Text = "The installer needs permission to run. Click Update to try again.";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Update failed";
            UpdateDetail.Text = ex is HttpRequestException ? "The download failed. Check your internet connection and try again." : ex.Message;
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- about ----------------

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenShell(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(_app.Settings.FolderPath);
        OpenShell(_app.Settings.FolderPath);
    }

    private static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
