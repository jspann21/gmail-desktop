using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GmailDesktop.Models;

namespace GmailDesktop.Dialogs;

public partial class AccountDialog : Window
{
    private static readonly string[] Colors =
        ["#2563EB", "#7C3AED", "#059669", "#DB2777", "#EA580C", "#0891B2"];

    private string _selectedColor = Colors[0];
    private string _gmailAvatarUrl = string.Empty;
    private BitmapImage? _gmailAvatarImage;

    public string AccountName => NameTextBox.Text.Trim();
    public string Email => EmailTextBox.Text.Trim();
    public string SelectedColor => _selectedColor;
    public bool UseGmailAvatar => UseGmailAvatarCheckBox.IsChecked == true;

    public AccountDialog(AccountProfile? account = null)
    {
        InitializeComponent();
        ColorList.ItemsSource = Colors;

        if (account is null)
        {
            NameTextBox.Text = "Gmail";
        }
        else
        {
            Title = "Edit Gmail account";
            TitleModeText.Text = "  /  Edit account";
            HeadingText.Text = "Edit account profile";
            DescriptionText.Text = "Update how this Gmail account appears in the app.";
            FooterHintText.Text = "Changes affect only how this account appears in Gmail Desktop.";
            SaveButton.Content = "Save changes";
            SaveButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Save account changes");
            NameTextBox.Text = account.DisplayName;
            EmailTextBox.Text = account.Email;
            _selectedColor = account.Color;
            UseGmailAvatarCheckBox.IsChecked = account.UseGmailAvatar;
            _gmailAvatarUrl = account.GmailAvatarUrl;
        }

        UpdatePreview();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DialogPlacement.FitToOwnerWorkArea(this);
        foreach (var item in FindVisualChildren<RadioButton>(ColorList))
            item.IsChecked = string.Equals(item.Tag?.ToString(), _selectedColor, StringComparison.OrdinalIgnoreCase);
        NameTextBox.Focus();
        NameTextBox.SelectAll();
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

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NameValidationText is not null && !string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            NameValidationText.Visibility = Visibility.Collapsed;
            NameTextBox.BorderBrush = (Brush)FindResource("BorderBrush");
        }
        UpdatePreview();
    }

    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string color })
        {
            _selectedColor = color;
            UpdatePreview();
        }
    }

    private void UseGmailAvatar_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewAvatar is null || PreviewInitialsText is null) return;
        PreviewAvatar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedColor));
        if (UseGmailAvatarCheckBox?.IsChecked == true &&
            Uri.TryCreate(_gmailAvatarUrl, UriKind.Absolute, out var avatarUri))
        {
            PreviewAvatarImageBrush.ImageSource = _gmailAvatarImage ??= new BitmapImage(avatarUri);
        }
        else
        {
            PreviewAvatarImageBrush.ImageSource = null;
        }

        PreviewInitialsText.Text = AccountProfile.CreateInitials(NameTextBox?.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            NameValidationText.Visibility = Visibility.Visible;
            NameTextBox.BorderBrush = (Brush)FindResource("DangerBrush");
            NameTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
