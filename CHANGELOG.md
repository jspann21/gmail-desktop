# Changelog

## Unreleased

- Refreshing an expired federated sign-in now starts a fresh Gmail authentication request instead of reloading stale SAML state.

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
