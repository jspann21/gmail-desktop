using System.Windows;
using System.Windows.Media;
using GmailDesktop.Models;
using Microsoft.Win32;

namespace GmailDesktop.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void Apply(ThemePreference preference)
    {
        ApplyPalette(preference == ThemePreference.Dark ||
                     preference == ThemePreference.System && IsSystemDark());
    }

    public static bool RefreshSystemTheme(ThemePreference preference)
    {
        if (preference != ThemePreference.System) return false;

        var isDark = IsSystemDark();
        if (isDark == IsDark) return false;

        ApplyPalette(isDark);
        return true;
    }

    private static void ApplyPalette(bool isDark)
    {
        IsDark = isDark;

        Set("WindowBackgroundBrush", IsDark ? "#FF15191E" : "#FFF7F9FC");
        Set("SidebarBackgroundBrush", IsDark ? "#FF0B1118" : "#FF101821");
        Set("SidebarHoverBrush", IsDark ? "#FF1D2A36" : "#FF1B2A38");
        Set("CardBackgroundBrush", IsDark ? "#FF20252B" : "#FFFFFFFF");
        Set("TextPrimaryBrush", IsDark ? "#FFF2F4F7" : "#FF17202A");
        Set("TextSecondaryBrush", IsDark ? "#FFA8B0BA" : "#FF667085");
        Set("BorderBrush", IsDark ? "#FF343A40" : "#FFE4E7EC");
        Set("ControlHoverBrush", IsDark ? "#FF30363D" : "#FFF0F2F5");
        Set("AccentBrush", "#FF0B57D0");
        Set("DangerBrush", IsDark ? "#FFFF8A80" : "#FFB42318");
    }

    private static void Set(string key, string color) =>
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
