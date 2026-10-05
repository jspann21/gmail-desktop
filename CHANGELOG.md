# Changelog

## [1.2.1] - 2026-10-05

- Restored normal Windows title-bar behavior: drag a maximized window down to restore it, double-click to maximize or restore, and use the system menu. Restoring from the notification area now preserves a maximized window.
- Fixed rapid account switching so an older Gmail view or error cannot appear under the newly selected account. Browser failures retain their Retry action when switching accounts, while recoverable helper-process failures no longer block the inbox.
- Refreshing an expired federated sign-in now starts a fresh Gmail authentication request instead of reloading stale SAML state.
- Deferred keyboard shortcuts until WebView2's key callback finishes, avoiding browser operations while its process is blocked.
- Kept account and settings dialogs on the owner's monitor, including smaller or high-DPI displays. Account dialogs now honor program zoom, and the settings footer shows the current app version.
- Updated the System theme while Windows theme changes, corrected account initials containing emoji or accented characters, and improved notification-area menu placement across displays with different DPI settings.
- Saved pending settings during shutdown, hardened account removal against switching races, and corrected the local publish directory used by the installer.

## [1.2.0] - 2026-08-31

- Added support for dropping files directly into Gmail drafts as attachments.
- Improved Gmail profile photo discovery, including more reliable detection when profile imagery loads or changes dynamically.

## [1.1.1] - 2026-08-19

- Fixed reauthentication for federated university and Workspace accounts, including direct Google `ServiceLogin` handoffs and newer `signin/continue` flows.

## [1.1.0] - 2026-08-12

- Replaced the portable release executable with a single per-user Windows installer.
- Fixed a WebView2 shutdown crash that could occur when a cleanup utility deleted the extracted `WebView2Loader.dll` from `%TEMP%` while Gmail Desktop was running.
- The installer now keeps the native WebView2 loader in the stable application directory. No application logging was added.

## [1.0.0] - 2026-08-05

- Initial release.
