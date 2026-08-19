# Security model

Gmail Desktop is deliberately a small native shell around Microsoft's Evergreen WebView2 Runtime. It does not use the Gmail API, IMAP, an application server, analytics, or a custom authentication flow.

## Credentials and account data

- Google sign-in is rendered by `https://accounts.google.com` inside WebView2. The WPF host never receives the password, form contents, or authentication response.
- Password autosave and general form autofill are explicitly disabled. Previously stored password/autofill entries in an account profile are cleared before navigation.
- The application stores no Google password, OAuth client secret, API token, or recovery code.
- Persistent Google session cookies, site storage, and cached resources remain in that account's WebView2 user data folder. These are required to keep the account signed in and must be treated as sensitive.
- Account labels, optional email addresses, colors, the selected account, and last Gmail URLs are encrypted with Windows DPAPI using `CurrentUser` scope in `%LOCALAPPDATA%\GmailDesktop\settings.dat`.
- If the user enables a Gmail profile-photo icon, the host reads only the Google-hosted avatar URL from the signed-in Gmail page. The validated `googleusercontent.com` or `ggpht.com` URL is stored in the same DPAPI-encrypted settings file; colored initials remain the fallback.



## WebView2 hardening

- Gmail, its official `gmail.com` entry aliases, and Google Account sign-in pages remain embedded. While a sign-in is actively in progress, HTTPS handoffs on Google-owned `google.com`, `googleusercontent.com`, `googleapis.com`, and `gstatic.com` hosts, plus Google's `accounts.youtube.com` session-synchronization endpoint, remain in the same isolated WebView2 profile so multi-step authentication can complete. Gmail welcome-page inbox popups are redirected to the canonical inbox inside that profile. Once Gmail finishes loading, the temporary handoff allowance is disabled.
- For federated Google Workspace accounts, an external HTTPS identity-provider host is admitted only when it is introduced during an active Gmail sign-in by Google's `accounts.google.com` flow. HTTPS redirects, form navigations, and authentication popups within that bounded federation chain may add up to five hosts so multi-provider university MFA can finish in the same profile. The temporary host set is isolated to one account session and cleared as soon as Gmail loads.
- External HTTP/HTTPS and `mailto:` links are handed to the Windows default application. Other schemes are rejected.
- TLS certificate errors are cancelled. The application never bypasses a certificate warning.
- DevTools, native host objects, web messaging, browser extensions, password saving, general autofill, and default JavaScript dialogs are disabled.
- SmartScreen reputation checking and WebView2 tracking prevention are requested explicitly.
- WebView2 user data folders use exclusive access, preventing another WebView2 process from attaching a controller to the same profile while Gmail Desktop owns it.
- Browser permission requests are denied and are not persisted. This includes camera, microphone, location, notifications, sensors, and clipboard-read requests.
- HTTP Basic/Digest/NTLM credential prompts from web content are cancelled. Gmail's normal HTML authentication is unaffected.
- Child frames must use HTTPS, `about:blank`, or an HTTPS-origin `blob:` URL.
- The app runs `asInvoker` and does not request administrator privileges.
- The app uses the Evergreen WebView2 Runtime so browser security updates are serviced by Microsoft.



## Profile lifecycle

Every Gmail account has a distinct user data folder under `%LOCALAPPDATA%\GmailDesktop\Profiles`. WebView2 stores cookies, DOM storage, cache, history, and other browser state there.

On profile startup, Gmail Desktop clears password-autosave data, form autofill, browsing history, download history, and saved browser permission settings. Cookies and Gmail site storage are retained so the account stays signed in.

When an account is removed, the app asks WebView2 to clear all profile browsing data, disposes the browser session, and deletes that account's user data folder. Downloaded attachments are separate files outside the profile and are not deleted automatically.

## Important trust boundaries

No desktop application can guarantee security after Windows or the signed-in Windows account is compromised. In particular:

- Malware running as the same Windows user, an administrator, a debugger, or a compromised browser runtime may be able to inspect process memory, invoke DPAPI as that user, or copy session data.
- A person with access to an unlocked Windows session can use the signed-in Gmail session.
- Enterprise TLS inspection or malicious root certificates installed into the Windows trust store can inspect traffic that Edge/WebView2 considers trusted.
- Disk backups, forensic recovery, crash artifacts, hibernation files, and downloaded attachments are outside the app's complete control.
- Compromise of Google, Microsoft WebView2, Windows, or their update channels is outside the app's security boundary.
- The locally published executable is not Authenticode-signed.

For stronger protection, use Windows Hello, full-disk encryption, Google passkeys or multi-factor authentication, automatic screen locking, current Windows/WebView2 updates, and reputable endpoint protection. Do not run the app elevated.

## Relevant platform guidance

- [Develop secure WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)
- [Manage WebView2 user data folders](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)
- [Clear browsing data from WebView2 profiles](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/clear-browsing-data)
- [Windows/.NET Data Protection API](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection)
