using System.Text.Json;

namespace PrivacyToggle.Helper;

/// <summary>
/// Wire format between the tray app and the camera helper service: one JSON request line,
/// one JSON response line, over a local named pipe.
/// </summary>
public static class CameraProtocol
{
    public const string PipeName = "PrivacyToggle.CameraHelper";
    public const string ServiceName = "PrivacyToggleHelper";
    public const string ServiceDisplayName = "PrivacyToggle Helper";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record CameraDevice(string Id, string Name, bool Enabled);

public enum NetworkKind { WiFi, Ethernet, Mobile }

/// <summary>
/// A physical network adapter. <see cref="Alias"/> is its Network Connections name ("Wi-Fi", "Ethernet 2");
/// <see cref="Connected"/> means it's enabled and has a working connection.
/// </summary>
public sealed record NetworkAdapter(string Id, string Name, string Alias, NetworkKind Kind, bool Enabled, bool Connected);

/// <summary>
/// One process's network totals since the helper started counting it. <see cref="Serial"/> is unique per
/// process lifetime (a reused process id gets a new one). <see cref="App"/> is the exe path, or "System";
/// <see cref="Services"/> names the Windows services hosted in a svchost process.
/// </summary>
public sealed record ProcessTraffic(long Serial, int Pid, string App, string? Services, long Sent, long Received);

/// <summary>A program that held a camera open and refused to let Windows turn it off.</summary>
public sealed class BlockerInfo
{
    public string Name { get; set; } = "";
    public int ProcessId { get; set; }

    /// <summary>A background Windows service; it is stopped (and later restarted) rather than killed.</summary>
    public bool IsService { get; set; }
}

public sealed class CameraRequest
{
    /// <summary>
    /// Cameras: "list", "set", "close-and-set" (close <see cref="CloseProcessId"/>, then set), or "in-use".
    /// Network adapters: "net-list" or "net-set" (<see cref="Ids"/> are adapter ids then).
    /// Network use: "net-usage" (bytes sent/received per process).
    /// </summary>
    public string Command { get; set; } = "list";
    public bool Enable { get; set; }
    public List<string> Ids { get; set; } = new();

    /// <summary>For "close-and-set": the blocker the helper reported earlier. Nothing else can be closed.</summary>
    public int CloseProcessId { get; set; }
}

public sealed class CameraResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool NeedsRestart { get; set; }

    /// <summary>Set when turning off failed because a program holds the camera.</summary>
    public BlockerInfo? Blocker { get; set; }

    /// <summary>For "in-use": whether an app is using a camera through Windows' Frame Server right now.</summary>
    public bool? InUse { get; set; }

    public List<CameraDevice> Devices { get; set; } = new();

    /// <summary>
    /// Set by helpers that understand the network commands. A helper from before 1.6 answers "net-list"
    /// as a camera "list" and leaves this null, so the app knows the helper needs updating.
    /// </summary>
    public bool? NetworkSupported { get; set; }

    public List<NetworkAdapter> Adapters { get; set; } = new();

    /// <summary>For "net-usage": per-process network totals, the counting session they belong to, or why there are none.</summary>
    public List<ProcessTraffic>? Traffic { get; set; }
    public string? TrafficSession { get; set; }
    public string? TrafficError { get; set; }
}
