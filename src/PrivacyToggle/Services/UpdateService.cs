using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PrivacyToggle.Services;

/// <summary>The latest GitHub release, and the setup EXE attached to it (if any).</summary>
public sealed record UpdateInfo(
    Version Latest,
    string Tag,
    string PageUrl,
    string? InstallerName,
    string? InstallerUrl,
    long InstallerSize,
    string? Sha256)
{
    public bool IsNewer => Latest > UpdateService.Current;
}

/// <summary>
/// Checks GitHub for a newer release, downloads its setup EXE (verified against the SHA-256 GitHub
/// publishes for the file) and starts it. The installer closes PrivacyToggle, updates it, and
/// starts it again (the <c>/relaunch=1</c> switch).
/// </summary>
public static class UpdateService
{
    private const string Repo = "azahinhasan/Privacy-Toggle";
    private const string LatestApi = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";

    // Order matters: static initializers run top to bottom, and the HTTP client's User-Agent needs Current.
    /// <summary>Running version as major.minor.build.</summary>
    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The most recent check, kept for this session so pages can show it without asking again.</summary>
    public static UpdateInfo? LastResult { get; private set; }
    public static DateTime LastChecked { get; private set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PrivacyToggle/{Current}");
        return client;
    }

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            throw new InvalidOperationException($"Unexpected release tag \"{tag}\".");

        // The installer is the release's "...Setup....exe" asset.
        string? name = null, url = null, sha = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string assetName = asset.GetProperty("name").GetString() ?? "";
            if (!assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !assetName.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
            name = assetName;
            url = asset.GetProperty("browser_download_url").GetString();
            size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = digest[7..];
            break;
        }

        var info = new UpdateInfo(Normalize(latest), tag, root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases",
            name, url, size, sha);
        LastResult = info;
        LastChecked = DateTime.Now;
        return info;
    }

    /// <summary>Downloads the installer to a temp folder and verifies it. Returns the file path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (info.InstallerUrl == null || info.InstallerName == null)
            throw new InvalidOperationException("This release has no installer attached.");
        // Only ever download from this project's own GitHub releases.
        if (!info.InstallerUrl.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installer link doesn't point to PrivacyToggle's GitHub releases.");

        string folder = Path.Combine(Path.GetTempPath(), "PrivacyToggle-Update");
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Path.GetFileName(info.InstallerName));

        using var response = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? info.InstallerSize;

        using var sha = SHA256.Create();
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (info.Sha256 != null && !actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException("The download didn't match GitHub's checksum, so it was deleted. Please try again.");
        }
        return path;
    }

    /// <summary>
    /// Starts the installer (Windows asks for admin). Returns false if the user declined the prompt.
    /// The installer closes PrivacyToggle, updates it, and starts it again.
    /// </summary>
    public static bool StartInstaller(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return false;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
