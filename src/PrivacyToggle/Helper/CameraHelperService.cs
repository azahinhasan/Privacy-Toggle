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
    }

    protected override void OnStop()
    {
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
                        else DisabledLedger.Record(id, disabled: !request.Enable);
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
}

/// <summary>
/// Remembers which cameras the helper disabled, so uninstalling can turn them back on
/// (and leave alone cameras the user disabled in Device Manager).
/// </summary>
internal static class DisabledLedger
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrivacyToggle", "disabled-cameras.json");

    public static HashSet<string> Load()
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

    public static void Record(string id, bool disabled)
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

    public static void Delete()
    {
        try { Directory.Delete(Path.GetDirectoryName(FilePath)!, recursive: true); } catch { }
    }
}
