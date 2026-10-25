using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;

namespace PrivacyToggle.Views;

/// <summary>
/// A click-through badge ("MIC OFF", "CAM ON", ...) that pops up in a screen corner when a device is switched,
/// then fades away. The alternative to a Windows notification.
/// </summary>
public partial class NotifyBadgeWindow : Window
{
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(2);
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(180));

    private static readonly Brush OffBrush = Frozen("#FF6B6B");
    private static readonly Brush OnBrush = Frozen("#7BE495");
    private static readonly Brush NoneBrush = Frozen("#FFD93D");

    private const double Gap = 12;
    private readonly DispatcherTimer _hideTimer;
    private OverlayCorner _corner = OverlayCorner.TopRight;
    private double _offset;

    public NotifyBadgeWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
        SizeChanged += (_, _) => Reposition();
        _hideTimer = new DispatcherTimer { Interval = ShowFor };
        _hideTimer.Tick += (_, _) => FadeOut();
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// Shows the badge (or refreshes it if already up) and restarts the hide countdown.
    /// <paramref name="offset"/> pushes it away from the corner, e.g. below a MIC OFF badge already there.
    /// </summary>
    /// <param name="off">True: device is off/muted. False: on/live. Null: no device found.</param>
    public void Flash(bool camera, bool? off, string text, OverlayCorner corner, double offset)
    {
        MicIcon.Visibility = camera ? Visibility.Collapsed : Visibility.Visible;
        CamIcon.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;
        Slash.Visibility = off == false ? Visibility.Collapsed : Visibility.Visible;
        var brush = off switch { true => OffBrush, false => OnBrush, null => NoneBrush };
        Badge.Background = brush;
        SlashGap.Stroke = brush;
        Label.Text = text;

        _corner = corner;
        _offset = offset;
        Reposition();
        if (!IsVisible) Show();
        // Re-assert z-order in case a fullscreen app took over.
        Topmost = false;
        Topmost = true;

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, Fade));
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void FadeOut()
    {
        _hideTimer.Stop();
        var fade = new DoubleAnimation(0, Fade);
        fade.Completed += (_, _) => { if (!_hideTimer.IsEnabled) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 110;
        double h = ActualHeight > 0 ? ActualHeight : 36;
        bool right = _corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool bottom = _corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
        Left = right ? area.Right - w - Gap : area.Left + Gap;
        Top = bottom ? area.Bottom - h - Gap - _offset : area.Top + Gap + _offset;
    }

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
