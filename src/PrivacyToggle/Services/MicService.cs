using System.Windows.Threading;
using PrivacyToggle.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PrivacyToggle.Services;

public enum MicState { NoDevice, Live, Muted }

public sealed record MicInfo(string Id, string Name, bool Muted);

/// <summary>
/// Mutes/unmutes capture endpoints via Core Audio and keeps track of external changes.
/// All Core Audio calls happen on the UI thread; COM callbacks only post work back to it.
/// </summary>
public sealed class MicService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly EndpointWatcher _watcher;
    private readonly List<MMDevice> _devices = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _refreshTimer;
    private int _syncQueued;
    private bool _disposed;

    private DeviceMode _mode = DeviceMode.All;
    private HashSet<string> _selected = new();

    /// <summary>What the app wants: true = all target mics muted.</summary>
    public bool DesiredMuted { get; private set; }

    /// <summary>Re-mute immediately when something else unmutes a target while muted.</summary>
    public bool EnforceMute { get; set; }

    public MicState State { get; private set; } = MicState.NoDevice;

    /// <summary>Raised (on the UI thread) whenever <see cref="State"/> or <see cref="DesiredMuted"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when the list of capture devices changes.</summary>
    public event Action? DevicesChanged;

    public MicService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Sync(), dispatcher);
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };

        _watcher = new EndpointWatcher(() => _dispatcher.BeginInvoke(ScheduleRefresh));
        _enumerator.RegisterEndpointNotificationCallback(_watcher);

        Refresh();
        _pollTimer.Start();
    }

    public IReadOnlyList<MicInfo> Devices =>
        _devices.Select(d => new MicInfo(d.ID, SafeName(d), SafeMuted(d) ?? false)).ToList();

    private IEnumerable<MMDevice> Targets =>
        _mode == DeviceMode.All ? _devices : _devices.Where(d => _selected.Contains(d.ID));

    public void SetTargets(DeviceMode mode, IEnumerable<string> selectedIds)
    {
        _mode = mode;
        _selected = new HashSet<string>(selectedIds);
        if (DesiredMuted) ApplyMute(true, Targets);
        Sync(forceRaise: true);
    }

    public void Toggle() => SetMuted(State switch
    {
        MicState.Muted => false,
        MicState.Live => true, // mixed state counts as live: mute everything
        _ => !DesiredMuted,
    });

    public void SetMuted(bool muted)
    {
        DesiredMuted = muted;
        ApplyMute(muted, Targets);
        Sync(forceRaise: true);
    }

    private static void ApplyMute(bool muted, IEnumerable<MMDevice> devices)
    {
        foreach (var d in devices)
        {
            try
            {
                if (d.AudioEndpointVolume.Mute != muted)
                    d.AudioEndpointVolume.Mute = muted;
            }
            catch
            {
                // Device vanished between enumeration and the call; the watcher will refresh.
            }
        }
    }

    /// <summary>Reads the real device state and reconciles it with <see cref="DesiredMuted"/>.</summary>
    private void Sync(bool forceRaise = false)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _syncQueued, 0);

        var oldState = State;
        var oldDesired = DesiredMuted;

        var targets = Targets.ToList();
        var unmuted = new List<MMDevice>();
        int readable = 0;
        foreach (var d in targets)
        {
            var m = SafeMuted(d);
            if (m == null) { ScheduleRefresh(); continue; }
            readable++;
            if (m == false) unmuted.Add(d);
        }

        if (readable == 0)
        {
            State = MicState.NoDevice;
        }
        else
        {
            if (DesiredMuted && unmuted.Count > 0)
            {
                if (EnforceMute)
                {
                    ApplyMute(true, unmuted);
                    unmuted.Clear();
                }
                else
                {
                    DesiredMuted = false; // someone unmuted a mic elsewhere: follow it
                }
            }
            else if (!DesiredMuted && unmuted.Count == 0)
            {
                DesiredMuted = true; // everything got muted elsewhere: follow it
            }
            State = unmuted.Count == 0 ? MicState.Muted : MicState.Live;
        }

        if (forceRaise || State != oldState || DesiredMuted != oldDesired)
            StateChanged?.Invoke();
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Re-enumerates capture devices. Newly arrived mics are muted if we are in the muted state.</summary>
    private void Refresh()
    {
        if (_disposed) return;
        var oldIds = _devices.Select(d => d.ID).ToHashSet();
        ReleaseDevices();

        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    d.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                    _devices.Add(d);
                }
                catch
                {
                    d.Dispose();
                }
            }
        }
        catch
        {
            // Audio service restarting; the watcher / next change will trigger another refresh.
        }

        if (DesiredMuted)
            ApplyMute(true, Targets.Where(d => !oldIds.Contains(d.ID)));

        DevicesChanged?.Invoke();
        Sync(forceRaise: true);
    }

    // Arrives on a COM worker thread; coalesce and hand over to the UI thread.
    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (Interlocked.Exchange(ref _syncQueued, 1) == 0)
            _dispatcher.BeginInvoke(() => Sync());
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; } catch { return "Unknown microphone"; }
    }

    private static bool? SafeMuted(MMDevice d)
    {
        try { return d.AudioEndpointVolume.Mute; } catch { return null; }
    }

    private void ReleaseDevices()
    {
        foreach (var d in _devices)
        {
            try { d.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
            try { d.Dispose(); } catch { }
        }
        _devices.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _refreshTimer.Stop();
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        ReleaseDevices();
        _enumerator.Dispose();
    }

    /// <summary>IMMNotificationClient: any device add/remove/state change triggers a refresh.</summary>
    private sealed class EndpointWatcher : IMMNotificationClient
    {
        private readonly Action _changed;
        public EndpointWatcher(Action changed) => _changed = changed;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string pwstrDeviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
