using System.Windows;
using System.Windows.Interop;
using PrivacyToggle.Interop;
using Microsoft.Win32;

namespace PrivacyToggle.Services;

/// <summary>Follows the Windows app theme (or a forced light/dark choice) and swaps the brush dictionary live.</summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static ResourceDictionary? _current;
    private static Models.ThemeMode _mode = Models.ThemeMode.System;

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Initialize(Models.ThemeMode mode)
    {
        _mode = mode;
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    private static bool ReadIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Switches between following Windows and a forced light/dark theme.</summary>
    public static void SetMode(Models.ThemeMode mode)
    {
        _mode = mode;
        Apply();
    }

    private static void Apply()
    {
        bool dark = _mode switch
        {
            Models.ThemeMode.Light => false,
            Models.ThemeMode.Dark => true,
            _ => ReadIsDark(),
        };
        if (_current != null && dark == IsDark) return;
        IsDark = dark;

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;
        Changed?.Invoke();
    }

    /// <summary>Gives a window a dark or light title bar matching the theme.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
