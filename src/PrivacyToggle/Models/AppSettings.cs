namespace PrivacyToggle.Models;

public enum OnStartState { KeepLast, Unmuted, Muted }
public enum HotkeyMode { Toggle, PushToTalk }
public enum DeviceMode { All, Selected }
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public OnStartState OnStartState { get; set; } = OnStartState.KeepLast;
    public bool LastMuted { get; set; }

    public HotkeySetting Hotkey { get; set; } = HotkeySetting.Default;
    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Toggle;

    /// <summary>When on, a single click waits for the double-click time so a double-click can open Settings.</summary>
    public bool DoubleClickOpensSettings { get; set; }

    public bool PlaySound { get; set; } = true;
    public bool ShowNotification { get; set; }
    public bool ShowOverlay { get; set; }
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopRight;

    public DeviceMode DeviceMode { get; set; } = DeviceMode.All;
    public List<string> SelectedDeviceIds { get; set; } = new();
    public bool EnforceMute { get; set; } = true;
}
