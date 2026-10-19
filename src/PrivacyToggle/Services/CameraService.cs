using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using PrivacyToggle.Helper;
using PrivacyToggle.Models;

namespace PrivacyToggle.Services;

public enum CameraState { Unavailable, NoDevice, On, Off }

/// <summary>
/// Camera on/off through the helper service. Mirrors <see cref="MicService"/>: tracks what the app
/// wants (<see cref="DesiredOff"/>), follows or reverts outside changes, and turns off newly
/// plugged-in cameras while off. Pipe calls run in the background; events fire on the UI thread.
/// </summary>
public sealed class CameraService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<CameraDevice> _devices = new();
    private HashSet<string> _knownIds = new(StringComparer.OrdinalIgnoreCase);
    private DeviceMode _mode = DeviceMode.All;
    private HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _firstListDone;
    private bool _disposed;

    /// <summary>What the app wants: true = all target cameras off.</summary>
    public bool DesiredOff { get; private set; }

    /// <summary>Turn a camera off again if something else turns it back on.</summary>
    public bool EnforceOff { get; set; }

    public CameraState State { get; private set; } = CameraState.Unavailable;

    /// <summary>A change is being applied (it can take a second).</summary>
    public bool Busy { get; private set; }

    /// <summary>Last error from the helper, if any.</summary>
    public string? LastError { get; private set; }

    public bool NeedsRestart { get; private set; }

    public IReadOnlyList<CameraDevice> Devices => _devices;

    public event Action? StateChanged;
    public event Action? DevicesChanged;

    public CameraService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), dispatcher);
    }

    /// <summary>Loads the device list, then applies the start state (null = follow the devices).</summary>
    public async Task StartAsync(bool? startOff)
    {
        await RefreshAsync();
        if (startOff is bool off && State is CameraState.On or CameraState.Off && off != (State == CameraState.Off))
            await SetOffAsync(off);
        else if (startOff is bool o)
            DesiredOff = o;
        _pollTimer.Start();
    }

    /// <summary>Chooses which cameras to control; <paramref name="apply"/> turns newly targeted ones off if off.</summary>
    public void SetTargets(DeviceMode mode, IEnumerable<string> selectedIds, bool apply = true)
    {
        _mode = mode;
        _selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        if (apply) _ = DesiredOff ? SetOffAsync(true) : RefreshAsync();
    }

    private IEnumerable<CameraDevice> Targets =>
        _mode == DeviceMode.All ? _devices : _devices.Where(d => _selected.Contains(d.Id));

    public Task ToggleAsync() => SetOffAsync(State switch
    {
        CameraState.Off => false,
        CameraState.On => true, // mixed counts as on: turn everything off
        _ => !DesiredOff,
    });

    public async Task SetOffAsync(bool off)
    {
        if (_disposed) return;
        DesiredOff = off;
        Busy = true;
        StateChanged?.Invoke();

        await _gate.WaitAsync();
        try
        {
            var change = Targets.Where(d => d.Enabled == off).Select(d => d.Id).ToList();
            if (change.Count > 0)
            {
                var response = await SendAsync(new CameraRequest { Command = "set", Enable = !off, Ids = change });
                Apply(response, fromSet: true);
            }
        }
        finally
        {
            Busy = false;
            _gate.Release();
        }
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return; // a change is in progress; next poll catches up
        try
        {
            var response = await SendAsync(new CameraRequest { Command = "list" });
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

        if (response == null)
        {
            State = CameraState.Unavailable;
            LastError = null;
            if (oldState != State) StateChanged?.Invoke();
            return;
        }

        if (fromSet)
        {
            LastError = response.Ok ? null : response.Error;
            NeedsRestart = response.NeedsRestart;
        }

        bool listChanged = !response.Devices.SequenceEqual(_devices);
        var newIds = response.Devices.Select(d => d.Id).Where(id => !_knownIds.Contains(id)).ToList();
        _devices = response.Devices;
        _knownIds = new HashSet<string>(_devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        var targets = Targets.ToList();

        // The switch failed (e.g. another program blocked it): accept how the cameras really are,
        // otherwise "keep cameras off" would retry on every poll.
        if (fromSet && !response.Ok && targets.Count > 0)
            DesiredOff = targets.All(d => !d.Enabled);

        if (targets.Count == 0)
        {
            State = CameraState.NoDevice;
        }
        else
        {
            var enabled = targets.Where(d => d.Enabled).ToList();
            if (!fromSet && !Busy && DesiredOff && enabled.Count > 0)
            {
                // A camera came back on: newly plugged in, or turned on somewhere else.
                bool onlyNew = _firstListDone && enabled.All(d => newIds.Contains(d.Id));
                if (EnforceOff || onlyNew)
                    _dispatcher.BeginInvoke(async () => await SetOffAsync(true));
                else
                    DesiredOff = false; // follow the outside change
            }
            else if (!fromSet && !Busy && !DesiredOff && enabled.Count == 0)
            {
                DesiredOff = true; // turned off elsewhere (e.g. Device Manager): follow it
            }
            State = enabled.Count == 0 ? CameraState.Off : CameraState.On;
        }
        _firstListDone = true;

        if (listChanged) DevicesChanged?.Invoke();
        if (State != oldState || DesiredOff != oldDesired || fromSet) StateChanged?.Invoke();
    }

    /// <summary>One request/response over the helper pipe. Returns null if the helper isn't running.</summary>
    private static Task<CameraResponse?> SendAsync(CameraRequest request) => Task.Run(() =>
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", CameraProtocol.PipeName, PipeDirection.InOut);
            pipe.Connect(800);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(request, CameraProtocol.Json));
            string? line = reader.ReadLine();
            return line == null ? null : JsonSerializer.Deserialize<CameraResponse>(line, CameraProtocol.Json);
        }
        catch
        {
            return null;
        }
    });

    public void Dispose()
    {
        _disposed = true;
        _pollTimer.Stop();
    }
}
