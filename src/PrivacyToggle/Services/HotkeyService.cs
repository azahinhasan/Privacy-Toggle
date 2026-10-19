using System.Windows.Interop;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;

namespace PrivacyToggle.Services;

/// <summary>System-wide hotkey via RegisterHotKey on a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4D4D;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("PrivacyToggleHotkeyWindow")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers the hotkey, replacing any previous one. Returns false if another app owns the combination.</summary>
    public bool Register(HotkeySetting hotkey)
    {
        Unregister();
        if (!hotkey.IsValid) return false;

        uint mods = NativeMethods.MOD_NOREPEAT;
        if (hotkey.Ctrl) mods |= NativeMethods.MOD_CONTROL;
        if (hotkey.Alt) mods |= NativeMethods.MOD_ALT;
        if (hotkey.Shift) mods |= NativeMethods.MOD_SHIFT;
        if (hotkey.Win) mods |= NativeMethods.MOD_WIN;

        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, mods, (uint)hotkey.VirtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
