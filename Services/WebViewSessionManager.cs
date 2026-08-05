using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GmailDesktop.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GmailDesktop.Services;

public sealed class BrowserStatusEventArgs(string message, bool isBusy = false, bool isError = false) : EventArgs
{
    public string Message { get; } = message;
    public bool IsBusy { get; } = isBusy;
    public bool IsError { get; } = isError;
}

public sealed class WebViewSessionManager(Grid host, SettingsService settingsService) : IDisposable
{
    private const string GmailInboxUrl = "https://mail.google.com/mail/";

    private sealed class Session(AccountProfile account, WebView2CompositionControl view)
    {
        public AccountProfile Account { get; } = account;
        public WebView2CompositionControl View { get; } = view;
        public ulong? NavigationId { get; set; }
        public bool IsAuthenticating { get; set; }
        public bool IsAwaitingFederatedRedirect { get; set; }
        public HashSet<string> FederatedAuthenticationHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int AvatarDiscoveryGeneration { get; set; }
        public HashSet<ulong> ExternallyHandledNavigationIds { get; } = [];
    }

    private readonly Dictionary<string, Session> _sessions = [];
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private string? _activeAccountId;
    private string? _previousAccountId;
    private bool _disposed;

    public event EventHandler<BrowserStatusEventArgs>? StatusChanged;
    public event EventHandler<AccountProfile>? AccountNavigationChanged;

    public async Task SwitchToAsync(AccountProfile account, MemoryMode mode)
    {
        await _switchLock.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (_activeAccountId == account.Id && _sessions.ContainsKey(account.Id)) return;

            var oldActiveId = _activeAccountId;
            if (oldActiveId is not null && _sessions.TryGetValue(oldActiveId, out var oldSession))
            {
                SaveCurrentUrl(oldSession);
                oldSession.View.Visibility = Visibility.Collapsed;
            }

            if (mode == MemoryMode.LowestMemory && oldActiveId is not null)
                DisposeSession(oldActiveId);

            _previousAccountId = oldActiveId;
            _activeAccountId = account.Id;
            StatusChanged?.Invoke(this, new BrowserStatusEventArgs($"Opening {account.DisplayName}…", isBusy: true));

            var session = await GetOrCreateSessionAsync(account);
            ThrowIfDisposed();
            // Composition-backed views need a realized WPF surface before their
            // browser capture is resumed, or account switches can return a blank image.
            session.View.Visibility = Visibility.Visible;
            session.View.UpdateLayout();
            if (session.View.CoreWebView2.IsSuspended)
                session.View.CoreWebView2.Resume();
            session.View.Focus();

            await ReconcileSessionsCoreAsync(mode);
            if (session.NavigationId is null)
                ReportStatus(account, new BrowserStatusEventArgs(string.Empty));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (_disposed) return;
            StatusChanged?.Invoke(this, new BrowserStatusEventArgs(
                "Microsoft Edge WebView2 Runtime is required. Install it, then select Retry.",
                isError: true));
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // The application closed while WebView2 was being initialized.
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            StatusChanged?.Invoke(this, new BrowserStatusEventArgs(
                $"Gmail could not be opened: {ex.Message}",
                isError: true));
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async Task ReconcileSessionsAsync(MemoryMode mode)
    {
        await _switchLock.WaitAsync();
        try
        {
            ThrowIfDisposed();
            await ReconcileSessionsCoreAsync(mode);
        }
        finally { _switchLock.Release(); }
    }

    public void RefreshActive()
    {
        if (_activeAccountId is not null &&
            _sessions.TryGetValue(_activeAccountId, out var session) &&
            session.View.CoreWebView2 is not null)
        {
            session.View.Reload();
        }
    }

    public void OpenInbox()
    {
        if (_activeAccountId is null ||
            !_sessions.TryGetValue(_activeAccountId, out var session) ||
            session.View.CoreWebView2 is null)
            return;

        session.IsAuthenticating = true;
        session.View.CoreWebView2.Navigate(GmailInboxUrl);
    }

    public void RefreshGmailAvatar(AccountProfile account)
    {
        if (!account.UseGmailAvatar ||
            !_sessions.TryGetValue(account.Id, out var session) ||
            session.View.CoreWebView2 is null ||
            !IsGmailUri(session.View.CoreWebView2.Source))
            return;

        _ = DiscoverGmailAvatarAsync(session);
    }

    public void GoBack()
    {
        if (_activeAccountId is not null &&
            _sessions.TryGetValue(_activeAccountId, out var session) &&
            session.View.CanGoBack)
        {
            session.View.GoBack();
        }
    }

    public void ApplyTheme()
    {
        foreach (var session in _sessions.Values)
        {
            session.View.DefaultBackgroundColor = ThemeService.IsDark
                ? System.Drawing.Color.FromArgb(32, 37, 43)
                : System.Drawing.Color.White;
            if (session.View.CoreWebView2 is not null)
            {
                session.View.CoreWebView2.Profile.PreferredColorScheme = ThemeService.IsDark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
        }
    }

    public void DisposeSession(string accountId)
    {
        if (!_sessions.Remove(accountId, out var session))
        {
            if (_activeAccountId == accountId) _activeAccountId = null;
            if (_previousAccountId == accountId) _previousAccountId = null;
            return;
        }
        SaveCurrentUrl(session);
        session.AvatarDiscoveryGeneration++;
        host.Children.Remove(session.View);
        session.View.Dispose();
        if (_activeAccountId == accountId) _activeAccountId = null;
        if (_previousAccountId == accountId) _previousAccountId = null;
    }

    public async Task ClearAndDisposeSessionAsync(string accountId)
    {
        await _switchLock.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(accountId, out var session)) return;

            try
            {
                await session.View.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.AllProfile);
            }
            catch
            {
                // The profile directory is deleted after disposal even if Edge cannot clear it.
            }

            DisposeSession(accountId);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private async Task<Session> GetOrCreateSessionAsync(AccountProfile account)
    {
        if (_sessions.TryGetValue(account.Id, out var existing)) return existing;

        Directory.CreateDirectory(settingsService.GetProfileDirectory(account));
        var options = new CoreWebView2EnvironmentOptions
        {
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            AreBrowserExtensionsEnabled = false,
            EnableTrackingPrevention = true,
            ExclusiveUserDataFolderAccess = true,
            IsCustomCrashReportingEnabled = true
        };
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: settingsService.GetProfileDirectory(account),
            options: options);
        ThrowIfDisposed();

        var view = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // The composition control must have a realized, visible WPF surface
            // before WebView2 initializes its capture target. Initializing it while
            // Collapsed can leave navigation running behind a permanently blank image.
            Visibility = Visibility.Visible,
            ZoomFactor = 1,
            DefaultBackgroundColor = ThemeService.IsDark
                ? System.Drawing.Color.FromArgb(32, 37, 43)
                : System.Drawing.Color.White
        };

        Panel.SetZIndex(view, 1);
        host.Children.Add(view);
        view.ApplyTemplate();
        host.UpdateLayout();

        try
        {
            await view.EnsureCoreWebView2Async(environment);
            ThrowIfDisposed();
            var session = new Session(account, view);
            await ConfigureViewAsync(session);
            ThrowIfDisposed();
            var destination = IsSafePersistedGmailUrl(account.LastUrl)
                ? account.LastUrl
                : "https://mail.google.com/";
            _sessions.Add(account.Id, session);
            view.CoreWebView2.Navigate(destination);
            return session;
        }
        catch
        {
            _sessions.Remove(account.Id);
            host.Children.Remove(view);
            view.Dispose();
            throw;
        }
    }

    private async Task ConfigureViewAsync(Session session)
    {
        var account = session.Account;
        var view = session.View;
        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsReputationCheckingRequired = true;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;

        await core.Profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.PasswordAutosave |
            CoreWebView2BrowsingDataKinds.GeneralAutofill |
            CoreWebView2BrowsingDataKinds.BrowsingHistory |
            CoreWebView2BrowsingDataKinds.DownloadHistory |
            CoreWebView2BrowsingDataKinds.Settings);
        ThrowIfDisposed();

        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;
        core.Profile.PreferredColorScheme = ThemeService.IsDark
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;

        core.NavigationStarting += (_, args) =>
        {
            session.AvatarDiscoveryGeneration++;
            var isAuthenticationPage = IsGoogleAuthenticationUri(args.Uri);
            if (isAuthenticationPage)
                session.IsAuthenticating = true;
            if (IsGoogleSamlRedirectUri(args.Uri))
                session.IsAwaitingFederatedRedirect = true;

            var isAllowed = IsAllowedEmbeddedUri(args.Uri) ||
                            (session.IsAuthenticating && IsTrustedGoogleAuthenticationHandoffUri(args.Uri)) ||
                            IsAllowedFederatedAuthenticationNavigation(session, args, core.Source);

            if (isAllowed)
            {
                session.NavigationId = args.NavigationId;
                ReportStatus(account, new BrowserStatusEventArgs(
                    $"Loading {account.DisplayName}…",
                    isBusy: true));
                return;
            }
            args.Cancel = true;
            session.NavigationId = null;
            session.ExternallyHandledNavigationIds.Add(args.NavigationId);
            ReportStatus(account, new BrowserStatusEventArgs(string.Empty));
            if (args.IsUserInitiated)
                OpenInDefaultBrowser(args.Uri);
        };

        core.FrameNavigationStarting += (_, args) =>
        {
            if (!IsAllowedFrameUri(args.Uri)) args.Cancel = true;
        };

        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
            args.Handled = true;
        };

        core.BasicAuthenticationRequested += (_, args) => args.Cancel = true;
        core.ServerCertificateErrorDetected += (_, args) =>
            args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;

        core.NavigationCompleted += (_, args) =>
        {
            if (session.ExternallyHandledNavigationIds.Remove(args.NavigationId)) return;
            if (session.NavigationId != args.NavigationId) return;

            session.NavigationId = null;
            if (!args.IsSuccess)
            {
                ReportStatus(account, new BrowserStatusEventArgs(
                    "Gmail did not finish loading. Check your connection and try again.",
                    isError: true));
                return;
            }

            if (IsGmailUri(core.Source))
            {
                session.IsAuthenticating = false;
                session.IsAwaitingFederatedRedirect = false;
                session.FederatedAuthenticationHosts.Clear();
                if (account.UseGmailAvatar)
                    _ = DiscoverGmailAvatarAsync(session);
            }
            SaveCurrentUrl(session);
            ReportStatus(account, new BrowserStatusEventArgs(string.Empty));
        };

        core.SourceChanged += (_, _) => SaveCurrentUrl(session);
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;

            // An empty popup has no safe destination to reuse in the main view, and
            // CoreWebView2.Navigate rejects it. Suppress it instead of crashing the UI.
            if (string.IsNullOrWhiteSpace(args.Uri) || args.Uri == "about:blank")
                return;

            // Gmail's welcome page opens the inbox/account chooser in a new window.
            // Keep that transition in this account's isolated browser profile.
            if (IsGmailWelcomeUri(core.Source))
            {
                session.IsAuthenticating = true;
                core.Navigate(GmailInboxUrl);
                return;
            }

            if (IsGoogleAuthenticationUri(args.Uri))
                session.IsAuthenticating = true;

            if (IsAllowedEmbeddedUri(args.Uri) ||
                (session.IsAuthenticating && IsTrustedGoogleAuthenticationHandoffUri(args.Uri)) ||
                IsAllowedFederatedPopup(session, args.Uri, core.Source))
            {
                core.Navigate(args.Uri);
            }
            else if (args.IsUserInitiated)
            {
                OpenInDefaultBrowser(args.Uri);
            }
        };

        core.LaunchingExternalUriScheme += (_, args) =>
        {
            args.Cancel = true;
            var containsGmailDestination = ContainsGmailDestination(args.Uri);

            if (session.IsAuthenticating && containsGmailDestination)
            {
                view.Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && session.View.CoreWebView2 is not null)
                    {
                        session.View.CoreWebView2.Navigate(GmailInboxUrl);
                    }
                });
            }
        };

        core.DownloadStarting += (_, args) =>
        {
            var fileName = Path.GetFileName(args.ResultFilePath);
            ReportStatus(account, new BrowserStatusEventArgs($"Downloading {fileName}…"));
            var operation = args.DownloadOperation;
            operation.StateChanged += DownloadStateChanged;

            void DownloadStateChanged(object? sender, object eventArgs)
            {
                if (operation.State == CoreWebView2DownloadState.Completed)
                {
                    ReportStatus(account, new BrowserStatusEventArgs($"Downloaded {fileName}"));
                    operation.StateChanged -= DownloadStateChanged;
                }
                else if (operation.State == CoreWebView2DownloadState.Interrupted)
                {
                    ReportStatus(account, new BrowserStatusEventArgs($"Download interrupted: {fileName}", isError: true));
                    operation.StateChanged -= DownloadStateChanged;
                }
            }
        };

        core.ProcessFailed += (_, _) => ReportStatus(account, new BrowserStatusEventArgs(
            "The Gmail browser process stopped unexpectedly. Select Retry to reopen it.",
            isError: true));
    }

    private async Task DiscoverGmailAvatarAsync(Session session)
    {
        var generation = ++session.AvatarDiscoveryGeneration;
        const string findAvatarScript = """
            (() => {
                const selectors = [
                    'a[href*="accounts.google.com/SignOutOptions"] img[src]',
                    'a[href*="/SignOutOptions"] img[src]',
                    'a[href*="accounts.google.com/AccountChooser"] img[src]',
                    'a[href*="accounts.google.com/ManageAccount"] img[src]',
                    'a[aria-label*="Google Account"] img[src]',
                    'button[aria-label*="Google Account"] img[src]'
                ];

                for (const selector of selectors) {
                    const image = document.querySelector(selector);
                    const source = image?.currentSrc || image?.src;
                    if (source) return source;
                }
                return null;
            })()
            """;

        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var core = session.View.CoreWebView2;
                if (_disposed ||
                    generation != session.AvatarDiscoveryGeneration ||
                    !session.Account.UseGmailAvatar ||
                    core is null ||
                    !IsGmailUri(core.Source))
                    return;

                var result = await core.ExecuteScriptAsync(findAvatarScript);
                if (_disposed ||
                    generation != session.AvatarDiscoveryGeneration ||
                    !session.Account.UseGmailAvatar ||
                    !IsGmailUri(core.Source))
                    return;

                var avatarUrl = JsonSerializer.Deserialize<string?>(result);
                if (!string.IsNullOrWhiteSpace(avatarUrl))
                {
                    var previousUrl = session.Account.GmailAvatarUrl;
                    session.Account.GmailAvatarUrl = avatarUrl;
                    if (!string.Equals(previousUrl, session.Account.GmailAvatarUrl, StringComparison.Ordinal))
                        AccountNavigationChanged?.Invoke(this, session.Account);
                    return;
                }

                await Task.Delay(650);
            }
        }
        catch (Exception ex) when (ex is JsonException or ObjectDisposedException or
                                   InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Gmail may still be loading or the account may have been switched. Initials remain visible.
        }
    }

    private async Task ReconcileSessionsCoreAsync(MemoryMode mode)
    {
        var keep = mode switch
        {
            MemoryMode.LowestMemory => new HashSet<string>(
                _activeAccountId is null ? [] : [_activeAccountId]),
            MemoryMode.Balanced => new HashSet<string>(
                new[] { _activeAccountId, _previousAccountId }.OfType<string>()),
            _ => new HashSet<string>(_sessions.Keys)
        };

        foreach (var id in _sessions.Keys.Where(id => !keep.Contains(id)).ToArray())
            DisposeSession(id);

        foreach (var pair in _sessions.Where(pair => pair.Key != _activeAccountId).ToArray())
        {
            pair.Value.View.Visibility = Visibility.Collapsed;
            try
            {
                if (!pair.Value.View.CoreWebView2.IsSuspended)
                    await pair.Value.View.CoreWebView2.TrySuspendAsync();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Suspension is best-effort; the view remains hidden if Edge is busy.
            }
        }
    }

    private void SaveCurrentUrl(Session session)
    {
        var source = session.View.Source?.AbsoluteUri ?? session.View.CoreWebView2?.Source;
        if (!IsSafePersistedGmailUrl(source)) return;
        session.Account.LastUrl = source!;
        session.Account.LastUsedUtc = DateTime.UtcNow;
        if (!_disposed) AccountNavigationChanged?.Invoke(this, session.Account);
    }

    private void ReportStatus(AccountProfile account, BrowserStatusEventArgs status)
    {
        if (!_disposed && _activeAccountId == account.Id)
            StatusChanged?.Invoke(this, status);
    }

    private static bool IsSafePersistedGmailUrl(string? uri) =>
        IsGmailUri(uri, requireCanonicalHost: true);

    private static bool IsAllowedEmbeddedUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "about:blank") return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        return IsGmailUri(value) || IsGoogleAuthenticationHost(uri.Host);
    }

    private static bool IsGmailUri(string? value, bool requireCanonicalHost = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (uri.Host.Equals("mail.google.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return !requireCanonicalHost &&
               (uri.Host.Equals("gmail.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("www.gmail.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGoogleAuthenticationUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        IsGoogleAuthenticationHost(uri.Host);

    private static bool IsGoogleAuthenticationHost(string host) =>
        host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("consent.google.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedGoogleAuthenticationHandoffUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return IsHostOrSubdomainOf(uri.Host, "google.com") ||
               IsHostOrSubdomainOf(uri.Host, "googleusercontent.com") ||
               IsHostOrSubdomainOf(uri.Host, "googleapis.com") ||
               IsHostOrSubdomainOf(uri.Host, "gstatic.com") ||
               uri.Host.Equals("accounts.youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGmailWelcomeUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var isGoogleWelcomeHost = uri.Host.Equals("workspace.google.com", StringComparison.OrdinalIgnoreCase) ||
                                  uri.Host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase);
        if (isGoogleWelcomeHost && uri.AbsolutePath.Contains("/gmail", StringComparison.OrdinalIgnoreCase))
            return true;

        return (uri.Host.Equals("gmail.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("www.gmail.com", StringComparison.OrdinalIgnoreCase)) &&
               uri.AbsolutePath.Contains("/about", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGoogleSamlRedirectUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals("/samlredirect", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedFederatedAuthenticationNavigation(
        Session session,
        CoreWebView2NavigationStartingEventArgs args,
        string? currentSource)
    {
        if (!session.IsAuthenticating ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttps)
            return false;

        if (session.FederatedAuthenticationHosts.Contains(target.Host))
            return true;

        var sourceIsFederated = Uri.TryCreate(currentSource, UriKind.Absolute, out var source) &&
                                session.FederatedAuthenticationHosts.Contains(source.Host);
        var mayExtendFederationChain = args.IsRedirected &&
                                       session.FederatedAuthenticationHosts.Count < 5 &&
                                       (session.IsAwaitingFederatedRedirect || sourceIsFederated);
        if (!mayExtendFederationChain)
            return false;

        session.IsAwaitingFederatedRedirect = false;
        session.FederatedAuthenticationHosts.Add(target.Host);
        return true;
    }

    private static bool IsAllowedFederatedPopup(Session session, string? targetValue, string? sourceValue)
    {
        if (!session.IsAuthenticating ||
            !Uri.TryCreate(targetValue, UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(sourceValue, UriKind.Absolute, out var source) ||
            !session.FederatedAuthenticationHosts.Contains(source.Host))
            return false;

        return session.FederatedAuthenticationHosts.Contains(target.Host) ||
               IsTrustedGoogleAuthenticationHandoffUri(targetValue);
    }

    private static bool ContainsGmailDestination(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var decoded = value;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (decoded.Contains("mail.google.com", StringComparison.OrdinalIgnoreCase) ||
                decoded.Contains("gmail.com", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                var next = Uri.UnescapeDataString(decoded);
                if (next == decoded) break;
                decoded = next;
            }
            catch (UriFormatException)
            {
                break;
            }
        }

        return false;
    }

    private static bool IsHostOrSubdomainOf(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedFrameUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "about:blank") return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;

        return uri.Scheme == "blob" &&
               value.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenInDefaultBrowser(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme is not ("https" or "http" or "mailto")) return;
        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // The OS may not have a registered handler; keep Gmail running.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _sessions.Keys.ToArray()) DisposeSession(id);
    }
}
