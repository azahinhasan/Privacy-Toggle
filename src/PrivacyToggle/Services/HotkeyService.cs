using System.Windows.Interop;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;

namespace PrivacyToggle.Services;

/// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    public const int MicId = 0x4D4D;
    public const int CameraId = 0x4D4E;
    public const int NetworkId = 0x4D4F;

    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = new();

    /// <summary>Raised with the id of the hotkey that was pressed.</summary>
    public event Action<int>? Pressed;

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

    /// <summary>Registers a hotkey under <paramref name="id"/>, replacing any previous one. Returns false if another app owns the combination.</summary>
    public bool Register(int id, HotkeySetting hotkey)
    {
        Unregister(id);
        if (!hotkey.IsValid) return false;

        uint mods = NativeMethods.MOD_NOREPEAT;
        if (hotkey.Ctrl) mods |= NativeMethods.MOD_CONTROL;
        if (hotkey.Alt) mods |= NativeMethods.MOD_ALT;
        if (hotkey.Shift) mods |= NativeMethods.MOD_SHIFT;
        if (hotkey.Win) mods |= NativeMethods.MOD_WIN;

        if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods, (uint)hotkey.VirtualKey)) return false;
        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
            NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.ToList()) Unregister(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.Contains(wParam.ToInt32()))
        {
            handled = true;
            Pressed?.Invoke(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
