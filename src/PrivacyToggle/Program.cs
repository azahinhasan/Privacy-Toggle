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
        // Diagnostics: prints the network adapters the helper would control (works without admin).
        if (Has("--list-adapters"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(NetworkAdapters.List(), CameraProtocol.Json));
            return 0;
        }
        // Diagnostics: counts network use per app for 10 seconds and prints it (needs admin, like the helper).
        if (Has("--network-usage"))
        {
            NetworkTraffic.Start();
            if (NetworkTraffic.Error != null) { Console.WriteLine(NetworkTraffic.Error); return 1; }
            Thread.Sleep(10_000);
            foreach (var p in NetworkTraffic.Snapshot().OrderByDescending(p => p.Sent + p.Received).Take(15))
                Console.WriteLine($"{p.Received / 1024,10:N0} KB down {p.Sent / 1024,10:N0} KB up  {p.Pid,6}  {p.App} {p.Services}");
            NetworkTraffic.Stop();
            return 0;
        }
        // Diagnostics: whether an app is using a camera through Frame Server (needs admin, like the helper).
        if (Has("--camera-in-use"))
        {
            Console.WriteLine(CameraActivity.InUse() ? "in use" : "idle");
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
