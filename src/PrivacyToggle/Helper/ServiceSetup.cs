using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;

namespace PrivacyToggle.Helper;

/// <summary>
/// Installs/removes the camera helper service. Called by the installer (elevated) as
/// <c>PrivacyToggle.exe --install-service</c> / <c>--uninstall-service</c>.
/// Exit codes: 0 ok, 5 not elevated, 3 EXE not in Program Files, 1 other failure.
/// </summary>
internal static class ServiceSetup
{
    public static int Install()
    {
        if (!IsElevated()) return 5;

        // A LocalSystem service must run from an admin-only folder; otherwise any program
        // running as the user could swap the EXE and gain SYSTEM rights.
        string exe = Environment.ProcessPath!;
        if (!IsUnderProgramFiles(exe)) return 3;

        RemoveService();
        // Version 1.1.0 could leave a camera "disabled at next boot" when a program vetoed turning it off.
        CameraDevices.RepairPendingDisables();

        int rc = Sc("create", CameraProtocol.ServiceName,
            "binPath=", $"\"{exe}\" --service",
            "start=", "auto",
            "DisplayName=", CameraProtocol.ServiceDisplayName);
        if (rc != 0) return 1;
        Sc("description", CameraProtocol.ServiceName, "Turns cameras on and off for PrivacyToggle.");
        Sc("failure", CameraProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");

        try
        {
            using var sc = new ServiceController(CameraProtocol.ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }
        catch
        {
            return 1;
        }
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsElevated()) return 5;

        // Turn back on every camera PrivacyToggle turned off, so none stays disabled after uninstall.
        foreach (var id in DisabledLedger.Load())
            CameraDevices.SetEnabled(id, true, out _, out _);
        StoppedServices.RestartAll();
        DisabledLedger.Delete();

        RemoveService();
        return 0;
    }

    private static void RemoveService()
    {
        try
        {
            using var sc = new ServiceController(CameraProtocol.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
            // Not installed, or already stopped.
        }
        Sc("delete", CameraProtocol.ServiceName);
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsUnderProgramFiles(string path)
    {
        string full = Path.GetFullPath(path);
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Any(root => full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }
}
