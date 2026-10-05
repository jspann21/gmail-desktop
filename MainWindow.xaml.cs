using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using GmailDesktop.Dialogs;
using GmailDesktop.Models;
using GmailDesktop.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace GmailDesktop;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly WebViewSessionManager _sessionManager;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _appIcon;
    private TrayMenuWindow? _trayMenu;
    private HwndSource? _windowSource;
    private AccountProfile? _selectedAccount;
    private bool _isExiting;
    private bool _isClosed;
    private readonly HashSet<string> _removingAccountIds = [];
    private bool _hasShownTrayHint;
    private Point _accountDragStart;
    private Point _accountDragOffset;
    private AccountProfile? _draggedAccount;
    private Button? _dropIndicatorButton;
    private DragPreviewAdorner? _dragPreview;
    private AdornerLayer? _dragAdornerLayer;
    private bool _suppressAccountClick;
    private double _appScale = 1;

    private const double CollapsedSidebarWidth = 82;
    private const double ExpandedSidebarWidth = 264;

    public static readonly DependencyProperty IsSidebarExpandedProperty = DependencyProperty.Register(
        nameof(IsSidebarExpanded),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    public bool IsSidebarExpanded
    {
        get => (bool)GetValue(IsSidebarExpandedProperty);
        private set => SetValue(IsSidebarExpandedProperty, value);
    }

    public ObservableCollection<AccountProfile> Accounts { get; }

    public MainWindow()
    {
        _settings = _settingsService.Load();
        ThemeService.Apply(_settings.Theme);
        Accounts = new ObservableCollection<AccountProfile>(_settings.Accounts);

        InitializeComponent();
        DataContext = this;
        IsSidebarExpanded = _settings.IsSidebarExpanded;
        ApplySidebarState();

        _appIcon = AppIconFactory.CreateIcon();
        Icon = AppIconFactory.ToImageSource(_appIcon);

        _sessionManager = new WebViewSessionManager(BrowserHost, _settingsService);
        _sessionManager.StatusChanged += SessionManager_StatusChanged;
        _sessionManager.AccountNavigationChanged += (_, _) => ScheduleSave();
        ApplyAppScale();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Text = string.Empty;
        };

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Gmail Desktop",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.MouseUp += TrayIcon_MouseUp;

        UpdateMemoryModeLabel();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Accounts.Count == 0)
        {
            ShowWelcome();
            return;
        }

        var initial = Accounts.FirstOrDefault(account => account.Id == _settings.SelectedAccountId)
                      ?? Accounts.OrderByDescending(account => account.LastUsedUtc).First();
        await SelectAccountAsync(initial);
    }

    private async Task SelectAccountAsync(AccountProfile account)
    {
        if (_isExiting || _isClosed || !Accounts.Contains(account) || _removingAccountIds.Contains(account.Id))
            return;

        _selectedAccount = account;
        foreach (var item in Accounts) item.IsSelected = item.Id == account.Id;

        _settings.SelectedAccountId = account.Id;
        account.LastUsedUtc = DateTime.UtcNow;
        TitleAccountText.Text = string.IsNullOrWhiteSpace(account.Email)
            ? account.DisplayName
            : $"{account.DisplayName} ({account.Email})";
        ToolbarAccountText.Text = account.DisplayName;
        WelcomePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        SaveSettings();

        await _sessionManager.SwitchToAsync(account, _settings.MemoryMode);
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressAccountClick)
        {
            e.Handled = true;
            return;
        }

        if (sender is Button { DataContext: AccountProfile account })
            await SelectAccountAsync(account);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        _settings.IsSidebarExpanded = IsSidebarExpanded;
        ApplySidebarState();
        SaveSettings();
    }

    private void ApplySidebarState()
    {
        var width = IsSidebarExpanded ? ExpandedSidebarWidth : CollapsedSidebarWidth;
        TitleSidebarColumn.Width = new GridLength(width);
        SidebarColumn.Width = new GridLength(width);
        SidebarToggleChevron.RenderTransform = new RotateTransform(IsSidebarExpanded ? 0 : 180);
    }

    private void ApplyAppScale()
    {
        _appScale = _settings.AppScale switch
        {
            AppScalePreference.Smaller => 0.9,
            AppScalePreference.Larger => 1.1,
            _ => 1
        };
        AppScaleRoot.Width = double.NaN;
        AppScaleRoot.Height = double.NaN;
        AppScaleRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
        AppScaleRoot.VerticalAlignment = VerticalAlignment.Stretch;
        AppScaleRoot.RenderTransform = Transform.Identity;
        AppScaleRoot.LayoutTransform = new ScaleTransform(_appScale, _appScale);
        UpdateWindowChrome();
        UpdateAppScaleBounds();
    }

    private void ScaleViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAppScaleBounds();

    private void UpdateAppScaleBounds()
    {
        if (ScaleViewport.ActualWidth <= 0 || ScaleViewport.ActualHeight <= 0) return;

        WindowContentClip.Clip = new RectangleGeometry(
            new Rect(0, 0, ScaleViewport.ActualWidth, ScaleViewport.ActualHeight),
            WindowState == WindowState.Maximized ? 0 : 10,
            WindowState == WindowState.Maximized ? 0 : 10);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowChrome();
        UpdateAppScaleBounds();
    }

    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        var chrome = WindowChrome.GetWindowChrome(this);
        chrome.ResizeBorderThickness = new Thickness(maximized ? 0 : 8);
        chrome.CornerRadius = new CornerRadius(maximized ? 0 : 10);
        // CaptionHeight starts below the resize border. Keep native dragging,
        // restore, double-click and the system menu within the scaled title bar.
        chrome.CaptionHeight = Math.Max(0, 44 * _appScale - chrome.ResizeBorderThickness.Top);
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
        MaximizeIcon.Data = Geometry.Parse(maximized
            ? "M 3,0 L 10,0 L 10,7 M 0,3 L 7,3 L 7,10 L 0,10 Z"
            : "M 0,0 L 10,0 L 10,10 L 0,10 Z");
    }

    private void Account_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: AccountProfile account }) return;
        _accountDragStart = e.GetPosition(this);
        _accountDragOffset = e.GetPosition((Button)sender);
        _draggedAccount = account;
        _suppressAccountClick = false;
    }

    private void Account_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _draggedAccount is null ||
            sender is not Button button)
            return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _accountDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _accountDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _suppressAccountClick = true;
        BeginAccountDragPreview(button);
        GiveFeedbackEventHandler feedbackHandler = (_, args) =>
        {
            UpdateAccountDragPreview(Mouse.GetPosition(AccountItems));
            Mouse.SetCursor(Cursors.SizeAll);
            args.UseDefaultCursors = false;
            args.Handled = true;
        };

        button.GiveFeedback += feedbackHandler;
        try
        {
            DragDrop.DoDragDrop(button, _draggedAccount, DragDropEffects.Move);
        }
        finally
        {
            button.GiveFeedback -= feedbackHandler;
            EndAccountDragPreview(button);
            _draggedAccount = null;
        }
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => _suppressAccountClick = false));
    }

    private void Account_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedAccount = null;
        if (!_suppressAccountClick) return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => _suppressAccountClick = false));
    }

    private void AccountItems_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(AccountProfile)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(AccountItems);
        UpdateAccountDragPreview(position);
        ShowDropIndicator(GetInsertionIndex(position.Y));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void AccountItems_DragLeave(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(AccountItems);
        if (position.X < 0 || position.Y < 0 ||
            position.X > AccountItems.ActualWidth || position.Y > AccountItems.ActualHeight)
            ClearDropIndicator();
    }

    private void AccountItems_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(AccountProfile)) is not AccountProfile source)
            return;

        var sourceIndex = Accounts.IndexOf(source);
        if (sourceIndex < 0) return;

        var destinationIndex = GetInsertionIndex(e.GetPosition(AccountItems).Y);
        if (sourceIndex < destinationIndex) destinationIndex--;
        destinationIndex = Math.Clamp(destinationIndex, 0, Accounts.Count - 1);

        if (sourceIndex != destinationIndex)
        {
            Accounts.Move(sourceIndex, destinationIndex);
            SaveSettings();
        }

        ClearDropIndicator();
        e.Handled = true;
    }

    private int GetInsertionIndex(double pointerY)
    {
        for (var index = 0; index < Accounts.Count; index++)
        {
            if (AccountItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                continue;

            var top = container.TranslatePoint(new Point(0, 0), AccountItems).Y;
            if (pointerY < top + container.ActualHeight / 2)
                return index;
        }

        return Accounts.Count;
    }

    private void ShowDropIndicator(int insertionIndex)
    {
        ClearDropIndicator();
        if (Accounts.Count == 0) return;

        var itemIndex = insertionIndex < Accounts.Count ? insertionIndex : Accounts.Count - 1;
        if (AccountItems.ItemContainerGenerator.ContainerFromIndex(itemIndex) is not DependencyObject container)
            return;

        var button = FindVisualChild<Button>(container);
        if (button is null) return;

        var indicatorName = insertionIndex < Accounts.Count ? "DropBefore" : "DropAfter";
        if (button.Template.FindName(indicatorName, button) is FrameworkElement indicator)
            indicator.Visibility = Visibility.Visible;
        _dropIndicatorButton = button;
    }

    private void ClearDropIndicator()
    {
        if (_dropIndicatorButton is null) return;
        if (_dropIndicatorButton.Template.FindName("DropBefore", _dropIndicatorButton) is FrameworkElement before)
            before.Visibility = Visibility.Collapsed;
        if (_dropIndicatorButton.Template.FindName("DropAfter", _dropIndicatorButton) is FrameworkElement after)
            after.Visibility = Visibility.Collapsed;
        _dropIndicatorButton = null;
    }

    private void BeginAccountDragPreview(Button button)
    {
        _dragAdornerLayer = AdornerLayer.GetAdornerLayer(AccountItems);
        if (_dragAdornerLayer is null) return;

        var dpi = VisualTreeHelper.GetDpi(button);
        var width = Math.Max(1, (int)Math.Ceiling(button.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(button.ActualHeight * dpi.DpiScaleY));
        var snapshot = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        snapshot.Render(button);
        snapshot.Freeze();

        _dragPreview = new DragPreviewAdorner(AccountItems, snapshot, button.ActualWidth, button.ActualHeight);
        _dragAdornerLayer.Add(_dragPreview);
        button.Opacity = 0.28;
        UpdateAccountDragPreview(Mouse.GetPosition(AccountItems));
    }

    private void UpdateAccountDragPreview(Point pointerPosition) =>
        _dragPreview?.MoveTo(pointerPosition.X - _accountDragOffset.X, pointerPosition.Y - _accountDragOffset.Y);

    private void EndAccountDragPreview(Button button)
    {
        button.Opacity = 1;
        ClearDropIndicator();
        if (_dragPreview is not null && _dragAdornerLayer is not null)
            _dragAdornerLayer.Remove(_dragPreview);
        _dragPreview = null;
        _dragAdornerLayer = null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }

        return null;
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AccountDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var account = new AccountProfile
        {
            DisplayName = dialog.AccountName,
            Email = dialog.Email,
            Color = dialog.SelectedColor,
            UseGmailAvatar = dialog.UseGmailAvatar
        };

        Accounts.Add(account);
        await SelectAccountAsync(account);
    }

    private void RenameAccount_Click(object sender, RoutedEventArgs e)
    {
        var account = GetContextAccount(sender);
        if (account is null || _removingAccountIds.Contains(account.Id)) return;

        var dialog = new AccountDialog(account) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        account.DisplayName = dialog.AccountName;
        account.Email = dialog.Email;
        account.Color = dialog.SelectedColor;
        account.UseGmailAvatar = dialog.UseGmailAvatar;
        if (_selectedAccount?.Id == account.Id)
        {
            TitleAccountText.Text = string.IsNullOrWhiteSpace(account.Email)
                ? account.DisplayName
                : $"{account.DisplayName} ({account.Email})";
            ToolbarAccountText.Text = account.DisplayName;
        }
        SaveSettings();
        if (account.UseGmailAvatar)
            _sessionManager.RefreshGmailAvatar(account);
    }

    private async void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        var account = GetContextAccount(sender);
        if (account is null || _isExiting || _isClosed || _removingAccountIds.Contains(account.Id)) return;

        var result = MessageBox.Show(
            this,
            $"Remove {account.DisplayName}?\n\nIts local sign-in session, cookies, and browser data will be deleted from this PC. This does not delete the Google account.",
            "Remove Gmail account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        if (!_removingAccountIds.Add(account.Id)) return;
        try
        {
            await _sessionManager.ClearAndDisposeSessionAsync(account.Id);
            if (_isExiting || _isClosed) return;

            var index = Accounts.IndexOf(account);
            var wasSelected = _selectedAccount?.Id == account.Id;
            Accounts.Remove(account);
            SaveSettings();

            var profileRemoval = _settingsService.DeleteProfileAsync(account);
            if (wasSelected)
            {
                _selectedAccount = null;
                var availableAccounts = Accounts.Where(item => !_removingAccountIds.Contains(item.Id)).ToList();
                if (availableAccounts.Count == 0)
                {
                    _settings.SelectedAccountId = null;
                    SaveSettings();
                    ShowWelcome();
                }
                else
                {
                    await SelectAccountAsync(availableAccounts[Math.Clamp(index, 0, availableAccounts.Count - 1)]);
                }
            }

            var profileRemoved = await profileRemoval;
            if (!_isExiting && !_isClosed && !profileRemoved)
            {
                MessageBox.Show(
                    this,
                    $"The account was removed, but its browser data could not be deleted. Close Gmail Desktop and delete this folder manually:\n\n{_settingsService.GetProfileDirectory(account)}",
                    "Browser data could not be deleted",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (ObjectDisposedException) when (_isExiting || _isClosed)
        {
            // Closing the window can dispose the browser while removal is awaiting its lock.
        }
        finally { _removingAccountIds.Remove(account.Id); }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var oldMode = _settings.MemoryMode;
        var dialog = new SettingsDialog(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings.MemoryMode = dialog.MemoryMode;
        _settings.Theme = dialog.Theme;
        _settings.AppScale = dialog.AppScale;
        _settings.HideToTray = dialog.HideToTray;
        _settings.EnableKeyboardShortcuts = dialog.EnableKeyboardShortcuts;
        ThemeService.Apply(_settings.Theme);
        ApplyAppScale();
        _sessionManager.ApplyTheme();
        UpdateMemoryModeLabel();
        SaveSettings();

        if (oldMode != _settings.MemoryMode)
            await _sessionManager.ReconcileSessionsAsync(_settings.MemoryMode);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAccount is null) return;
        _sessionManager.DisposeSession(_selectedAccount.Id);
        await SelectAccountAsync(_selectedAccount);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _sessionManager.RefreshActive();
    private void Inbox_Click(object sender, RoutedEventArgs e) => _sessionManager.OpenInbox();
    private void Back_Click(object sender, RoutedEventArgs e) => _sessionManager.GoBack();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int wmNcHitTest = 0x0084;
        if (message != wmNcHitTest || WindowState == WindowState.Maximized || ResizeMode != ResizeMode.CanResize)
            return IntPtr.Zero;

        var packedPosition = lParam.ToInt64();
        var screenPosition = new Point(
            unchecked((short)(packedPosition & 0xffff)),
            unchecked((short)((packedPosition >> 16) & 0xffff)));
        var position = PointFromScreen(screenPosition);
        const double resizeBorder = 8;

        var left = position.X >= 0 && position.X <= resizeBorder;
        var right = position.X >= ActualWidth - resizeBorder && position.X <= ActualWidth;
        var top = position.Y >= 0 && position.Y <= resizeBorder;
        var bottom = position.Y >= ActualHeight - resizeBorder && position.Y <= ActualHeight;

        var hitTest = (left, right, top, bottom) switch
        {
            (true, _, true, _) => 13,  // HTTOPLEFT
            (_, true, true, _) => 14,  // HTTOPRIGHT
            (true, _, _, true) => 16,  // HTBOTTOMLEFT
            (_, true, _, true) => 17,  // HTBOTTOMRIGHT
            (true, _, _, _) => 10,     // HTLEFT
            (_, true, _, _) => 11,     // HTRIGHT
            (_, _, true, _) => 12,     // HTTOP
            (_, _, _, true) => 15,     // HTBOTTOM
            _ => 0
        };

        if (hitTest == 0) return IntPtr.Zero;
        handled = true;
        return new IntPtr(hitTest);
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R)
        {
            _sessionManager.RefreshActive();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            Settings_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (!_settings.EnableKeyboardShortcuts || !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var index = key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            _ => -1
        };

        if (index >= 0 && index < Accounts.Count)
        {
            e.Handled = true;
            await SelectAccountAsync(Accounts[index]);
        }
    }

    private void SessionManager_StatusChanged(object? sender, BrowserStatusEventArgs e)
    {
        // A completed-download message may have armed this timer. Do not let that
        // stale timer erase a newer loading or navigation status.
        _statusTimer.Stop();
        LoadingProgress.Visibility = e.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        if (e.IsError)
        {
            ErrorMessageText.Text = e.Message;
            ErrorPanel.Visibility = Visibility.Visible;
            StatusText.Text = string.Empty;
            return;
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = e.Message;
        if (!e.IsBusy && !string.IsNullOrWhiteSpace(e.Message))
        {
            _statusTimer.Start();
        }
    }

    private void ShowWelcome()
    {
        WelcomePanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingProgress.Visibility = Visibility.Collapsed;
        TitleAccountText.Text = "No account selected";
        ToolbarAccountText.Text = "Gmail";
        StatusText.Text = string.Empty;
    }

    private void UpdateMemoryModeLabel()
    {
        MemoryModeText.Text = _settings.MemoryMode switch
        {
            MemoryMode.LowestMemory => "LOWEST MEMORY",
            MemoryMode.Balanced => "BALANCED",
            _ => "FAST SWITCHING"
        };
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings()
    {
        _saveTimer.Stop();
        _settings.Accounts = Accounts.ToList();
        _settingsService.Save(_settings);
    }

    private static AccountProfile? GetContextAccount(object sender)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu) return null;
        return (menu.PlacementTarget as FrameworkElement)?.DataContext as AccountProfile;
    }

    private void TrayIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right) return;
        Dispatcher.Invoke(ShowTrayMenu);
    }

    private void ShowTrayMenu()
    {
        _trayMenu?.Close();
        var trayMenu = new TrayMenuWindow(
            RestoreFromTray,
            () =>
            {
                _isExiting = true;
                Close();
            });
        _trayMenu = trayMenu;
        trayMenu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayMenu, trayMenu)) _trayMenu = null;
        };
        trayMenu.Show();
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting && _settings.HideToTray && !Application.Current.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
            if (!_hasShownTrayHint)
            {
                _trayIcon.ShowBalloonTip(2500, "Gmail Desktop is still running",
                    "Open it from the notification area, or choose Exit there to quit.",
                    Forms.ToolTipIcon.Info);
                _hasShownTrayHint = true;
            }
            return;
        }

        SaveSettings();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        if (_trayMenu is { IsClosing: false } trayMenu) trayMenu.Close();
        _trayMenu = null;
        _saveTimer.Stop();
        _statusTimer.Stop();
        _sessionManager.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
    }

    private sealed class DragPreviewAdorner : Adorner
    {
        private readonly System.Windows.Controls.Image _preview;
        private double _left;
        private double _top;

        public DragPreviewAdorner(UIElement adornedElement, ImageSource image, double width, double height)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            _preview = new System.Windows.Controls.Image
            {
                Source = image,
                Width = width,
                Height = height,
                Opacity = 0.94,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 14,
                    ShadowDepth = 4,
                    Opacity = 0.32
                }
            };
            AddVisualChild(_preview);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) =>
            index == 0 ? _preview : throw new ArgumentOutOfRangeException(nameof(index));

        protected override Size MeasureOverride(Size constraint)
        {
            _preview.Measure(new Size(_preview.Width, _preview.Height));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _preview.Arrange(new Rect(_left, _top, _preview.Width, _preview.Height));
            return finalSize;
        }

        public void MoveTo(double left, double top)
        {
            _left = left;
            _top = top;
            InvalidateArrange();
        }
    }
}
