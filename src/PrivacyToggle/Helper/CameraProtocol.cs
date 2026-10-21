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
    public const string ServiceDisplayName = "PrivacyToggle Camera Helper";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record CameraDevice(string Id, string Name, bool Enabled);

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
    /// <summary>"list", "set", "close-and-set" (close <see cref="CloseProcessId"/>, then set), or "in-use".</summary>
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
}
