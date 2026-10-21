using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;
using PrivacyToggle.Services;
using PrivacyToggle.Tray;
using PrivacyToggle.Views;

namespace PrivacyToggle;

/// <summary>Owns the services and the tray icons and holds the app's behaviour.</summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<MicState, System.Drawing.Icon> _micIcons = new();
    private readonly Dictionary<CameraState, System.Drawing.Icon> _camIcons = new();
    private TaskbarIcon? _micTray, _camTray;
    private TrayMenu? _micMenu, _camMenu;
    private SettingsWindow? _settingsWindow;
    private OverlayWindow? _overlay;
    private bool _hotkeySuspended;
    private bool _disposed;

    public SettingsService Settings { get; } = new();
    public MicService Mic { get; private set; } = null!;
    public CameraService Camera { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public PushToTalkService PushToTalk { get; private set; } = null!;
    public SoundService Sounds { get; } = new();

    /// <summary>Which apps used the camera/microphone and when (Activity page).</summary>
    public UsageTracker Usage { get; private set; } = null!;

    /// <summary>Non-null when the mic hotkey could not be activated (e.g. used by another app).</summary>
    public string? HotkeyError { get; private set; }

    /// <summary>Non-null when the camera hotkey could not be activated.</summary>
    public string? CameraHotkeyError { get; private set; }

    /// <summary>Mic/camera state or hotkey status changed; the Settings window refreshes on this.</summary>
    public event Action? StateChanged;

    /// <summary>The microphone list changed.</summary>
    public event Action? DevicesChanged;

    /// <summary>The camera list changed.</summary>
    public event Action? CameraDevicesChanged;

    private AppSettings S => Settings.Current;

    public AppController(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start(bool minimizedArg)
    {
        Settings.Load();
        ThemeService.Initialize(S.Theme);
        LoadIcons();

        Mic = new MicService(_dispatcher) { EnforceMute = S.EnforceMute };
        Mic.SetTargets(S.DeviceMode, S.SelectedDeviceIds);
        Mic.SetMuted(S.HotkeyMode == HotkeyMode.PushToTalk || S.OnStartState switch
        {
            OnStartState.Muted => true,
            OnStartState.Unmuted => false,
            _ => S.LastMuted,
        });
        Mic.StateChanged += OnMicStateChanged;
        Mic.DevicesChanged += () => DevicesChanged?.Invoke();

        Camera = new CameraService(_dispatcher) { EnforceOff = S.EnforceCameraOff };
        Camera.SetTargets(S.CameraDeviceMode, S.SelectedCameraIds, apply: false);
        Camera.StateChanged += OnCameraStateChanged;
        Camera.DevicesChanged += () => CameraDevicesChanged?.Invoke();
        _ = Camera.StartAsync(S.CameraOnStartState switch
        {
            CameraStartState.Off => true,
            CameraStartState.On => false,
            _ => S.LastCameraOff,
        });

        Usage = new UsageTracker(_dispatcher, Settings.FolderPath);
        UpdateBlocked();

        // The Run key is the source of truth (the installer may have created it);
        // while the option is on, keep its path pointing at this EXE.
        S.StartWithWindows = S.StartWithWindows || StartupService.IsEnabled();
        if (S.StartWithWindows) StartupService.RefreshPath();
        Settings.Save();

        UpdateTrays();

        Hotkeys = new HotkeyService();
        Hotkeys.Pressed += id =>
        {
            if (id == HotkeyService.MicId) UserToggle();
            else if (id == HotkeyService.CameraId) UserToggleCamera();
        };
        PushToTalk = new PushToTalkService(_dispatcher);
        PushToTalk.HeldChanged += held => SetMutedByUser(!held, notify: false);
        ApplyHotkeys();

        OnMicStateChanged();

        if (HotkeyError != null)
            Notify("Hotkey unavailable", HotkeyError);
        else if (CameraHotkeyError != null)
            Notify("Hotkey unavailable", CameraHotkeyError);

        if (!minimizedArg && !S.StartMinimized)
            ShowSettings();
    }

    // ---------------- tray ----------------

    private void LoadIcons()
    {
        int size = Math.Max(16, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON));
        System.Drawing.Icon Load(string name)
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"));
            using var stream = info!.Stream;
            return new System.Drawing.Icon(stream, new System.Drawing.Size(size, size));
        }
        _micIcons[MicState.Live] = Load("mic-on.ico");
        _micIcons[MicState.Muted] = Load("mic-off.ico");
        _micIcons[MicState.NoDevice] = Load("mic-none.ico");
        _camIcons[CameraState.On] = Load("cam-on.ico");
        _camIcons[CameraState.Off] = Load("cam-off.ico");
        _camIcons[CameraState.NoDevice] = Load("cam-none.ico");
        _camIcons[CameraState.Unavailable] = _camIcons[CameraState.NoDevice];
    }

    /// <summary>Creates or removes each tray icon to match the settings.</summary>
    private void UpdateTrays()
    {
        if (S.ShowMicTrayIcon && _micTray == null)
        {
            _micMenu = new TrayMenu(this);
            _micTray = new TaskbarIcon
            {
                Id = StableTrayId("microphone"),
                Icon = (System.Drawing.Icon)_micIcons[Mic.State].Clone(),
                ToolTipText = "PrivacyToggle microphone",
                ContextMenu = _micMenu,
                MenuActivation = PopupActivationMode.RightClick,
                LeftClickCommand = new RelayCommand(OnMicTrayClick),
                // Never wait for a possible double-click: the click acts at once (see OnMicTrayDoubleClick).
                NoLeftClickDelay = true,
            };
            _micTray.ForceCreate(enablesEfficiencyMode: false);
        }
        else if (!S.ShowMicTrayIcon && _micTray != null)
        {
            _micTray.Dispose();
            _micTray = null;
            _micMenu = null;
        }

        if (S.ShowCameraTrayIcon && _camTray == null)
        {
            _camMenu = new TrayMenu(this);
            _camTray = new TaskbarIcon
            {
                Id = StableTrayId("camera"),
                Icon = (System.Drawing.Icon)_camIcons[Camera.State].Clone(),
                ToolTipText = "PrivacyToggle camera",
                ContextMenu = _camMenu,
                MenuActivation = PopupActivationMode.RightClick,
                LeftClickCommand = new RelayCommand(UserToggleCamera),
            };
            _camTray.ForceCreate(enablesEfficiencyMode: false);
        }
        else if (!S.ShowCameraTrayIcon && _camTray != null)
        {
            _camTray.Dispose();
            _camTray = null;
            _camMenu = null;
        }

        ApplyClickMode();
        UpdateVisuals();
    }

    /// <summary>
    /// Each tray icon needs its own fixed ID: icons without one share the same default identity,
    /// so the second replaces the first. Windows ties an ID to the EXE path, so derive it from both.
    /// </summary>
    private static Guid StableTrayId(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"PrivacyToggle|{name}|{Environment.ProcessPath}".ToUpperInvariant()));
        return new Guid(bytes);
    }

    private void ApplyClickMode()
    {
        if (_micTray != null)
            _micTray.DoubleClickCommand = S.DoubleClickOpensSettings ? new RelayCommand(OnMicTrayDoubleClick) : null;
        if (_camTray != null)
        {
            // The camera icon always acts at once. Undoing a camera switch on double-click would turn the
            // device off and on again (dropping any call), so double-click-to-Settings is mic-icon only;
            // the second click of a double-click is ignored while the first switch is still running.
            _camTray.NoLeftClickDelay = true;
            _camTray.DoubleClickCommand = null;
        }
    }

    private long _lastClickTicks;
    private bool _mutedBeforeClick;

    private void OnMicTrayClick()
    {
        _lastClickTicks = Environment.TickCount64;
        _mutedBeforeClick = Mic.DesiredMuted;
        UserToggle();
    }

    /// <summary>
    /// The first click of a double-click already toggled; undo it quietly, then open Settings.
    /// This keeps single clicks instant instead of waiting out the double-click time.
    /// </summary>
    private void OnMicTrayDoubleClick()
    {
        long sinceClick = Environment.TickCount64 - _lastClickTicks;
        if (sinceClick <= NativeMethods.GetDoubleClickTime() + 200 && Mic.DesiredMuted != _mutedBeforeClick)
            Mic.SetMuted(_mutedBeforeClick);
        ShowSettings();
    }

    private string BuildMicTooltip()
    {
        string hk = S.Hotkey.ToString();
        bool ptt = S.HotkeyMode == HotkeyMode.PushToTalk;
        string hint = HotkeyError != null ? "click to {0} (hotkey unavailable)"
            : ptt ? $"hold {hk} to talk"
            : $"{hk} to {{0}}";

        return Mic.State switch
        {
            MicState.NoDevice => "PrivacyToggle — no microphone found",
            MicState.Muted => "Mic OFF — " + string.Format(hint, "unmute"),
            _ => ptt && HotkeyError == null ? "Mic ON — push-to-talk" : "Mic ON — " + string.Format(hint, "mute"),
        };
    }

    private string BuildCameraTooltip()
    {
        string hint = CameraHotkeyError != null ? "click to {0}" : $"{S.CameraHotkey} to {{0}}";
        if (Camera.Busy) return Camera.DesiredOff ? "Camera — turning off…" : "Camera — turning on…";
        return Camera.State switch
        {
            CameraState.Unavailable => "Camera control not installed — open Settings",
            CameraState.NoDevice => "PrivacyToggle — no camera found",
            CameraState.Off => "Camera OFF — " + string.Format(hint, "turn on"),
            _ => "Camera ON — " + string.Format(hint, "turn off"),
        };
    }

    /// <summary>
    /// While a switch is running, show where it is heading so the icon reacts instantly like the mic's;
    /// if Windows refuses, the next update shows the real state again.
    /// </summary>
    private CameraState DisplayedCameraState =>
        Camera.Busy && Camera.State is CameraState.On or CameraState.Off
            ? (Camera.DesiredOff ? CameraState.Off : CameraState.On)
            : Camera.State;

    private void UpdateVisuals()
    {
        if (_micTray != null)
        {
            // TaskbarIcon disposes the icon it replaces, so always hand it a copy.
            _micTray.Icon = (System.Drawing.Icon)_micIcons[Mic.State].Clone();
            _micTray.ToolTipText = BuildMicTooltip();
        }
        if (_camTray != null && Camera != null)
        {
            _camTray.Icon = (System.Drawing.Icon)_camIcons[DisplayedCameraState].Clone();
            _camTray.ToolTipText = BuildCameraTooltip();
        }
        _micMenu?.Refresh();
        _camMenu?.Refresh();
        UpdateOverlay();
    }

    private void Notify(string title, string message)
    {
        try
        {
            (_micTray ?? _camTray)?.ShowNotification(title, message, NotificationIcon.None, sound: false);
        }
        catch { }
    }

    // ---------------- mic actions ----------------

    public void UserToggle()
    {
        bool target = Mic.State switch
        {
            MicState.Muted => false,
            MicState.Live => true,
            _ => !Mic.DesiredMuted,
        };
        SetMutedByUser(target, notify: true);
    }

    private void SetMutedByUser(bool muted, bool notify)
    {
        bool before = Mic.DesiredMuted;
        Mic.SetMuted(muted);
        if (Mic.DesiredMuted == before && S.HotkeyMode == HotkeyMode.PushToTalk) return;

        if (S.PlaySound) Sounds.Play(Mic.DesiredMuted);
        if (notify && S.ShowNotification)
        {
            Notify("PrivacyToggle", Mic.State switch
            {
                MicState.NoDevice => "No microphone found",
                MicState.Muted => "Microphone muted",
                _ => "Microphone is live",
            });
        }
    }

    private void OnMicStateChanged()
    {
        if (S.HotkeyMode == HotkeyMode.Toggle && S.LastMuted != Mic.DesiredMuted)
        {
            S.LastMuted = Mic.DesiredMuted;
            Settings.Save();
        }
        UpdateBlocked();
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Tells the usage tracker when a device is fully blocked: every microphone muted / every camera off.
    /// Apps holding a blocked device get silence or nothing, so that time isn't counted as use.
    /// (If only some mics are muted, an app could still hear through another one, so that still counts.)
    /// </summary>
    private void UpdateBlocked()
    {
        if (Usage == null) return;
        if (Mic != null)
        {
            var mics = Mic.Devices;
            Usage.SetBlocked(UsageDevice.Microphone, mics.Count > 0 && mics.All(d => d.Muted));
        }
        if (Camera != null && Camera.State != CameraState.Unavailable && !Camera.Busy)
        {
            var cams = Camera.Devices;
            Usage.SetBlocked(UsageDevice.Camera, cams.Count > 0 && cams.All(d => !d.Enabled));
        }
    }

    // ---------------- camera actions ----------------

    public async void UserToggleCamera()
    {
        if (Camera.State == CameraState.Unavailable)
        {
            Notify("Camera control not installed",
                "Camera on/off needs the PrivacyToggle helper, which the installer sets up.");
            return;
        }
        if (Camera.Busy) return;

        bool off = Camera.State switch
        {
            CameraState.Off => false,
            CameraState.On => true,
            _ => !Camera.DesiredOff,
        };
        // Feedback right away (like the mic); Windows takes a moment to actually switch the device.
        if (S.PlaySound && Camera.State is CameraState.On or CameraState.Off) Sounds.Play(off, camera: true);
        await Camera.SetOffAsync(off);

        // A program holds the camera and refused: ask before closing it, then try again.
        if (off && Camera.LastBlocker is { } blocker)
        {
            if (!BlockerDialog.Ask(blocker)) return;
            await Camera.CloseBlockerAndTurnOffAsync(blocker.ProcessId);
        }

        if (Camera.LastError != null)
            Notify("Camera", "Couldn't switch the camera: " + Camera.LastError);
        else if (Camera.NeedsRestart)
            Notify("Camera", "Windows needs a restart to finish switching the camera.");
        else if (S.ShowNotification)
            Notify("PrivacyToggle", Camera.State switch
            {
                CameraState.NoDevice => "No camera found",
                CameraState.Off => "Camera turned off",
                _ => "Camera is on",
            });
    }

    private void OnCameraStateChanged()
    {
        if (Camera.State is CameraState.On or CameraState.Off && !Camera.Busy && S.LastCameraOff != Camera.DesiredOff)
        {
            S.LastCameraOff = Camera.DesiredOff;
            Settings.Save();
        }
        UpdateBlocked();
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    // ---------------- hotkeys ----------------

    private void ApplyHotkeys()
    {
        Hotkeys.UnregisterAll();
        PushToTalk.Stop();
        HotkeyError = null;
        CameraHotkeyError = null;

        if (_hotkeySuspended) return;

        if (S.HotkeyMode == HotkeyMode.Toggle)
        {
            if (!Hotkeys.Register(HotkeyService.MicId, S.Hotkey))
                HotkeyError = $"{S.Hotkey} is already in use by another app. Pick a different hotkey in Settings.";
        }
        else if (!PushToTalk.Start(S.Hotkey))
        {
            HotkeyError = "Could not start push-to-talk (keyboard hook failed).";
        }

        if (!Hotkeys.Register(HotkeyService.CameraId, S.CameraHotkey))
            CameraHotkeyError = $"{S.CameraHotkey} is already in use by another app. Pick a different hotkey in Settings.";
    }

    /// <summary>Called while a Settings recorder box captures keys, so the current hotkeys don't fire.</summary>
    public void SuspendHotkey(bool suspend)
    {
        _hotkeySuspended = suspend;
        var (micError, camError) = (HotkeyError, CameraHotkeyError);
        ApplyHotkeys();
        if (suspend) (HotkeyError, CameraHotkeyError) = (micError, camError);
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    /// <summary>Tries a new hotkey. Returns null on success, or why it was rejected (the old one stays).</summary>
    public string? TryChangeHotkey(HotkeySetting hotkey, bool camera)
    {
        var other = camera ? S.Hotkey : S.CameraHotkey;
        if (hotkey.SameAs(other))
            return $"{hotkey} is already the {(camera ? "microphone" : "camera")} shortcut.";

        if (camera || S.HotkeyMode == HotkeyMode.Toggle)
        {
            // Probe the combination before committing to it.
            Hotkeys.UnregisterAll();
            PushToTalk.Stop();
            bool ok = Hotkeys.Register(HotkeyService.MicId, hotkey);
            Hotkeys.UnregisterAll();
            if (!ok)
            {
                ApplyHotkeys();
                return $"Hotkey already in use: {hotkey} is taken by another app.";
            }
        }

        if (camera) S.CameraHotkey = hotkey; else S.Hotkey = hotkey;
        Settings.Save();
        ApplyHotkeys();
        UpdateVisuals();
        StateChanged?.Invoke();
        return null;
    }

    public void SetHotkeyMode(HotkeyMode mode)
    {
        if (S.HotkeyMode == mode) return;
        S.HotkeyMode = mode;
        Settings.Save();
        ApplyHotkeys();
        if (mode == HotkeyMode.PushToTalk) Mic.SetMuted(true); // push-to-talk rests muted
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    // ---------------- settings actions ----------------

    public void SetStartWithWindows(bool on)
    {
        S.StartWithWindows = on;
        StartupService.SetEnabled(on);
        Settings.Save();
        _micMenu?.Refresh();
        _camMenu?.Refresh();
        StateChanged?.Invoke();
    }

    public void SetDoubleClickOpensSettings(bool on)
    {
        S.DoubleClickOpensSettings = on;
        Settings.Save();
        ApplyClickMode();
    }

    public void SetTheme(ThemeMode mode)
    {
        S.Theme = mode;
        Settings.Save();
        ThemeService.SetMode(mode);
    }

    public void SetShowMicTrayIcon(bool on)
    {
        S.ShowMicTrayIcon = on;
        Settings.Save();
        UpdateTrays();
    }

    public void SetShowCameraTrayIcon(bool on)
    {
        S.ShowCameraTrayIcon = on;
        Settings.Save();
        UpdateTrays();
    }

    public void SetEnforceMute(bool on)
    {
        S.EnforceMute = on;
        Mic.EnforceMute = on;
        Settings.Save();
    }

    public void SetEnforceCameraOff(bool on)
    {
        S.EnforceCameraOff = on;
        Camera.EnforceOff = on;
        Settings.Save();
    }

    public void SetDeviceTargets(DeviceMode mode, IEnumerable<string> selectedIds)
    {
        S.DeviceMode = mode;
        S.SelectedDeviceIds = selectedIds.ToList();
        Settings.Save();
        Mic.SetTargets(S.DeviceMode, S.SelectedDeviceIds);
    }

    public void SetCameraTargets(DeviceMode mode, IEnumerable<string> selectedIds)
    {
        S.CameraDeviceMode = mode;
        S.SelectedCameraIds = selectedIds.ToList();
        Settings.Save();
        Camera.SetTargets(S.CameraDeviceMode, S.SelectedCameraIds);
    }

    public void SetOverlay(bool show, OverlayCorner corner)
    {
        S.ShowOverlay = show;
        S.OverlayCorner = corner;
        Settings.Save();
        UpdateOverlay();
    }

    /// <summary>Saves simple flags that need no further action.</summary>
    public void Save() => Settings.Save();

    // ---------------- windows ----------------

    public void ShowSettings()
    {
        if (_disposed) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;  // nudge to the front, then release
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    private void UpdateOverlay()
    {
        bool micOff = Mic?.State == MicState.Muted;
        bool camOff = Camera?.State == CameraState.Off;
        if (S.ShowOverlay && (micOff || camOff))
        {
            _overlay ??= new OverlayWindow();
            _overlay.SetBadges(micOff, camOff);
            _overlay.Place(S.OverlayCorner);
            if (!_overlay.IsVisible) _overlay.Show();
        }
        else
        {
            _overlay?.Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Push-to-talk keeps mics muted only while PrivacyToggle runs; don't leave them stuck muted.
        if (S.HotkeyMode == HotkeyMode.PushToTalk && Mic != null)
            Mic.SetMuted(false);

        _settingsWindow?.Close();
        _overlay?.Close();
        Hotkeys?.Dispose();
        PushToTalk?.Dispose();
        Mic?.Dispose();
        Camera?.Dispose();
        Usage?.Dispose();
        _micTray?.Dispose(); // removes the icons so no "ghost" stays in the tray
        _camTray?.Dispose();
        Sounds.Dispose();
        foreach (var icon in _micIcons.Values.Concat(_camIcons.Values).Distinct()) icon.Dispose();
    }
}
