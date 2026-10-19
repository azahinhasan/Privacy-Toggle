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

/// <summary>Owns the services and the tray icon and holds the app's behaviour.</summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<MicState, System.Drawing.Icon> _icons = new();
    private TaskbarIcon? _tray;
    private TrayMenu? _menu;
    private SettingsWindow? _settingsWindow;
    private OverlayWindow? _overlay;
    private bool _hotkeySuspended;
    private bool _disposed;

    public SettingsService Settings { get; } = new();
    public MicService Mic { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public PushToTalkService PushToTalk { get; private set; } = null!;
    public SoundService Sounds { get; } = new();

    /// <summary>Non-null when the hotkey could not be activated (e.g. used by another app).</summary>
    public string? HotkeyError { get; private set; }

    /// <summary>Mic state or hotkey status changed; Settings window listens to refresh its header.</summary>
    public event Action? StateChanged;

    public event Action? DevicesChanged;

    private AppSettings S => Settings.Current;

    public AppController(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start(bool minimizedArg)
    {
        Settings.Load();
        ThemeService.Initialize();
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

        // The Run key is the source of truth (the installer may have created it);
        // while the option is on, keep its path pointing at this EXE.
        S.StartWithWindows = S.StartWithWindows || StartupService.IsEnabled();
        if (S.StartWithWindows) StartupService.RefreshPath();
        Settings.Save();

        CreateTray();

        Hotkeys = new HotkeyService();
        Hotkeys.Pressed += UserToggle;
        PushToTalk = new PushToTalkService(_dispatcher);
        PushToTalk.HeldChanged += held => SetMutedByUser(!held, notify: false);
        ApplyHotkey();

        OnMicStateChanged();

        if (HotkeyError != null)
            Notify("Hotkey unavailable", HotkeyError);

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
        _icons[MicState.Live] = Load("mic-on.ico");
        _icons[MicState.Muted] = Load("mic-off.ico");
        _icons[MicState.NoDevice] = Load("mic-none.ico");
    }

    private void CreateTray()
    {
        _menu = new TrayMenu(this);
        _tray = new TaskbarIcon
        {
            Icon = (System.Drawing.Icon)_icons[MicState.NoDevice].Clone(),
            ToolTipText = "PrivacyToggle",
            ContextMenu = _menu,
            MenuActivation = PopupActivationMode.RightClick,
            LeftClickCommand = new RelayCommand(OnTrayClick),
            // Never wait for a possible double-click: the click acts at once (see OnTrayDoubleClick).
            NoLeftClickDelay = true,
        };
        ApplyClickMode();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void ApplyClickMode()
    {
        if (_tray == null) return;
        _tray.DoubleClickCommand = S.DoubleClickOpensSettings ? new RelayCommand(OnTrayDoubleClick) : null;
    }

    private long _lastClickTicks;
    private bool _mutedBeforeClick;

    private void OnTrayClick()
    {
        _lastClickTicks = Environment.TickCount64;
        _mutedBeforeClick = Mic.DesiredMuted;
        UserToggle();
    }

    /// <summary>
    /// The first click of a double-click already toggled; undo it quietly, then open Settings.
    /// This keeps single clicks instant instead of waiting out the double-click time.
    /// </summary>
    private void OnTrayDoubleClick()
    {
        long sinceClick = Environment.TickCount64 - _lastClickTicks;
        if (sinceClick <= NativeMethods.GetDoubleClickTime() + 200 && Mic.DesiredMuted != _mutedBeforeClick)
            Mic.SetMuted(_mutedBeforeClick);
        ShowSettings();
    }

    private string BuildTooltip()
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

    private void UpdateVisuals()
    {
        if (_tray != null)
        {
            // TaskbarIcon disposes the icon it replaces, so always hand it a copy.
            _tray.Icon = (System.Drawing.Icon)_icons[Mic.State].Clone();
            _tray.ToolTipText = BuildTooltip();
        }
        _menu?.Refresh();
        UpdateOverlay();
    }

    private void Notify(string title, string message)
    {
        try
        {
            _tray?.ShowNotification(title, message, NotificationIcon.None, sound: false);
        }
        catch { }
    }

    // ---------------- mute actions ----------------

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
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    // ---------------- hotkey ----------------

    private void ApplyHotkey()
    {
        Hotkeys.Unregister();
        PushToTalk.Stop();
        HotkeyError = null;

        if (_hotkeySuspended) return;

        if (S.HotkeyMode == HotkeyMode.Toggle)
        {
            if (!Hotkeys.Register(S.Hotkey))
                HotkeyError = $"{S.Hotkey} is already in use by another app. Pick a different hotkey in Settings.";
        }
        else if (!PushToTalk.Start(S.Hotkey))
        {
            HotkeyError = "Could not start push-to-talk (keyboard hook failed).";
        }
    }

    /// <summary>Called while the Settings recorder box captures keys, so the current hotkey doesn't fire.</summary>
    public void SuspendHotkey(bool suspend)
    {
        _hotkeySuspended = suspend;
        var previousError = HotkeyError;
        ApplyHotkey();
        if (suspend) HotkeyError = previousError;
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    /// <summary>Tries a new hotkey. Returns false (and keeps the old one) if another app owns it.</summary>
    public bool TryChangeHotkey(HotkeySetting hotkey)
    {
        if (S.HotkeyMode == HotkeyMode.Toggle)
        {
            // Probe the combination before committing to it.
            Hotkeys.Unregister();
            PushToTalk.Stop();
            bool ok = Hotkeys.Register(hotkey);
            Hotkeys.Unregister();
            if (!ok)
            {
                ApplyHotkey();
                return false;
            }
        }

        S.Hotkey = hotkey;
        Settings.Save();
        ApplyHotkey();
        UpdateVisuals();
        StateChanged?.Invoke();
        return true;
    }

    public void SetHotkeyMode(HotkeyMode mode)
    {
        if (S.HotkeyMode == mode) return;
        S.HotkeyMode = mode;
        Settings.Save();
        ApplyHotkey();
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
        _menu?.Refresh();
        StateChanged?.Invoke();
    }

    public void SetDoubleClickOpensSettings(bool on)
    {
        S.DoubleClickOpensSettings = on;
        Settings.Save();
        ApplyClickMode();
    }

    public void SetEnforceMute(bool on)
    {
        S.EnforceMute = on;
        Mic.EnforceMute = on;
        Settings.Save();
    }

    public void SetDeviceTargets(DeviceMode mode, IEnumerable<string> selectedIds)
    {
        S.DeviceMode = mode;
        S.SelectedDeviceIds = selectedIds.ToList();
        Settings.Save();
        Mic.SetTargets(S.DeviceMode, S.SelectedDeviceIds);
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
        bool show = S.ShowOverlay && Mic.State == MicState.Muted;
        if (show)
        {
            _overlay ??= new OverlayWindow();
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
        _tray?.Dispose(); // removes the icon so no "ghost" stays in the tray
        Sounds.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }
}
