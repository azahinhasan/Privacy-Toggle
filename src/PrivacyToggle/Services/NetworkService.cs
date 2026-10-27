using System.Windows.Threading;
using PrivacyToggle.Helper;
using PrivacyToggle.Models;

namespace PrivacyToggle.Services;

/// <summary>Unavailable: no helper, or one too old for network control (see <see cref="NetworkService.HelperOutdated"/>).</summary>
public enum NetworkState { Unavailable, NoDevice, On, Off }

/// <summary>
/// Network adapters on/off through the helper service. Works like <see cref="CameraService"/>: tracks what
/// the app wants (<see cref="DesiredOff"/>), follows or reverts outside changes, and turns off newly
/// plugged-in adapters while off. Pipe calls run in the background; events fire on the UI thread.
/// </summary>
public sealed class NetworkService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<NetworkAdapter> _adapters = new();
    private HashSet<string> _knownIds = new(StringComparer.OrdinalIgnoreCase);
    private DeviceMode _mode = DeviceMode.All;
    private HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _firstListDone;
    private bool _disposed;

    /// <summary>What the app wants: true = all target adapters off.</summary>
    public bool DesiredOff { get; private set; }

    /// <summary>Turn an adapter off again if something else turns it back on.</summary>
    public bool EnforceOff { get; set; }

    public NetworkState State { get; private set; } = NetworkState.Unavailable;

    /// <summary>The helper is running but predates network control; reinstalling PrivacyToggle updates it.</summary>
    public bool HelperOutdated { get; private set; }

    /// <summary>A change is being applied (it can take a few seconds).</summary>
    public bool Busy { get; private set; }

    /// <summary>Last error from the helper, if any.</summary>
    public string? LastError { get; private set; }

    public bool NeedsRestart { get; private set; }

    public IReadOnlyList<NetworkAdapter> Adapters => _adapters;

    /// <summary>The adapters PrivacyToggle controls: all of them, or the selected ones.</summary>
    public IReadOnlyList<NetworkAdapter> Targets =>
        _mode == DeviceMode.All ? _adapters : _adapters.Where(a => _selected.Contains(a.Id)).ToList();

    public event Action? StateChanged;
    public event Action? AdaptersChanged;

    public NetworkService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), dispatcher);
        _pollTimer.Stop();
    }

    /// <summary>Loads the adapter list, then applies the start state (null = follow the adapters).</summary>
    public async Task StartAsync(bool? startOff)
    {
        await RefreshAsync();
        if (startOff is bool off && State is NetworkState.On or NetworkState.Off && off != (State == NetworkState.Off))
            await SetOffAsync(off);
        else if (startOff is bool o)
            DesiredOff = o;
        _pollTimer.Start();
    }

    /// <summary>Chooses which adapters to control; <paramref name="apply"/> turns newly targeted ones off if off.</summary>
    public void SetTargets(DeviceMode mode, IEnumerable<string> selectedIds, bool apply = true)
    {
        _mode = mode;
        _selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        if (apply) _ = DesiredOff ? SetOffAsync(true) : RefreshAsync();
    }

    public Task SetOffAsync(bool off) => ChangeAsync(off);

    private async Task ChangeAsync(bool off)
    {
        if (_disposed) return;
        DesiredOff = off;
        Busy = true;
        StateChanged?.Invoke();

        await _gate.WaitAsync();
        try
        {
            var change = Targets.Where(a => a.Enabled == off).Select(a => a.Id).ToList();
            if (change.Count > 0)
            {
                var response = await CameraService.SendAsync(new CameraRequest { Command = "net-set", Enable = !off, Ids = change });
                Apply(response, fromSet: true);
            }
        }
        finally
        {
            Busy = false;
            _gate.Release();
            StateChanged?.Invoke(); // listeners showing "disconnecting…" need to hear that it finished
        }
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return; // a change is in progress; next poll catches up
        try
        {
            var response = await CameraService.SendAsync(new CameraRequest { Command = "net-list" });
            Apply(response, fromSet: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Apply(CameraResponse? response, bool fromSet)
    {
        if (_disposed) return;
        var oldState = State;
        bool oldDesired = DesiredOff;
        bool oldOutdated = HelperOutdated;

        // No helper, or one that doesn't know the network commands (it answered with a camera list).
        HelperOutdated = response != null && response.NetworkSupported != true;
        if (response == null || HelperOutdated)
        {
            State = NetworkState.Unavailable;
            LastError = null;
            if (oldState != State || oldOutdated != HelperOutdated) StateChanged?.Invoke();
            return;
        }

        if (fromSet)
        {
            LastError = response.Ok ? null : response.Error;
            NeedsRestart = response.NeedsRestart;
        }

        bool listChanged = !response.Adapters.SequenceEqual(_adapters);
        var newIds = response.Adapters.Select(a => a.Id).Where(id => !_knownIds.Contains(id)).ToList();
        _adapters = response.Adapters;
        _knownIds = new HashSet<string>(_adapters.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);

        var targets = Targets.ToList();

        // The switch failed: accept how the adapters really are, otherwise "keep off" would retry on every poll.
        if (fromSet && !response.Ok && targets.Count > 0)
            DesiredOff = targets.All(a => !a.Enabled);

        if (targets.Count == 0)
        {
            State = NetworkState.NoDevice;
        }
        else
        {
            var enabled = targets.Where(a => a.Enabled).ToList();
            if (!fromSet && !Busy && DesiredOff && enabled.Count > 0)
            {
                // An adapter came back on: newly plugged in, or turned on somewhere else.
                bool onlyNew = _firstListDone && enabled.All(a => newIds.Contains(a.Id));
                if (EnforceOff || onlyNew)
                    _dispatcher.BeginInvoke(async () => await SetOffAsync(true));
                else
                    DesiredOff = false; // follow the outside change
            }
            else if (!fromSet && !Busy && !DesiredOff && enabled.Count == 0)
            {
                DesiredOff = true; // turned off elsewhere (e.g. Network Connections): follow it
            }
            State = enabled.Count == 0 ? NetworkState.Off : NetworkState.On;
        }
        _firstListDone = true;

        if (listChanged) AdaptersChanged?.Invoke();
        if (State != oldState || DesiredOff != oldDesired || fromSet || listChanged) StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _pollTimer.Stop();
    }
}
