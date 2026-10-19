using Microsoft.Win32;

namespace PrivacyToggle.Services;

/// <summary>Manages the per-user Run key entry (no admin rights needed).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrivacyToggle";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry locked down by policy: nothing useful to do.
        }
    }

    /// <summary>Rewrites the entry if the EXE was moved since it was written.</summary>
    public static void RefreshPath()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key.GetValue(ValueName) as string != Command)
                key.SetValue(ValueName, Command);
        }
        catch { }
    }
}
