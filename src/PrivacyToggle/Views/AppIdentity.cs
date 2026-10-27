using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrivacyToggle.Views;

/// <summary>
/// Friendly name and icon for a usage-store app key: "np:C:\…\app.exe", "pkg:Publisher.App_hash", and for
/// network use "sys:System" (Windows itself) or "svc:Windows Update, …" (Windows services in a svchost process).
/// </summary>
public sealed record AppIdentity(string Key, string Name, string Detail, ImageSource? Icon)
{
    private static readonly Dictionary<string, AppIdentity> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static AppIdentity For(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var identity = key.StartsWith("np:") ? Desktop(key, key[3..])
            : key.StartsWith("sys:") ? new AppIdentity(key, key[4..], "Part of Windows", null)
            : key.StartsWith("svc:") ? new AppIdentity(key, key[4..], "Windows service", null)
            : Packaged(key, key.StartsWith("pkg:") ? key[4..] : key);
        Cache[key] = identity;
        return identity;
    }

    private static AppIdentity Desktop(string key, string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            if (File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var described = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(described)) name = described.Trim();
            }
        }
        catch { }
        return new AppIdentity(key, name, path, LoadIcon(path));
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Windows Camera".</summary>
    private static AppIdentity Packaged(string key, string package)
    {
        string core = package.Split('_')[0];
        string last = core.Contains('.') ? core[(core.LastIndexOf('.') + 1)..] : core;
        string spaced = Regex.Replace(last, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return new AppIdentity(key, spaced, package + " (Microsoft Store app)", null);
    }

    private static ImageSource? LoadIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
