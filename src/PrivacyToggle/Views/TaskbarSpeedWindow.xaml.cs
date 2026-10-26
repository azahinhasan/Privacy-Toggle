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
/// does); clicking it opens the Network page. It steps aside while a fullscreen app runs, and with an
/// auto-hiding taskbar it shows only while the taskbar is up.
/// </summary>
public partial class TaskbarSpeedWindow : Window
{
    private static readonly Brush LightText = Frozen(Colors.White), DarkText = Frozen(Color.FromRgb(0x11, 0x11, 0x11));

    private readonly NetSpeedMeter _meter = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _placeTimer; // fast, so it follows an auto-hiding taskbar sliding in and out
    private readonly WinEventProc _onForeground; // kept alive: Windows calls it
    private IntPtr _hook;
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
        };
        // Clicking the taskbar brings it to the front over this window; step back on top right away.
        _onForeground = (_, _, _, _, _, _, _) => Place();
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _placeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Place(), Dispatcher);
        SizeChanged += (_, _) => Place();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _placeTimer.Stop();
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        };
        _meter.Sample(); // starting point; the first tick shows a real speed
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

    /// <summary>Puts the window on the taskbar left of the tray, on top; hides it where it can't sit well.</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        IntPtr tray = taskbar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        bool ok = tray != IntPtr.Zero && GetWindowRect(taskbar, out var bar) && GetWindowRect(tray, out var notify)
            && bar.Right - bar.Left > bar.Bottom - bar.Top // horizontal taskbar only
            && TaskbarOnScreen(taskbar, bar) && !FullscreenAppRunning();
        if (!ok)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();

        GetWindowRect(taskbar, out bar);
        GetWindowRect(tray, out notify);
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int x = notify.Left - width - (int)(4 * dpi.DpiScaleX);
        int y = bar.Top + (bar.Bottom - bar.Top - height) / 2;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

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
    /// Whether the taskbar is (almost) fully on its screen. An auto-hiding taskbar slides off the edge, leaving
    /// a sliver; while it's away there's nothing to sit on, so the speed hides with it.
    /// </summary>
    private static bool TaskbarOnScreen(IntPtr taskbar, RECT bar)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST), ref info)) return true;
        int visible = Math.Min(bar.Bottom, info.rcMonitor.Bottom) - Math.Max(bar.Top, info.rcMonitor.Top);
        return visible >= (bar.Bottom - bar.Top) * 0.9;
    }

    /// <summary>A fullscreen app, game or presentation is in front: don't draw over it.</summary>
    private static bool FullscreenAppRunning() =>
        SHQueryUserNotificationState(out int state) == 0 && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---------------- native ----------------

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

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
