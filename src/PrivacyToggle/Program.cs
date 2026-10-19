using System.ServiceProcess;
using PrivacyToggle.Helper;

namespace PrivacyToggle;

/// <summary>
/// Entry point. The same EXE is either the tray app (default) or, with <c>--service</c>, the camera
/// helper service that runs as LocalSystem and enables/disables camera devices for the app.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (Has("--service"))
        {
            ServiceBase.Run(new CameraHelperService());
            return 0;
        }
        // Run elevated by the installer.
        if (Has("--install-service")) return ServiceSetup.Install();
        if (Has("--uninstall-service")) return ServiceSetup.Uninstall();
        // Diagnostics: prints the cameras the helper would control (works without admin).
        if (Has("--list-cameras"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(CameraDevices.List(), CameraProtocol.Json));
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
