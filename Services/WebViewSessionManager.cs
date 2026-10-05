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
        public string? LastAllowedNavigationUri { get; set; }
        public bool IsAuthenticating { get; set; }
        public bool IsAwaitingFederatedRedirect { get; set; }
        public HashSet<string> FederatedAuthenticationHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int AvatarDiscoveryGeneration { get; set; }
        public HashSet<ulong> ExternallyHandledNavigationIds { get; } = [];
        public BrowserStatusEventArgs Status { get; set; } = new(string.Empty);
        public BrowserStatusEventArgs? ProcessFailure { get; set; }
    }

    private readonly Dictionary<string, Session> _sessions = [];
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private string? _activeAccountId;
    private string? _previousAccountId;
    private string? _requestedAccountId;
    private long _switchGeneration;
    private bool _disposed;

    public event EventHandler<BrowserStatusEventArgs>? StatusChanged;
    public event EventHandler<AccountProfile>? AccountNavigationChanged;

    public async Task SwitchToAsync(AccountProfile account, MemoryMode mode)
    {
        if (_disposed) return;
        var generation = ++_switchGeneration;
        _requestedAccountId = account.Id;
        if (_activeAccountId != account.Id)
        {
            // Selection labels change before initialization can finish. Hide the
            // previous account immediately so it cannot be used under a new label.
            foreach (var view in host.Children.OfType<WebView2CompositionControl>())
                view.Visibility = Visibility.Collapsed;
        }
        StatusChanged?.Invoke(this, new BrowserStatusEventArgs($"Opening {account.DisplayName}…", isBusy: true));

        await _switchLock.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (generation != _switchGeneration) return;
            if (_activeAccountId == account.Id && _sessions.TryGetValue(account.Id, out var activeSession))
            {
                if (activeSession.ProcessFailure is null)
                {
                    activeSession.View.Visibility = Visibility.Visible;
                    activeSession.View.UpdateLayout();
                    if (activeSession.View.CoreWebView2.IsSuspended)
                        activeSession.View.CoreWebView2.Resume();
                    activeSession.View.Focus();
                }
                ReportStatus(activeSession, activeSession.Status);
                return;
            }

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

            var session = await GetOrCreateSessionAsync(account, generation);
            ThrowIfDisposed();
            if (generation != _switchGeneration) return;
            if (session.ProcessFailure is null)
            {
                // Composition-backed views need a realized WPF surface before their
                // browser capture is resumed, or account switches can return a blank image.
                session.View.Visibility = Visibility.Visible;
                session.View.UpdateLayout();
                if (session.View.CoreWebView2.IsSuspended)
                    session.View.CoreWebView2.Resume();
                session.View.Focus();
            }

            await ReconcileSessionsCoreAsync(mode);
            ReportStatus(session, session.Status);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (_disposed || generation != _switchGeneration) return;
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
            if (_disposed || generation != _switchGeneration) return;
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
        catch (ObjectDisposedException) when (_disposed)
        {
            // Settings may still be waiting for a switch when the app closes.
        }
        finally { _switchLock.Release(); }
    }

    public void RefreshActive()
    {
        if (_activeAccountId is not null &&
            _activeAccountId == _requestedAccountId &&
            _sessions.TryGetValue(_activeAccountId, out var session) &&
            session.ProcessFailure is null &&
            session.View.CoreWebView2 is not null)
        {
            // Federated sign-in pages can contain a one-time SAML request. Reloading
            // one after it expires may fail or try to resubmit stale authentication
            // state, so begin a new Gmail sign-in flow instead.
            if (session.IsAuthenticating && !IsGmailUri(session.View.CoreWebView2.Source))
            {
                RestartAuthentication(session);
                return;
            }

            session.View.Reload();
        }
    }

    public void OpenInbox()
    {
        if (_activeAccountId is null ||
            _activeAccountId != _requestedAccountId ||
            !_sessions.TryGetValue(_activeAccountId, out var session) ||
            session.ProcessFailure is not null ||
            session.View.CoreWebView2 is null)
            return;

        RestartAuthentication(session);
    }

    public void RefreshGmailAvatar(AccountProfile account)
    {
        if (!account.UseGmailAvatar ||
            !_sessions.TryGetValue(account.Id, out var session) ||
            session.ProcessFailure is not null ||
            session.View.CoreWebView2 is null ||
            !IsGmailUri(session.View.CoreWebView2.Source))
            return;

        _ = DiscoverGmailAvatarAsync(session);
    }

    public void GoBack()
    {
        if (_activeAccountId is not null &&
            _activeAccountId == _requestedAccountId &&
            _sessions.TryGetValue(_activeAccountId, out var session) &&
            session.ProcessFailure is null &&
            session.View.CanGoBack)
        {
            session.View.GoBack();
        }
    }

    public void ApplyTheme()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.ProcessFailure is not null) continue;

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

    private async Task<Session> GetOrCreateSessionAsync(AccountProfile account, long generation)
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
        if (generation != _switchGeneration) throw new OperationCanceledException();

        var view = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            AllowDrop = true,
            // WebView2CompositionControl does not currently forward external file
            // drops reliably. Handle the WPF drop below and give the files to
            // Gmail's own attachment input instead.
            AllowExternalDrop = false,
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
            if (generation != _switchGeneration) throw new OperationCanceledException();
            var session = new Session(account, view);
            await ConfigureViewAsync(session);
            ThrowIfDisposed();
            if (generation != _switchGeneration) throw new OperationCanceledException();
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

        view.PreviewDragOver += (_, args) => HandleFileDragOver(session, args);
        view.PreviewDrop += async (_, args) => await HandleFileDropAsync(session, args);

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
            var navigationSource = session.NavigationId == args.NavigationId
                ? session.LastAllowedNavigationUri
                : core.Source;
            var isAuthenticationPage = IsGoogleAuthenticationUri(args.Uri);
            if (isAuthenticationPage)
                session.IsAuthenticating = true;
            if (IsGoogleFederationHandoffUri(args.Uri))
                session.IsAwaitingFederatedRedirect = true;

            var isEmbedded = IsAllowedEmbeddedUri(args.Uri);
            var isTrustedGoogleHandoff = !isEmbedded &&
                                         session.IsAuthenticating &&
                                         IsTrustedGoogleAuthenticationHandoffUri(args.Uri);
            var isFederated = !isEmbedded &&
                              !isTrustedGoogleHandoff &&
                              IsAllowedFederatedAuthenticationNavigation(session, args, navigationSource);
            var isAllowed = isEmbedded || isTrustedGoogleHandoff || isFederated;

            if (isAllowed)
            {
                session.AvatarDiscoveryGeneration++;
                session.NavigationId = args.NavigationId;
                session.LastAllowedNavigationUri = args.Uri;
                ReportStatus(session, new BrowserStatusEventArgs(
                    $"Loading {account.DisplayName}…",
                    isBusy: true));
                return;
            }
            args.Cancel = true;
            session.ExternallyHandledNavigationIds.Add(args.NavigationId);
            if (session.NavigationId == args.NavigationId)
            {
                session.NavigationId = null;
                session.LastAllowedNavigationUri = null;
                ReportStatus(session, new BrowserStatusEventArgs(string.Empty));
            }
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
            session.LastAllowedNavigationUri = null;
            if (!args.IsSuccess)
            {
                ReportStatus(session, new BrowserStatusEventArgs(
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
            ReportStatus(session, new BrowserStatusEventArgs(string.Empty));
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

            var isEmbedded = IsAllowedEmbeddedUri(args.Uri);
            var isTrustedGoogleHandoff = !isEmbedded &&
                                         session.IsAuthenticating &&
                                         IsTrustedGoogleAuthenticationHandoffUri(args.Uri);
            var isFederated = !isEmbedded &&
                              !isTrustedGoogleHandoff &&
                              IsAllowedFederatedPopup(session, args.Uri, core.Source);
            var isAllowed = isEmbedded || isTrustedGoogleHandoff || isFederated;

            if (isAllowed)
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
                    if (IsCurrentSession(session) && session.ProcessFailure is null &&
                        session.View.CoreWebView2 is not null)
                    {
                        session.View.CoreWebView2.Navigate(GmailInboxUrl);
                    }
                });
            }
        };

        core.DownloadStarting += (_, args) =>
        {
            var fileName = Path.GetFileName(args.ResultFilePath);
            ReportStatus(session, new BrowserStatusEventArgs($"Downloading {fileName}…"));
            var operation = args.DownloadOperation;
            operation.StateChanged += DownloadStateChanged;

            void DownloadStateChanged(object? sender, object eventArgs)
            {
                if (operation.State == CoreWebView2DownloadState.Completed)
                {
                    ReportStatus(session, new BrowserStatusEventArgs($"Downloaded {fileName}"));
                    operation.StateChanged -= DownloadStateChanged;
                }
                else if (operation.State == CoreWebView2DownloadState.Interrupted)
                {
                    ReportStatus(session, new BrowserStatusEventArgs($"Download interrupted: {fileName}", isError: true));
                    operation.StateChanged -= DownloadStateChanged;
                }
            }
        };

        core.ProcessFailed += (_, args) =>
        {
            // Edge restarts auxiliary processes (including GPU and utility
            // processes) automatically; those failures do not close the inbox.
            if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                session.ProcessFailure = new BrowserStatusEventArgs(
                    "The Gmail browser process stopped unexpectedly. Select Retry to reopen it.",
                    isError: true);
                ReportStatus(session, session.ProcessFailure);
            }
            else if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                ReportStatus(session, new BrowserStatusEventArgs(
                    "Gmail is not responding. Wait for it to recover, or select Retry to reopen it.",
                    isError: true));
            }
        };
    }

    private static void HandleFileDragOver(Session session, DragEventArgs args)
    {
        if (session.ProcessFailure is not null ||
            !IsGmailUri(session.View.CoreWebView2?.Source) || !ContainsFiles(args.Data))
            return;

        args.Effects = DragDropEffects.Copy;
        args.Handled = true;
    }

    private async Task HandleFileDropAsync(Session session, DragEventArgs args)
    {
        if (session.ProcessFailure is not null ||
            !IsGmailUri(session.View.CoreWebView2?.Source) ||
            args.Data.GetData(DataFormats.FileDrop, true) is not string[] droppedPaths)
            return;

        var dropPoint = args.GetPosition(session.View);
        var files = droppedPaths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        args.Effects = files.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
        if (files.Length == 0) return;

        try
        {
            var attached = await AttachFilesAtPointAsync(session, dropPoint, files);
            var message = attached
                ? files.Length == 1
                    ? $"Adding {Path.GetFileName(files[0])} to the draft…"
                    : $"Adding {files.Length} files to the draft…"
                : "Drop files directly onto an open reply or compose message box.";
            ReportStatus(session, new BrowserStatusEventArgs(message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                   JsonException or System.Runtime.InteropServices.COMException)
        {
            ReportStatus(session, new BrowserStatusEventArgs(
                "Gmail could not add the dropped file. Try the paperclip button instead."));
        }
    }

    private static bool ContainsFiles(IDataObject data) =>
        // Drag-over runs on every pointer movement. Inspect the format here;
        // accessing file metadata (especially on network shares) waits until drop.
        data.GetDataPresent(DataFormats.FileDrop, true);

    private static async Task<bool> AttachFilesAtPointAsync(
        Session session,
        Point dropPoint,
        string[] files)
    {
        var core = session.View.CoreWebView2;
        if (session.View.ActualWidth <= 0 || session.View.ActualHeight <= 0) return false;

        // WPF reports control coordinates, while the DOM expects CSS pixels.
        // Relative coordinates remain accurate after browser zoom or app scaling.
        var pointJson = JsonSerializer.Serialize(new
        {
            x = dropPoint.X / session.View.ActualWidth,
            y = dropPoint.Y / session.View.ActualHeight
        });
        var expression = $$"""
            (() => {
                const point = {{pointJson}};
                const hit = document.elementFromPoint(
                    point.x * window.innerWidth, point.y * window.innerHeight);
                const editor = hit?.closest?.('[contenteditable="true"]');
                if (!editor) return null;

                for (let container = editor; container && container !== document.body; container = container.parentElement) {
                    const inputs = Array.from(
                        container.querySelectorAll?.('input[type="file"]:not([disabled])') ?? []
                    );
                    if (inputs.length === 0) continue;

                    return inputs.find(input =>
                        input.name === 'Filedata' ||
                        input.multiple ||
                        /attach|file/i.test(`${input.name} ${input.id} ${input.getAttribute('aria-label') ?? ''}`)
                    ) ?? inputs[0];
                }

                return null;
            })()
            """;
        var objectGroup = $"gmailDesktopDrop-{Guid.NewGuid():N}";

        try
        {
            var evaluationJson = await core.CallDevToolsProtocolMethodAsync(
                "Runtime.evaluate",
                JsonSerializer.Serialize(new
                {
                    expression,
                    objectGroup,
                    silent = true,
                    returnByValue = false,
                    userGesture = true
                }));
            using var evaluation = JsonDocument.Parse(evaluationJson);
            if (!evaluation.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("objectId", out var objectIdElement))
                return false;

            var objectId = objectIdElement.GetString();
            if (string.IsNullOrWhiteSpace(objectId)) return false;

            await core.CallDevToolsProtocolMethodAsync(
                "DOM.setFileInputFiles",
                JsonSerializer.Serialize(new { files, objectId }));
            return true;
        }
        finally
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(
                    "Runtime.releaseObjectGroup",
                    JsonSerializer.Serialize(new { objectGroup }));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                       System.Runtime.InteropServices.COMException)
            {
                // Navigation or disposal can release the remote objects first.
            }
        }
    }

    private async Task DiscoverGmailAvatarAsync(Session session)
    {
        var generation = ++session.AvatarDiscoveryGeneration;
        const string findAvatarScript = """
            (() => {
                const isGoogleAvatarUrl = value => {
                    if (!value) return null;
                    try {
                        const url = new URL(value, document.baseURI);
                        const host = url.hostname.toLowerCase();
                        const allowedHost = host === 'googleusercontent.com' ||
                            host.endsWith('.googleusercontent.com') ||
                            host === 'ggpht.com' ||
                            host.endsWith('.ggpht.com');
                        return url.protocol === 'https:' && allowedHost ? url.href : null;
                    } catch {
                        return null;
                    }
                };

                const sourceFrom = element => {
                    const directSources = [
                        element.getAttribute?.('data-src'),
                        element.getAttribute?.('data-lazy-src'),
                        element.currentSrc,
                        element.getAttribute?.('src')
                    ];
                    for (const source of directSources) {
                        const avatarUrl = isGoogleAvatarUrl(source);
                        if (avatarUrl) return avatarUrl;
                    }

                    const backgrounds = [
                        element.style?.backgroundImage,
                        getComputedStyle(element).backgroundImage
                    ];
                    for (const background of backgrounds) {
                        const match = /url\(["']?(.+?)["']?\)/i.exec(background ?? '');
                        const avatarUrl = isGoogleAvatarUrl(match?.[1]);
                        if (avatarUrl) return avatarUrl;
                    }

                    return null;
                };

                const accountControls = [
                    ...document.querySelectorAll('#gb a[href*="accounts.google.com"]'),
                    ...document.querySelectorAll('#gb [data-ogsr-up]'),
                    ...document.querySelectorAll('a[href*="/SignOutOptions"]'),
                    ...document.querySelectorAll('a[href*="/AccountChooser"]'),
                    ...document.querySelectorAll('a[href*="/ManageAccount"]')
                ];
                const candidates = new Set();
                for (const control of accountControls) {
                    candidates.add(control);
                    for (const descendant of control.querySelectorAll('img, [data-src], [style*="background"]'))
                        candidates.add(descendant);
                }

                // The One Google Bar changes its wrapper elements regularly. Its
                // location is stable, so use its image-bearing elements as a
                // constrained fallback without depending on localized labels.
                for (const element of document.querySelectorAll(
                    '#gb img, #gb [data-src], #gb [style*="background-image"]'))
                    candidates.add(element);

                for (const candidate of candidates) {
                    const avatarUrl = sourceFrom(candidate);
                    if (avatarUrl) return avatarUrl;
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
                    // Gmail lazy-loads the account image through transparent data
                    // URLs in some layouts. The model intentionally rejects those;
                    // keep looking instead of treating the placeholder as success.
                    if (string.IsNullOrWhiteSpace(session.Account.GmailAvatarUrl))
                    {
                        await Task.Delay(650);
                        continue;
                    }

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
            if (pair.Value.ProcessFailure is not null) continue;

            try
            {
                if (!pair.Value.View.CoreWebView2.IsSuspended)
                    await pair.Value.View.CoreWebView2.TrySuspendAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                       System.Runtime.InteropServices.COMException)
            {
                // Suspension is best-effort; Edge may be busy, or the account may
                // have been closed while the asynchronous suspension was pending.
            }
        }
    }

    private void SaveCurrentUrl(Session session)
    {
        string? source;
        try
        {
            source = session.View.Source?.AbsoluteUri ?? session.View.CoreWebView2?.Source;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                   System.Runtime.InteropServices.COMException)
        {
            // A crashed browser can no longer report its URL. Keep the last saved
            // location so disposal and Retry can still rebuild the account view.
            return;
        }

        if (!IsSafePersistedGmailUrl(source)) return;
        session.Account.LastUrl = source!;
        session.Account.LastUsedUtc = DateTime.UtcNow;
        if (!_disposed) AccountNavigationChanged?.Invoke(this, session.Account);
    }

    private bool IsCurrentSession(Session session) =>
        !_disposed &&
        _sessions.TryGetValue(session.Account.Id, out var current) &&
        ReferenceEquals(current, session);

    private void ReportStatus(Session session, BrowserStatusEventArgs status)
    {
        if (!IsCurrentSession(session)) return;

        // Late download or drop callbacks cannot recover a closed browser.
        // Keep its Retry action visible until the session is recreated.
        status = session.ProcessFailure ?? status;
        session.Status = status;
        if (_activeAccountId == session.Account.Id && _requestedAccountId == session.Account.Id)
            StatusChanged?.Invoke(this, status);
    }

    private static void RestartAuthentication(Session session)
    {
        session.IsAuthenticating = true;
        session.IsAwaitingFederatedRedirect = false;
        session.FederatedAuthenticationHosts.Clear();
        session.LastAllowedNavigationUri = null;
        session.View.CoreWebView2.Navigate(GmailInboxUrl);
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

    private static bool IsGoogleFederationHandoffUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase))
            return false;

        if (uri.AbsolutePath.Equals("/samlredirect", StringComparison.OrdinalIgnoreCase))
            return true;

        // Expired Workspace sessions now commonly resume through versioned
        // /signin/continue pages before Google hands the browser to the IdP.
        // Only treat that page as a federation boundary when it is returning
        // to Gmail; other Google Account continuation flows stay constrained.
        return uri.AbsolutePath.EndsWith("/signin/continue", StringComparison.OrdinalIgnoreCase) &&
               ContainsGmailDestination(value);
    }

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

        var hasSource = Uri.TryCreate(currentSource, UriKind.Absolute, out var source);
        var sourceIsFederated = hasSource &&
                                session.FederatedAuthenticationHosts.Contains(source!.Host);
        var sourceIsGoogleAuthentication = hasSource &&
                                           IsGoogleAuthenticationUri(source!.AbsoluteUri);
        var isInitialGoogleHandoff = session.IsAwaitingFederatedRedirect &&
                                     session.FederatedAuthenticationHosts.Count == 0 &&
                                     args.IsRedirected;
        isInitialGoogleHandoff |= session.FederatedAuthenticationHosts.Count == 0 &&
                                  sourceIsGoogleAuthentication;
        var mayExtendFederationChain = session.FederatedAuthenticationHosts.Count < 5 &&
                                       (isInitialGoogleHandoff || sourceIsFederated);
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
            !Uri.TryCreate(sourceValue, UriKind.Absolute, out var source))
            return false;

        if (session.FederatedAuthenticationHosts.Contains(source.Host))
        {
            if (session.FederatedAuthenticationHosts.Contains(target.Host) ||
                IsTrustedGoogleAuthenticationHandoffUri(targetValue))
                return true;

            if (session.FederatedAuthenticationHosts.Count >= 5)
                return false;

            session.FederatedAuthenticationHosts.Add(target.Host);
            return true;
        }

        if (session.FederatedAuthenticationHosts.Count != 0 ||
            !IsGoogleAuthenticationUri(sourceValue))
            return false;

        session.IsAwaitingFederatedRedirect = false;
        session.FederatedAuthenticationHosts.Add(target.Host);
        return true;
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
