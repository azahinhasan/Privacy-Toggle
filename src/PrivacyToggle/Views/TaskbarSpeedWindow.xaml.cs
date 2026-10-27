using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>
/// The current download/upload speed, shown on the taskbar just left of the system tray. Windows 11 has no
/// way for apps to add to the taskbar itself, so this is a small window kept on top of it (like TrafficMonitor
/// does); clicking it opens the Network page. It steps aside the moment a fullscreen app is in front, and
/// follows an auto-hiding taskbar as it slides in and out.
/// </summary>
public partial class TaskbarSpeedWindow : Window
{
    private static readonly Brush LightText = Frozen(Colors.White), DarkText = Frozen(Color.FromRgb(0x11, 0x11, 0x11));

    private readonly NetSpeedMeter _meter = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _placeTimer; // fast, so it follows an auto-hiding taskbar sliding in and out
    private readonly WinEventProc _onForeground, _onTaskbarMoved; // kept alive: Windows calls them
    private IntPtr _hook, _moveHook, _taskbar;
    private bool _appBar, _placePending, _following, _askingWindows, _fullscreenPerWindows;
    private int _barTop = int.MinValue;
    private long _barMoved;
    private IntPtr _hwnd;
    private TaskbarSpeedShow _show;

    public TaskbarSpeedWindow(TaskbarSpeedShow show)
    {
        InitializeComponent();
        SetShow(show);
        DownText.Text = UpText.Text = " ";
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Clickable (it opens the Network page) but never takes focus from the app you're in.
            long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
            // Registered as an (empty, space-less) app bar only so Windows says the moment a fullscreen app
            // opens or closes, the same moment the taskbar itself steps aside.
            HwndSource.FromHwnd(_hwnd)?.AddHook(OnMessage);
            var bar = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = AppBarMessage };
            _appBar = SHAppBarMessage(ABM_NEW, ref bar) != 0;
            WatchTaskbar();
        };
        // Clicking the taskbar brings it to the front over this window; step back on top right away.
        _onForeground = (_, _, _, _, _, _, _) => PlaceSoon();
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        // Every move of the taskbar (an auto-hiding one sliding in or out), followed as it happens.
        _onTaskbarMoved = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (hwnd == _taskbar && idObject == OBJID_WINDOW && idChild == 0) PlaceSoon();
        };
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _placeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            Place();
            RefreshFullscreenState();
        }, Dispatcher);
        SizeChanged += (_, _) => Place();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _placeTimer.Stop();
            if (_following) CompositionTarget.Rendering -= OnFrame;
            _following = false;
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            if (_moveHook != IntPtr.Zero) UnhookWinEvent(_moveHook);
            _hook = _moveHook = IntPtr.Zero;
            if (_appBar)
            {
                var bar = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
                SHAppBarMessage(ABM_REMOVE, ref bar);
                _appBar = false;
            }
        };
        _meter.Sample(); // starting point; the first tick shows a real speed
    }

    /// <summary>
    /// Listens for the taskbar's moves. Hooked to Explorer's process only (cheap); hooked again if Explorer
    /// restarts and the taskbar comes back as a new window.
    /// </summary>
    private void WatchTaskbar()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == _taskbar && _moveHook != IntPtr.Zero) return;
        if (_moveHook != IntPtr.Zero) UnhookWinEvent(_moveHook);
        _moveHook = IntPtr.Zero;
        _taskbar = taskbar;
        if (taskbar == IntPtr.Zero) return;
        GetWindowThreadProcessId(taskbar, out uint explorer);
        _moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
            _onTaskbarMoved, explorer, 0, WINEVENT_OUTOFCONTEXT);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == AppBarMessage && wParam.ToInt32() == ABN_FULLSCREENAPP)
        {
            Place();
            RefreshFullscreenState();
            // The fullscreen window may still be settling into place; look once more shortly.
            Dispatcher.BeginInvoke(Place, DispatcherPriority.Input);
        }
        return IntPtr.Zero;
    }

    /// <summary>The speed was clicked.</summary>
    public event Action? Clicked;

    private void Lines_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => Clicked?.Invoke();

    public void SetShow(TaskbarSpeedShow show)
    {
        _show = show;
        DownText.Visibility = show == TaskbarSpeedShow.Upload ? Visibility.Collapsed : Visibility.Visible;
        UpText.Visibility = show == TaskbarSpeedShow.Download ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Tick()
    {
        var (down, up) = _meter.Sample();
        DownText.Text = "↓ " + Speed(down);
        UpText.Text = "↑ " + Speed(up);
        var brush = TaskbarIsLight() ? DarkText : LightText;
        DownText.Foreground = UpText.Foreground = brush;
    }

    /// <summary>"0 KB/s", "850 KB/s", "12.4 MB/s": at least KB, so the width barely changes.</summary>
    private static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1 << 30 => $"{bytesPerSecond / (1 << 30):0.0} GB/s",
        >= 1 << 20 => $"{bytesPerSecond / (1 << 20):0.0} MB/s",
        _ => $"{bytesPerSecond / 1024:0} KB/s",
    };

    /// <summary>
    /// <see cref="Place"/> right after the Windows event that asked for it, outside the event callback, with
    /// several events in a row handled once.
    /// </summary>
    private void PlaceSoon()
    {
        if (_placePending) return;
        _placePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _placePending = false;
            Place();
        }, DispatcherPriority.Send);
    }

    /// <summary>Puts the window on the taskbar left of the tray, on top; hides it where it can't sit well.</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != _taskbar) WatchTaskbar(); // Explorer restarted
        IntPtr tray = taskbar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        bool ok = tray != IntPtr.Zero && GetWindowRect(taskbar, out var bar) && GetWindowRect(tray, out var notify)
            && bar.Right - bar.Left > bar.Bottom - bar.Top // horizontal taskbar only
            && GetMonitorInfo(MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST), ref info);
        if (!ok)
        {
            SetShown(false);
            return;
        }

        // Rides along with an auto-hiding taskbar as it slides in and out, step by step, cut off by the screen
        // edge just like the tray icons, so it never pops in late or vanishes early. While the taskbar is tucked
        // away the speed is simply off the screen with it rather than hidden, so there's nothing to show again.
        GetWindowRect(taskbar, out bar);
        GetWindowRect(tray, out notify);
        if (bar.Top != _barTop)
        {
            _barTop = bar.Top;
            _barMoved = Environment.TickCount64;
            FollowTaskbar();
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int x = notify.Left - width - (int)(4 * dpi.DpiScaleX);
        int y = bar.Top + (bar.Bottom - bar.Top - height) / 2;
        var screen = info.rcMonitor;
        bool fits = x >= screen.Left && x + width <= screen.Right && !SpillsOntoOtherScreen(x, y, width, height, screen);

        // Moved first (a hidden window moves too), so it never flashes up at its old spot.
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        // Over a fullscreen app there's nothing to sit on. Even when the Windows key brings the taskbar up over
        // it, the taskbar is then drawn above every topmost window and the speed would show blurred through it.
        SetShown(fits && !_fullscreenPerWindows && !FullscreenWindowInFront(screen));
    }

    /// <summary>
    /// Asks Windows whether a fullscreen game or presentation runs, off the UI thread: Windows answers via
    /// Explorer, which is busy while the taskbar slides and can take ~200 ms, and waiting for it here froze
    /// the speed mid-slide. The answer is kept for <see cref="Place"/>.
    /// </summary>
    private void RefreshFullscreenState()
    {
        if (_askingWindows) return;
        _askingWindows = true;
        Task.Run(FullscreenAppRunning).ContinueWith(t =>
        {
            _askingWindows = false;
            bool fullscreen = t.IsCompletedSuccessfully && t.Result;
            if (fullscreen == _fullscreenPerWindows) return;
            _fullscreenPerWindows = fullscreen;
            PlaceSoon();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Whether the part sliding past the screen edge would show up on a neighbouring screen (one placed right
    /// below or above). There the speed waits until it fits whole instead of peeking onto the other screen.
    /// </summary>
    private static bool SpillsOntoOtherScreen(int x, int y, int width, int height, RECT screen)
    {
        int midX = x + width / 2;
        if (y + height > screen.Bottom && MonitorFromPoint(new POINT { X = midX, Y = y + height - 1 }, MONITOR_DEFAULTTONULL) != IntPtr.Zero)
            return true;
        return y < screen.Top && MonitorFromPoint(new POINT { X = midX, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    /// <summary>
    /// While the taskbar is moving, re-place every frame. Windows reports each step of the taskbar sliding
    /// down, but sliding up only its first and last, so following its reports alone left the speed behind.
    /// Stops once the taskbar has been still for a moment.
    /// </summary>
    private void FollowTaskbar()
    {
        if (_following) return;
        _following = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        Place();
        if (Environment.TickCount64 - _barMoved < 400) return;
        CompositionTarget.Rendering -= OnFrame;
        _following = false;
    }

    private void SetShown(bool shown)
    {
        if (shown && !IsVisible) Show();
        else if (!shown && IsVisible) Hide();
    }

    /// <summary>
    /// Whether the app in front on this screen fills it (a game, a fullscreen video or browser). Checked
    /// directly rather than waiting for Windows to notice, which takes up to a second or two.
    /// </summary>
    private static bool FullscreenWindowInFront(RECT screen)
    {
        int self = Environment.ProcessId, steps = 0;
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && steps < 2000; h = GetWindow(h, GW_HWNDNEXT), steps++)
        {
            if (!IsWindowVisible(h) || IsIconic(h) || Cloaked(h) || !GetWindowRect(h, out var r)) continue;
            if (r.Right <= screen.Left || r.Left >= screen.Right || r.Bottom <= screen.Top || r.Top >= screen.Bottom) continue;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) continue;
            long ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((ex & (NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW)) != 0) continue; // overlays, flyouts
            if (ShellClasses.Contains(ClassOf(h))) continue; // taskbar, Start, search, desktop

            bool covers = r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
            // Small always-on-top windows (picture-in-picture, widgets) aren't "the app in front".
            if ((ex & WS_EX_TOPMOST) != 0 && !covers) continue;
            // A maximized window spills past the screen by its borders; a fullscreen one fits it exactly.
            bool exact = r.Left == screen.Left && r.Top == screen.Top && r.Right == screen.Right && r.Bottom == screen.Bottom;
            return covers && (!IsZoomed(h) || exact);
        }
        return false;
    }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "Progman", "WorkerW", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
    };

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(128);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static bool Cloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A fullscreen game or a presentation, as Windows reports it. (Windows' "busy" state is left out: it
    /// lags a second or more behind, and <see cref="FullscreenWindowInFront"/> sees those apps right away.)
    /// </summary>
    private static bool FullscreenAppRunning() =>
        SHQueryUserNotificationState(out int state) == 0 && state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---------------- native ----------------

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private const uint GW_HWNDNEXT = 2;
    private const long WS_EX_TOPMOST = 0x8;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    // App bar, only for its "a fullscreen app opened/closed" notice.
    private static readonly int AppBarMessage = (int)RegisterWindowMessage("PrivacyToggle.TaskbarSpeed.AppBar");
    private const uint ABM_NEW = 0, ABM_REMOVE = 1;
    private const int ABN_FULLSCREENAPP = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
