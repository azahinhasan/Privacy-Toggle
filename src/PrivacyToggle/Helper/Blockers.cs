using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;

namespace PrivacyToggle.Helper;

/// <summary>
/// Programs that hold a camera open and refuse to let Windows turn it off. Windows logs each refusal
/// (Kernel-PnP event 225); that log is the only source of what may be closed, so the helper can't be
/// used to close arbitrary programs.
/// </summary>
internal static class Blockers
{
    private const string VetoQuery =
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and EventID=225 and TimeCreated[timediff(@SystemTime) <= {0}]]]";

    /// <summary>The program that refused last time, if it is still running (so trying again would fail).</summary>
    public static BlockerInfo? StillBlocking(string cameraId) =>
        FindVeto(cameraId, TimeSpan.FromDays(1), requireRunning: true);

    /// <summary>The program that just refused (the log entry can land a moment after the refusal).</summary>
    public static BlockerInfo? FromLatestVeto(string cameraId)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var blocker = FindVeto(cameraId, TimeSpan.FromSeconds(30), requireRunning: false);
            if (blocker != null) return blocker;
            Thread.Sleep(250);
        }
        return null;
    }

    private static BlockerInfo? FindVeto(string cameraId, TimeSpan window, bool requireRunning)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, string.Format(VetoQuery, (long)window.TotalMilliseconds))
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
            {
                using (record)
                {
                    // Fields: ProcessId, ProcessNameLength, ProcessName, DeviceInstanceLength, DeviceInstance, …, CommandLine
                    var p = record.Properties;
                    if (p.Count < 7 || !string.Equals(p[4].Value as string, cameraId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int pid = Convert.ToInt32(p[0].Value);
                    if (requireRunning && !IsSameProcessRunning(pid, record.TimeCreated)) return null;
                    return Describe(pid, p[2].Value as string ?? "", (p[6].Value as string ?? "").Trim().Trim('"'));
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsSameProcessRunning(int pid, DateTime? vetoTime)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // A reused PID belongs to a process started after the refusal.
            return vetoTime == null || process.StartTime <= vetoTime.Value;
        }
        catch
        {
            return false;
        }
    }

    private static BlockerInfo Describe(int pid, string ntPath, string exePath)
    {
        string fileName = Path.GetFileName(ntPath);
        string? product = null;
        try { product = FileVersionInfo.GetVersionInfo(exePath).ProductName?.Trim(); } catch { }
        return new BlockerInfo
        {
            ProcessId = pid,
            Name = string.IsNullOrEmpty(product) ? fileName : $"{product} ({fileName})",
            IsService = ServicesInProcess(pid).Count > 0,
        };
    }

    /// <summary>
    /// Closes the program that is blocking <paramref name="cameraId"/>, if it is <paramref name="pid"/>.
    /// Services are stopped (and restarted when the camera turns back on); apps are ended.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? Close(string cameraId, int pid)
    {
        var blocker = StillBlocking(cameraId);
        if (blocker == null || blocker.ProcessId != pid)
            return "That program is no longer blocking the camera.";

        var services = ServicesInProcess(pid);
        try
        {
            if (services.Count > 1)
                return $"{blocker.Name} is a shared Windows process; PrivacyToggle won't close it.";

            if (services.Count == 1)
            {
                using var sc = new ServiceController(services[0]);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
                StoppedServices.Add(services[0]);
                return null;
            }

            using var process = Process.GetProcessById(pid);
            if (process.SessionId == 0)
                return $"{blocker.Name} is a system process; PrivacyToggle won't close it.";
            process.Kill();
            process.WaitForExit(5000);
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't close {blocker.Name}: {ex.Message}";
        }
    }

    /// <summary>Names of the Windows services hosted in a process (empty for normal apps).</summary>
    private static List<string> ServicesInProcess(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tasklist.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "/svc", "/fi", $"PID eq {pid}", "/fo", "csv", "/nh" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string line = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            // "image.exe","1234","Svc1,Svc2"  or  …,"N/A"
            var parts = line.Split("\",\"");
            if (parts.Length < 3) return new();
            string services = parts[2].Trim('"', '\r', '\n');
            return services == "N/A" ? new() : services.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return new();
        }
    }
}

/// <summary>Services the helper stopped to free a camera; started again when the camera turns back on.</summary>
internal static class StoppedServices
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrivacyToggle", "stopped-services.json");

    public static void Add(string name)
    {
        var set = Load();
        if (!set.Add(name)) return;
        Save(set);
    }

    /// <summary>Starts every service the helper stopped, then forgets them.</summary>
    public static void RestartAll()
    {
        var set = Load();
        if (set.Count == 0) return;
        foreach (var name in set)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            }
            catch { }
        }
        Save(new HashSet<string>());
    }

    private static HashSet<string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? new HashSet<string>(JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FilePath)) ?? new(), StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(HashSet<string> set)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(set));
        }
        catch { }
    }
}
