using System.Windows;
using System.Windows.Interop;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;

namespace PrivacyToggle.Views;

/// <summary>Small always-on-top, click-through "MIC OFF" / "CAM OFF" / "NET OFF" badges.</summary>
public partial class OverlayWindow : Window
{
    private const double Gap = 12;
    private OverlayCorner _corner = OverlayCorner.TopRight;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
        SizeChanged += (_, _) => Reposition();
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    public void SetBadges(bool micOff, bool camOff, bool netOff)
    {
        MicBadge.Visibility = micOff ? Visibility.Visible : Visibility.Collapsed;
        CamBadge.Visibility = camOff ? Visibility.Visible : Visibility.Collapsed;
        NetBadge.Visibility = netOff ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Place(OverlayCorner corner)
    {
        _corner = corner;
        Reposition();
        // Re-assert z-order in case a fullscreen app took over.
        Topmost = false;
        Topmost = true;
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 110;
        double h = ActualHeight > 0 ? ActualHeight : 36;
        bool right = _corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool bottom = _corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
        Left = right ? area.Right - w - Gap : area.Left + Gap;
        Top = bottom ? area.Bottom - h - Gap : area.Top + Gap;
    }
}
