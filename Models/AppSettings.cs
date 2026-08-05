namespace GmailDesktop.Models;

public enum MemoryMode
{
    LowestMemory,
    Balanced,
    FastSwitching
}

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public enum AppScalePreference
{
    Smaller,
    Normal,
    Larger
}

public sealed class AppSettings
{
    public List<AccountProfile> Accounts { get; set; } = [];
    public string? SelectedAccountId { get; set; }
    public MemoryMode MemoryMode { get; set; } = MemoryMode.LowestMemory;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public AppScalePreference AppScale { get; set; } = AppScalePreference.Normal;
    public bool HideToTray { get; set; } = true;
    public bool EnableKeyboardShortcuts { get; set; } = true;
    public bool IsSidebarExpanded { get; set; }
}
