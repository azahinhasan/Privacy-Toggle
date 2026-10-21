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
        RefreshStatus();

        Recorder.HotkeyRecorded += h => OnHotkeyRecorded(h, camera: false);
        Recorder.RecordingChanged += recording => _app.SuspendHotkey(recording);
        CamRecorder.HotkeyRecorded += h => OnHotkeyRecorded(h, camera: true);
        CamRecorder.RecordingChanged += recording => _app.SuspendHotkey(recording);

        ActivityPage.Attach(_app.Usage);
        _ = CheckForUpdatesAsync(); // quietly, so the About item can show "NEW"

        _app.StateChanged += RefreshStatus;
        _app.DevicesChanged += RefreshDevices;
        _app.CameraDevicesChanged += RefreshCameras;
        ThemeService.Changed += OnThemeChanged;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Closed += (_, _) =>
        {
            ActivityPage.Detach();
            _app.StateChanged -= RefreshStatus;
            _app.DevicesChanged -= RefreshDevices;
            _app.CameraDevicesChanged -= RefreshCameras;
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

        // shortcuts
        Recorder.Hotkey = S.Hotkey;
        CamRecorder.Hotkey = S.CameraHotkey;
        ModeToggle.IsChecked = S.HotkeyMode == HotkeyMode.Toggle;
        ModePtt.IsChecked = S.HotkeyMode == HotkeyMode.PushToTalk;

        // alerts (per device) + sound
        PlaySoundBox.IsChecked = S.PlaySound;
        MicNotifyBox.IsChecked = S.MicNotify;
        MicOverlayBox.IsChecked = S.MicOverlay;
        SetCorner(MicCorners, S.MicOverlayCorner);
        CamNotifyBox.IsChecked = S.CamNotify;
        CamOverlayBox.IsChecked = S.CamOverlay;
        SetCorner(CamCorners, S.CamOverlayCorner);

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
        DeviceListPanel.IsEnabled = DevSelected.IsChecked == true;
        CamListPanel.IsEnabled = CamDevSelected.IsChecked == true;
        SelectNoneWarning.Visibility = S.DeviceMode == DeviceMode.Selected && S.SelectedDeviceIds.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        CamSelectNoneWarning.Visibility = S.CameraDeviceMode == DeviceMode.Selected && S.SelectedCameraIds.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Text = S.HotkeyMode == HotkeyMode.Toggle
            ? $"Press {S.Hotkey} once to mute and again to unmute."
            : $"Microphones stay muted and are live only while you hold {S.Hotkey}. The key press is not passed on to other apps.";
        CamHotkeyHint.Text = $"Press {S.CameraHotkey} to turn the camera off, and again to turn it back on.";
        NoTrayWarning.Visibility = !S.ShowMicTrayIcon && !S.ShowCameraTrayIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- status ----------------

    private void RefreshStatus()
    {
        // microphone
        var mic = _app.Mic.State;
        string micColor = mic switch { MicState.Muted => "MutedColor", MicState.Live => "LiveColor", _ => "NoneColor" };
        MicDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, micColor);
        MicStateBadge.SetResourceReference(Border.BackgroundProperty, micColor.Replace("Color", "Fill"));
        MicChip.Text = mic switch { MicState.Muted => "Mic muted", MicState.Live => "Mic live", _ => "No microphone" };
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
        CamDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, camColor);
        CamStateBadge.SetResourceReference(Border.BackgroundProperty, camColor.Replace("Color", "Fill"));
        CamChip.Text = cam switch
        {
            CameraState.Off => "Camera off",
            CameraState.On => "Camera on",
            CameraState.NoDevice => "No camera",
            _ => "Camera control not installed",
        };
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
            ? "Camera helper: not installed"
            : "Camera helper: running";

        // shortcuts
        HotkeyWarning.Text = _app.HotkeyError ?? "";
        HotkeyWarning.Visibility = _app.HotkeyError == null ? Visibility.Collapsed : Visibility.Visible;
        CamHotkeyWarning.Text = _app.CameraHotkeyError ?? "";
        CamHotkeyWarning.Visibility = _app.CameraHotkeyError == null ? Visibility.Collapsed : Visibility.Visible;

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

    private void MicCardButton_Click(object sender, RoutedEventArgs e) => _app.UserToggle();
    private void CamCardButton_Click(object sender, RoutedEventArgs e) => _app.UserToggleCamera();

    // ---------------- navigation ----------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview == null) return; // during InitializeComponent
        var pages = new (RadioButton Nav, FrameworkElement Page)[]
        {
            (NavOverview, PageOverview), (NavActivity, PageActivity), (NavMic, PageMic), (NavCamera, PageCamera), (NavShortcuts, PageShortcuts),
            (NavGeneral, PageGeneral), (NavAbout, PageAbout),
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

    // ---------------- shortcuts ----------------

    private void OnHotkeyRecorded(HotkeySetting hotkey, bool camera)
    {
        var error = _app.TryChangeHotkey(hotkey, camera);
        var recorder = camera ? CamRecorder : Recorder;
        var warning = camera ? CamHotkeyWarning : HotkeyWarning;
        recorder.Hotkey = camera ? S.CameraHotkey : S.Hotkey;
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

    private void ResetHotkey_Click(object sender, RoutedEventArgs e) => OnHotkeyRecorded(HotkeySetting.Default, camera: false);
    private void ResetCamHotkey_Click(object sender, RoutedEventArgs e) => OnHotkeyRecorded(HotkeySetting.CameraDefault, camera: true);

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

    private static void SetCorner((RadioButton Button, OverlayCorner Corner)[] buttons, OverlayCorner corner)
    {
        foreach (var (button, c) in buttons) button.IsChecked = c == corner;
    }

    private static OverlayCorner GetCorner((RadioButton Button, OverlayCorner Corner)[] buttons) =>
        buttons.FirstOrDefault(b => b.Button.IsChecked == true) is { Button: not null } chosen ? chosen.Corner : OverlayCorner.TopRight;

    /// <summary>Any alert switch or badge corner changed on either page: save both devices' choices.</summary>
    private void Alerts_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetAlerts(UsageDevice.Microphone, MicNotifyBox.IsChecked == true, MicOverlayBox.IsChecked == true, GetCorner(MicCorners));
        _app.SetAlerts(UsageDevice.Camera, CamNotifyBox.IsChecked == true, CamOverlayBox.IsChecked == true, GetCorner(CamCorners));
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
                : $"You have {current}. This release has no installer attached; download it from the release page.";
            UpdateButton.Content = info.InstallerUrl != null ? $"Update to {info.Latest}" : "Open release page";
            UpdateButton.Visibility = Visibility.Visible;
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
        if (info == null || _updateBusy) return;
        if (info.InstallerUrl == null)
        {
            OpenShell(info.PageUrl);
            return;
        }

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
