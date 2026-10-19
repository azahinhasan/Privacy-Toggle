using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using PrivacyToggle.Models;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _app;
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

        // An Image given the .ico directly would scale up the 16px frame; pick the largest one.
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        AboutIcon.Source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

        LoadValues();
        RefreshStatus();
        RefreshDevices();

        Recorder.HotkeyRecorded += OnHotkeyRecorded;
        Recorder.RecordingChanged += recording => _app.SuspendHotkey(recording);

        _app.StateChanged += RefreshStatus;
        _app.DevicesChanged += RefreshDevices;
        ThemeService.Changed += OnThemeChanged;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Closed += (_, _) =>
        {
            _app.StateChanged -= RefreshStatus;
            _app.DevicesChanged -= RefreshDevices;
            ThemeService.Changed -= OnThemeChanged;
        };
    }

    private void OnThemeChanged() => ThemeService.ApplyTitleBar(this);

    private void LoadValues()
    {
        _loading = true;

        StartWithWindowsBox.IsChecked = S.StartWithWindows;
        StartMinimizedBox.IsChecked = S.StartMinimized;
        StartKeepLast.IsChecked = S.OnStartState == OnStartState.KeepLast;
        StartUnmuted.IsChecked = S.OnStartState == OnStartState.Unmuted;
        StartMuted.IsChecked = S.OnStartState == OnStartState.Muted;
        DoubleClickBox.IsChecked = S.DoubleClickOpensSettings;

        Recorder.Hotkey = S.Hotkey;
        ModeToggle.IsChecked = S.HotkeyMode == HotkeyMode.Toggle;
        ModePtt.IsChecked = S.HotkeyMode == HotkeyMode.PushToTalk;

        PlaySoundBox.IsChecked = S.PlaySound;
        NotificationBox.IsChecked = S.ShowNotification;
        OverlayBox.IsChecked = S.ShowOverlay;
        CornerTL.IsChecked = S.OverlayCorner == OverlayCorner.TopLeft;
        CornerTR.IsChecked = S.OverlayCorner == OverlayCorner.TopRight;
        CornerBL.IsChecked = S.OverlayCorner == OverlayCorner.BottomLeft;
        CornerBR.IsChecked = S.OverlayCorner == OverlayCorner.BottomRight;

        DevAll.IsChecked = S.DeviceMode == DeviceMode.All;
        DevSelected.IsChecked = S.DeviceMode == DeviceMode.Selected;
        EnforceBox.IsChecked = S.EnforceMute;

        _loading = false;
        UpdateDependentControls();
    }

    private void UpdateDependentControls()
    {
        CornerPanel.IsEnabled = OverlayBox.IsChecked == true;
        DeviceListPanel.IsEnabled = DevSelected.IsChecked == true;
        SelectNoneWarning.Visibility = S.DeviceMode == DeviceMode.Selected && S.SelectedDeviceIds.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Text = S.HotkeyMode == HotkeyMode.Toggle
            ? $"Press {S.Hotkey} once to mute and again to unmute."
            : $"Microphones stay muted and are live only while you hold {S.Hotkey}. The key press is not passed on to other apps.";
    }

    // ---------------- status ----------------

    private void RefreshStatus()
    {
        var state = _app.Mic.State;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state switch
        {
            MicState.Muted => "MutedColor",
            MicState.Live => "LiveColor",
            _ => "NoneColor",
        });
        StatusText.Text = state switch
        {
            MicState.Muted => "Mic is muted",
            MicState.Live => "Mic is live",
            _ => "No microphone",
        };
        int count = _app.Mic.Devices.Count;
        StatusDetail.Text = state == MicState.NoDevice
            ? "Plug in a microphone or check the Microphones page."
            : S.HotkeyMode == HotkeyMode.PushToTalk ? $"Hold {S.Hotkey} to talk" : $"{S.Hotkey} to toggle";
        ToggleButton.Content = state == MicState.Muted ? "Unmute" : "Mute";

        HotkeyWarning.Text = _app.HotkeyError ?? "";
        HotkeyWarning.Visibility = _app.HotkeyError == null ? Visibility.Collapsed : Visibility.Visible;

        _loading = true;
        StartWithWindowsBox.IsChecked = S.StartWithWindows;
        _loading = false;
    }

    private void RefreshDevices()
    {
        var devices = _app.Mic.Devices;
        var selected = S.SelectedDeviceIds.ToHashSet();
        DeviceList.Items.Clear();
        foreach (var d in devices.OrderBy(d => d.Name))
        {
            var box = new CheckBox
            {
                Style = (Style)FindResource("CheckItem"),
                Content = d.Name,
                Tag = d.Id,
                IsChecked = selected.Contains(d.Id),
            };
            box.Checked += DeviceCheck_Changed;
            box.Unchecked += DeviceCheck_Changed;
            DeviceList.Items.Add(box);
        }
        NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatus();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => _app.UserToggle();

    // ---------------- navigation ----------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageGeneral == null) return; // during InitializeComponent
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageHotkey.Visibility = NavHotkey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageFeedback.Visibility = NavFeedback.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = NavDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- general ----------------

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetStartWithWindows(StartWithWindowsBox.IsChecked == true);
    }

    private void DoubleClick_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetDoubleClickOpensSettings(DoubleClickBox.IsChecked == true);
    }

    private void OnStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.OnStartState = StartMuted.IsChecked == true ? OnStartState.Muted
            : StartUnmuted.IsChecked == true ? OnStartState.Unmuted
            : OnStartState.KeepLast;
        _app.Save();
    }

    /// <summary>Flags that only need saving.</summary>
    private void Simple_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.StartMinimized = StartMinimizedBox.IsChecked == true;
        S.PlaySound = PlaySoundBox.IsChecked == true;
        S.ShowNotification = NotificationBox.IsChecked == true;
        _app.Save();
    }

    // ---------------- hotkey ----------------

    private void OnHotkeyRecorded(HotkeySetting hotkey)
    {
        if (_app.TryChangeHotkey(hotkey))
        {
            Recorder.Hotkey = hotkey;
            HotkeyWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            Recorder.Hotkey = S.Hotkey;
            HotkeyWarning.Text = $"Hotkey already in use: {hotkey} is taken by another app. Keeping {S.Hotkey}.";
            HotkeyWarning.Visibility = Visibility.Visible;
        }
        UpdateDependentControls();
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e) => OnHotkeyRecorded(HotkeySetting.Default);

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetHotkeyMode(ModePtt.IsChecked == true ? HotkeyMode.PushToTalk : HotkeyMode.Toggle);
        UpdateDependentControls();
    }

    // ---------------- feedback ----------------

    private void Overlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var corner = CornerTL.IsChecked == true ? OverlayCorner.TopLeft
            : CornerBL.IsChecked == true ? OverlayCorner.BottomLeft
            : CornerBR.IsChecked == true ? OverlayCorner.BottomRight
            : OverlayCorner.TopRight;
        _app.SetOverlay(OverlayBox.IsChecked == true, corner);
        UpdateDependentControls();
    }

    // ---------------- devices ----------------

    private void Devices_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveDeviceTargets();
    }

    private void DeviceCheck_Changed(object sender, RoutedEventArgs e) => SaveDeviceTargets();

    private void SaveDeviceTargets()
    {
        var present = DeviceList.Items.OfType<CheckBox>().ToList();
        var presentIds = present.Select(b => (string)b.Tag).ToHashSet();
        // Keep selections for mics that are currently unplugged.
        var ids = S.SelectedDeviceIds.Where(id => !presentIds.Contains(id))
            .Concat(present.Where(b => b.IsChecked == true).Select(b => (string)b.Tag));
        _app.SetDeviceTargets(DevSelected.IsChecked == true ? DeviceMode.Selected : DeviceMode.All, ids);
        UpdateDependentControls();
    }

    private void Enforce_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetEnforceMute(EnforceBox.IsChecked == true);
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
