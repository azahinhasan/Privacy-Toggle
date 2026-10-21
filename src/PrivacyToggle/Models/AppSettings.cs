using System.Text.Json.Serialization;

namespace PrivacyToggle.Models;

public enum OnStartState { KeepLast, Unmuted, Muted }
public enum HotkeyMode { Toggle, PushToTalk }
public enum DeviceMode { All, Selected }
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }
public enum CameraStartState { KeepLast, On, Off }
public enum ThemeMode { System, Light, Dark }

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;

    /// <summary>Follow the Windows app theme, or force light / dark.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public OnStartState OnStartState { get; set; } = OnStartState.KeepLast;
    public bool LastMuted { get; set; }

    public HotkeySetting Hotkey { get; set; } = HotkeySetting.Default;
    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Toggle;

    /// <summary>When on, a single click waits for the double-click time so a double-click can open Settings.</summary>
    public bool DoubleClickOpensSettings { get; set; }

    public bool PlaySound { get; set; } = true;
    // ---- alerts, per device ----
    public bool MicNotify { get; set; }
    public bool CamNotify { get; set; }
    public bool MicOverlay { get; set; }
    public bool CamOverlay { get; set; }
    public OverlayCorner MicOverlayCorner { get; set; } = OverlayCorner.TopRight;
    public OverlayCorner CamOverlayCorner { get; set; } = OverlayCorner.TopRight;

    // Before 1.2 these were shared by both devices. Read once from old settings files and copied to
    // both devices (see SettingsService.Load); never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowNotification { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowOverlay { get; set; }
    [JsonPropertyName("overlayCorner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverlayCorner? LegacyOverlayCorner { get; set; }

    public DeviceMode DeviceMode { get; set; } = DeviceMode.All;
    public List<string> SelectedDeviceIds { get; set; } = new();
    public bool EnforceMute { get; set; } = true;

    // ---- camera ----
    public HotkeySetting CameraHotkey { get; set; } = HotkeySetting.CameraDefault;
    public CameraStartState CameraOnStartState { get; set; } = CameraStartState.KeepLast;

    /// <summary>Null until the camera was first toggled; KeepLast then simply follows the devices.</summary>
    public bool? LastCameraOff { get; set; }

    public DeviceMode CameraDeviceMode { get; set; } = DeviceMode.All;
    public List<string> SelectedCameraIds { get; set; } = new();
    public bool EnforceCameraOff { get; set; } = true;
    public bool ShowCameraTrayIcon { get; set; } = true;
    public bool ShowMicTrayIcon { get; set; } = true;
}
