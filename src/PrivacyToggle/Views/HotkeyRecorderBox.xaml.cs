using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PrivacyToggle.Models;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>Click it, press a key combination, and it raises <see cref="HotkeyRecorded"/>.</summary>
public partial class HotkeyRecorderBox : UserControl
{
    private readonly KeyCaptureHook _capture;
    private HotkeySetting _hotkey = HotkeySetting.Default;
    private bool _recording;

    public event Action<HotkeySetting>? HotkeyRecorded;
    public event Action<bool>? RecordingChanged;

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        _capture = new KeyCaptureHook(Dispatcher);
        _capture.KeyPressed += OnKeyPressed;
        _capture.ModifiersChanged += OnModifiersChanged;
        Unloaded += (_, _) => _capture.Stop();
        UpdateDisplay();
    }

    public HotkeySetting Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; UpdateDisplay(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SetRecording(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetRecording(false);
    }

    // Keys are read by the low-level hook; just stop WPF from acting on them (e.g. Alt focusing a menu).
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_recording && e.Key != Key.Tab) e.Handled = true;
    }

    private void OnModifiersChanged(ModifierKeys mods)
    {
        if (!_recording) return;
        Display.Text = mods == ModifierKeys.None ? "Press a shortcut…" : PartialText(mods) + "…";
    }

    private void OnKeyPressed(ModifierKeys mods, Key key)
    {
        if (!_recording) return;

        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            EndRecording();
            return;
        }

        if (mods == ModifierKeys.None)
        {
            Display.Text = "Add Ctrl, Alt, Shift or Win";
            return;
        }

        var hotkey = HotkeySetting.From(mods, key);
        if (!hotkey.IsValid) return;

        EndRecording();
        HotkeyRecorded?.Invoke(hotkey);
    }

    private void EndRecording()
    {
        SetRecording(false);
        // Move focus to the window so a later click starts a new recording.
        if (Window.GetWindow(this) is { } window)
        {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), window);
            Keyboard.Focus(window);
        }
    }

    private void SetRecording(bool on)
    {
        if (_recording == on) return;
        _recording = on;
        if (on) _capture.Start(); else _capture.Stop();
        UpdateDisplay();
        RecordingChanged?.Invoke(on);
    }

    private void UpdateDisplay()
    {
        if (_recording)
        {
            Display.Text = "Press a shortcut…";
            Badge.Text = "Esc to cancel";
            Frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            Frame.BorderThickness = new Thickness(2);
        }
        else
        {
            Display.Text = _hotkey.ToString();
            Badge.Text = "Click to change";
            Frame.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
            Frame.BorderThickness = new Thickness(1);
        }
    }

    private static string PartialText(ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts) + "+";
    }
}
