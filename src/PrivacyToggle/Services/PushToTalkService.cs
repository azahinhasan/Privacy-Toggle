using System.Runtime.InteropServices;
using System.Windows.Threading;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;

namespace PrivacyToggle.Services;

/// <summary>
/// Push-to-talk via a low-level keyboard hook: raises <see cref="HeldChanged"/>(true) when the
/// combination goes down and (false) when its key is released. The key itself is swallowed while held.
/// </summary>
public sealed class PushToTalkService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep the delegate alive
    private IntPtr _hook;
    private HotkeySetting? _hotkey;
    private uint _vk;
    private bool _held;

    public event Action<bool>? HeldChanged;

    public bool IsRunning => _hook != IntPtr.Zero;

    public PushToTalkService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookProc;
    }

    public bool Start(HotkeySetting hotkey)
    {
        Stop();
        if (!hotkey.IsValid) return false;
        _hotkey = hotkey;
        _vk = (uint)hotkey.VirtualKey;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (_held)
        {
            _held = false;
            Raise(false);
        }
    }

    private bool ModifiersHeld()
    {
        var h = _hotkey!;
        if (h.Ctrl && !NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)) return false;
        if (h.Alt && !NativeMethods.IsKeyDown(NativeMethods.VK_MENU)) return false;
        if (h.Shift && !NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT)) return false;
        if (h.Win && !NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) && !NativeMethods.IsKeyDown(NativeMethods.VK_RWIN)) return false;
        return true;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == _vk)
            {
                int msg = wParam.ToInt32();
                bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

                if (down)
                {
                    if (!_held && ModifiersHeld())
                    {
                        _held = true;
                        Raise(true);
                        if (_hotkey!.Win) NativeMethods.SuppressStartMenu();
                    }
                    if (_held) return (IntPtr)1; // swallow (also auto-repeat)
                }
                else if (up && _held)
                {
                    _held = false;
                    Raise(false);
                    return (IntPtr)1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // The hook must return quickly, so the real work runs later on the UI thread.
    private void Raise(bool held) => _dispatcher.BeginInvoke(() => HeldChanged?.Invoke(held));

    public void Dispose() => Stop();
}
