using NAudio.CoreAudioApi;

namespace PrivacyToggle.Services;

/// <summary>
/// Live microphone levels per app, from Windows Core Audio: each app's capture session reports its peak level
/// (IAudioMeterInformation). The session list is re-read every couple of seconds; levels are read on every
/// <see cref="Sample"/> (the Activity page asks about ten times a second, only while it's showing).
/// </summary>
public sealed class MicLevelMeter : IDisposable
{
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(2);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<(string AppKey, AudioSessionControl Session)> _sessions = new();
    private readonly List<MMDevice> _devices = new();
    private DateTime _scanned = DateTime.MinValue;

    /// <summary>Peak level 0..1 per app key ("np:C:\…\app.exe") that is recording right now.</summary>
    public Dictionary<string, double> Sample()
    {
        if (DateTime.UtcNow - _scanned > Rescan) Scan();
        var levels = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, session) in _sessions)
        {
            try
            {
                double peak = session.AudioMeterInformation.MasterPeakValue;
                levels[key] = Math.Max(levels.GetValueOrDefault(key), peak);
            }
            catch
            {
                // The session went away; the next scan drops it.
            }
        }
        return levels;
    }

    private void Scan()
    {
        _scanned = DateTime.UtcNow;
        Clear();
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                _devices.Add(device);
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive || pid == 0
                        || UsageTracker.ExePath(pid) is not { } path)
                    {
                        session.Dispose();
                        continue;
                    }
                    _sessions.Add(("np:" + path, session));
                }
            }
        }
        catch
        {
            // No audio service, or a device vanished mid-scan: no levels this time.
        }
    }

    private void Clear()
    {
        foreach (var (_, session) in _sessions) session.Dispose();
        _sessions.Clear();
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();
    }

    public void Dispose()
    {
        Clear();
        _enumerator.Dispose();
    }
}
