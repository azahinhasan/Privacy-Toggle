using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivacyToggle.Models;

namespace PrivacyToggle.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in %AppData%\PrivacyToggle\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FolderPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrivacyToggle");

    public string FilePath => Path.Combine(FolderPath, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Current = new AppSettings();
                return;
            }
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            Current.Hotkey ??= HotkeySetting.Default;
            Current.Hotkey.Modifiers ??= new();
            Current.SelectedDeviceIds ??= new();
            Current.CameraHotkey ??= HotkeySetting.CameraDefault;
            Current.CameraHotkey.Modifiers ??= new();
            Current.SelectedCameraIds ??= new();
            MigrateSharedAlerts(Current);
        }
        catch
        {
            // A broken file shouldn't stop the app; fall back to defaults.
            Current = new AppSettings();
        }
    }

    /// <summary>Before 1.2, notification and badge settings were shared; give both devices the old choice.</summary>
    private static void MigrateSharedAlerts(AppSettings s)
    {
        if (s.ShowNotification is bool notify) s.MicNotify = s.CamNotify = notify;
        if (s.ShowOverlay is bool overlay) s.MicOverlay = s.CamOverlay = overlay;
        if (s.LegacyOverlayCorner is OverlayCorner corner) s.MicOverlayCorner = s.CamOverlayCorner = corner;
        s.ShowNotification = null;
        s.ShowOverlay = null;
        s.LegacyOverlayCorner = null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Saving is best effort (e.g. file locked by an editor).
        }
    }
}
