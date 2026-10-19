using System.Windows;
using System.Windows.Controls;
using PrivacyToggle.Services;

namespace PrivacyToggle.Tray;

public partial class TrayMenu : ContextMenu
{
    private readonly AppController _app;

    public TrayMenu(AppController app)
    {
        _app = app;
        InitializeComponent();
        Opened += (_, _) => Refresh();
    }

    public void Refresh()
    {
        ToggleItem.Header = _app.Mic.State == MicState.Muted ? "Unmute microphone" : "Mute microphone";

        var cam = _app.Camera;
        CameraItem.Header = cam.Busy ? "Camera: switching…"
            : cam.State switch
            {
                CameraState.Off => "Turn camera on",
                CameraState.On => "Turn camera off",
                CameraState.NoDevice => "No camera found",
                _ => "Camera control not installed",
            };
        CameraItem.IsEnabled = !cam.Busy && cam.State is CameraState.On or CameraState.Off;

        StartupItem.IsChecked = _app.Settings.Current.StartWithWindows;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => _app.UserToggle();
    private void Camera_Click(object sender, RoutedEventArgs e) => _app.UserToggleCamera();
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();
    private void Startup_Click(object sender, RoutedEventArgs e) => _app.SetStartWithWindows(StartupItem.IsChecked);
    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
