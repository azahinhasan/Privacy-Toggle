using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace PrivacyToggle.Helper;

/// <summary>
/// Windows service (LocalSystem) that turns cameras on/off for the tray app over a local named pipe.
/// Disabling a camera needs admin rights; this keeps the tray app itself unprivileged.
/// </summary>
public sealed class CameraHelperService : ServiceBase
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private static readonly object Gate = new();

    public CameraHelperService()
    {
        ServiceName = CameraProtocol.ServiceName;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ServeAsync(_cts.Token));
        Task.Run(NetworkTraffic.Start); // counting network use per app (Network page)
    }

    protected override void OnStop()
    {
        NetworkTraffic.Stop();
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        // Local signed-in users may talk to the helper; it only ever touches camera devices.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Never reachable from other machines.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private static async Task ServeAsync(CancellationToken ct)
    {
        var security = CreateSecurity();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(CameraProtocol.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
                await pipe.WaitForConnectionAsync(ct);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(connected), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(500, CancellationToken.None);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line == null) return;
                var request = JsonSerializer.Deserialize<CameraRequest>(line, CameraProtocol.Json) ?? new CameraRequest();
                CameraResponse response;
                lock (Gate) response = Execute(request);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, CameraProtocol.Json));
            }
            catch
            {
                // Client went away or sent garbage; nothing to do.
            }
        }
    }

    internal static CameraResponse Execute(CameraRequest request)
    {
        var response = new CameraResponse { Ok = true };
        try
        {
            if (request.Command.Equals("in-use", StringComparison.OrdinalIgnoreCase))
            {
                response.InUse = CameraActivity.InUse();
                return response;
            }

            if (request.Command.StartsWith("net-", StringComparison.OrdinalIgnoreCase))
                return ExecuteNetwork(request, response);

            bool close = request.Command.Equals("close-and-set", StringComparison.OrdinalIgnoreCase);
            if (close || request.Command.Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                var ids = request.Ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var errors = new List<string>();

                // Close the blocking program first (only the one Windows logged as blocking these cameras).
                if (close && !request.Enable)
                {
                    string? closeError = "That program is no longer blocking the camera.";
                    foreach (var id in ids)
                    {
                        closeError = Blockers.Close(id, request.CloseProcessId);
                        if (closeError == null) break;
                    }
                    if (closeError != null) errors.Add(closeError);
                    else Thread.Sleep(500); // give Windows a moment to see the handle closed
                }

                if (errors.Count == 0)
                {
                    foreach (var id in ids)
                    {
                        var error = CameraDevices.SetEnabled(id, request.Enable, out bool restart, out var blocker);
                        if (error != null) errors.Add(error);
                        else DisabledLedger.Cameras.Record(id, disabled: !request.Enable);
                        response.NeedsRestart |= restart;
                        response.Blocker ??= blocker;
                    }
                    // Programs stopped to free the camera get their camera back now.
                    if (request.Enable && errors.Count == 0) StoppedServices.RestartAll();
                }

                if (errors.Count > 0)
                {
                    response.Ok = false;
                    response.Error = string.Join(" ", errors.Distinct());
                }
            }
            response.Devices = CameraDevices.List();
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }
        return response;
    }

    /// <summary>"net-list" and "net-set": network adapters, which work like cameras minus the blocker handling.</summary>
    private static CameraResponse ExecuteNetwork(CameraRequest request, CameraResponse response)
    {
        response.NetworkSupported = true;
        try
        {
            if (request.Command.Equals("net-usage", StringComparison.OrdinalIgnoreCase))
            {
                response.Traffic = NetworkTraffic.Snapshot();
                response.TrafficSession = NetworkTraffic.SessionId;
                response.TrafficError = NetworkTraffic.Error;
                return response;
            }

            if (request.Command.Equals("net-set", StringComparison.OrdinalIgnoreCase))
            {
                var errors = new List<string>();
                foreach (var id in request.Ids.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var error = NetworkAdapters.SetEnabled(id, request.Enable, out bool restart);
                    if (error != null) errors.Add(error);
                    else DisabledLedger.Adapters.Record(id, disabled: !request.Enable);
                    response.NeedsRestart |= restart;
                }
                if (errors.Count > 0)
                {
                    response.Ok = false;
                    response.Error = string.Join(" ", errors.Distinct());
                }
            }
            response.Adapters = NetworkAdapters.List();
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }
        return response;
    }
}

/// <summary>
/// Remembers which devices the helper disabled, so uninstalling can turn them back on
/// (and leave alone devices the user disabled in Device Manager).
/// </summary>
internal sealed class DisabledLedger
{
    public static readonly DisabledLedger Cameras = new("disabled-cameras.json");
    public static readonly DisabledLedger Adapters = new("disabled-adapters.json");

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrivacyToggle");

    private readonly string _fileName;

    private DisabledLedger(string fileName) => _fileName = fileName;

    private string FilePath => Path.Combine(Folder, _fileName);

    public HashSet<string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    public void Record(string id, bool disabled)
    {
        try
        {
            var set = new HashSet<string>(Load(), StringComparer.OrdinalIgnoreCase);
            if (disabled ? !set.Add(id) : !set.Remove(id)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(set));
        }
        catch { }
    }

    /// <summary>Removes PrivacyToggle's whole ProgramData folder (every ledger, and the stopped-services list).</summary>
    public static void DeleteAll()
    {
        try { Directory.Delete(Folder, recursive: true); } catch { }
    }
}
