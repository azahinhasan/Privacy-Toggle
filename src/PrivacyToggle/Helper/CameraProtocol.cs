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

public sealed class CameraRequest
{
    /// <summary>"list" or "set".</summary>
    public string Command { get; set; } = "list";
    public bool Enable { get; set; }
    public List<string> Ids { get; set; } = new();
}

public sealed class CameraResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool NeedsRestart { get; set; }
    public List<CameraDevice> Devices { get; set; } = new();
}
