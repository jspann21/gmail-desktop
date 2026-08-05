using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GmailDesktop.Models;

namespace GmailDesktop.Dialogs;

public partial class SettingsDialog : Window
{
    public MemoryMode MemoryMode => LowestMemoryRadio.IsChecked == true
        ? MemoryMode.LowestMemory
        : BalancedRadio.IsChecked == true
            ? MemoryMode.Balanced
            : MemoryMode.FastSwitching;

    public ThemePreference Theme => LightThemeRadio.IsChecked == true
        ? ThemePreference.Light
        : DarkThemeRadio.IsChecked == true
            ? ThemePreference.Dark
            : ThemePreference.System;

    public AppScalePreference AppScale => SmallerScaleRadio.IsChecked == true
        ? AppScalePreference.Smaller
        : LargerScaleRadio.IsChecked == true
            ? AppScalePreference.Larger
            : AppScalePreference.Normal;

    public bool HideToTray => HideToTrayCheckBox.IsChecked == true;
    public bool EnableKeyboardShortcuts => ShortcutsCheckBox.IsChecked == true;

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();

        switch (settings.MemoryMode)
        {
            case MemoryMode.Balanced:
                BalancedRadio.IsChecked = true;
                break;
            case MemoryMode.FastSwitching:
                FastRadio.IsChecked = true;
                break;
            default:
                LowestMemoryRadio.IsChecked = true;
                break;
        }

        switch (settings.Theme)
        {
            case ThemePreference.Light:
                LightThemeRadio.IsChecked = true;
                break;
            case ThemePreference.Dark:
                DarkThemeRadio.IsChecked = true;
                break;
            default:
                SystemThemeRadio.IsChecked = true;
                break;
        }

        switch (settings.AppScale)
        {
            case AppScalePreference.Smaller:
                SmallerScaleRadio.IsChecked = true;
                break;
            case AppScalePreference.Larger:
                LargerScaleRadio.IsChecked = true;
                break;
            default:
                NormalScaleRadio.IsChecked = true;
                break;
        }

        HideToTrayCheckBox.IsChecked = settings.HideToTray;
        ShortcutsCheckBox.IsChecked = settings.EnableKeyboardShortcuts;

        var scale = settings.AppScale switch
        {
            AppScalePreference.Smaller => 0.9,
            AppScalePreference.Larger => 1.1,
            _ => 1
        };
        SettingsScaleRoot.LayoutTransform = new ScaleTransform(scale, scale);
        Width = 760 * scale;
        Height = 820 * scale;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
    }

    private void WindowSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WindowSurface.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            radiusX: 13,
            radiusY: 13);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
