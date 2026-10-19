using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using PrivacyToggle.Interop;

namespace PrivacyToggle.Services;

/// <summary>
/// Captures key presses system-wide while the hotkey recorder is active. A low-level hook sees keys
/// before Windows dispatches registered hotkeys, so combinations owned by other apps can still be
/// recorded (and then reported as "already in use").
/// </summary>
public sealed class KeyCaptureHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    /// <summary>A non-modifier key went down, with the modifiers held at that moment.</summary>
    public event Action<ModifierKeys, Key>? KeyPressed;

    /// <summary>The set of held modifiers changed.</summary>
    public event Action<ModifierKeys>? ModifiersChanged;

    public KeyCaptureHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookProc;
    }

    public void Start()
    {
        if (_hook == IntPtr.Zero)
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if (NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)) m |= ModifierKeys.Control;
        if (NativeMethods.IsKeyDown(NativeMethods.VK_MENU)) m |= ModifierKeys.Alt;
        if (NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT)) m |= ModifierKeys.Shift;
        if (NativeMethods.IsWinDown()) m |= ModifierKeys.Windows;
        return m;
    }

    private static bool IsModifierVk(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or >= 0xA0 and <= 0xA5; // L/R Shift, Ctrl, Alt

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;
            int msg = wParam.ToInt32();
            bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool injectedFiller = vk == 0xE8;

            if (IsModifierVk(vk))
            {
                // State as it will be after this event (GetAsyncKeyState still shows the old one).
                var mods = CurrentModifiers();
                var flag = vk switch
                {
                    0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
                    0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
                    0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
                    _ => ModifierKeys.Windows,
                };
                mods = down ? mods | flag : mods & ~flag;
                _dispatcher.BeginInvoke(() => ModifiersChanged?.Invoke(mods));
            }
            else if (down && !injectedFiller)
            {
                var mods = CurrentModifiers();
                // Let plain Tab through so keyboard users can leave the box.
                if (!(vk == 0x09 && mods == ModifierKeys.None))
                {
                    var key = KeyInterop.KeyFromVirtualKey(vk);
                    _dispatcher.BeginInvoke(() => KeyPressed?.Invoke(mods, key));
                    if (mods.HasFlag(ModifierKeys.Windows)) NativeMethods.SuppressStartMenu();
                    return (IntPtr)1; // swallow: don't type into apps or trigger their shortcuts
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
