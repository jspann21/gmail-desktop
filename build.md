## WPF/WebView2 Gmail App — Basic Design Summary

Ultimate goal: The app would be a lightweight Windows desktop wrapper for the **normal Gmail website**, designed around one main goal: lower memory and cpu usage with multiple Gmail accounts signed in while avoiding the memory cost of several Gmail tabs being active at once in Chrome.

### Core design

* Use the latest tech stack for the best design.
* Built with **C# and WPF**
* Uses **Microsoft WebView2** to display the real Gmail webpage
* Has a native Windows sidebar listing each Gmail account
* Each account gets its own persistent WebView2 profile, so accounts remain independently signed in

### How account switching works

When you select another account:

1. The current Gmail WebView is closed or disposed.
2. Its cookies and login session remain saved on disk.
3. A new WebView is created using the selected account’s profile.
4. Gmail reloads already signed in to that account.

This gives direct one-click account switching without using Gmail’s built-in account menu.

### Why it should use less memory

The major saving comes from keeping only one full Gmail webpage alive.

Instead of:

```text
5 Gmail accounts
= 5 Gmail pages
= 5 active JavaScript applications
```

the app would use:

```text
5 saved account profiles
+ 1 active Gmail page
+ 1 small WPF application shell
```

The saved profiles use disk space but relatively little active memory.

### Expected trade-off

Switching accounts would not be completely instant because Gmail must reload when an inactive account is reopened.

That delay is the main trade-off for reducing memory. Login credentials, cookies and account state would remain stored.

### Memory modes

The app should offer three modes:

* **Lowest memory:** only the selected account remains loaded
* **Balanced:** current account stays active and the previous account stays temporarily suspended
* **Fast switching:** several accounts stay loaded, with higher memory use

### Features to include

* Native account sidebar
* Add, rename and remove accounts
* Persistent independent Gmail logins
* Account colors or icons
* Manual Refresh button
* Remember the last selected account
* Remember the last Gmail URL for each account
* Open external links in the default browser
* Handle downloads and Gmail pop-up windows
* Optional account keyboard shortcuts
* Icons for Windows and pin-to-tray
* Include dark/light theme switching in settings

### Features to leave out

Since you do not need active alerts, the app should avoid:

* Background inbox polling
* Notifications
* Tray-based mail monitoring
* Keeping inactive Gmail sessions running
* Gmail API or IMAP synchronization
* Extensions
* Embedded Calendar, Chat or Meet unless manually opened

### Security approach

* Google login happens entirely inside WebView2
* The app does not need your Gmail password
* Cookies and browser data remain inside each WebView2 profile
* WebView2 uses Microsoft’s regularly updated Edge runtime
* External navigation and pop-ups should be restricted or handled carefully

### Why WebView2 was selected

Other options such as Electron, CEF and Qt WebEngine also use Chromium and are unlikely to be more memory-efficient.

WebView2 is preferable on Windows because:

* The runtime is already installed on most Windows systems
* The app does not need to package another full Chromium copy
* WPF provides a smaller native shell than Electron
* Microsoft handles browser-engine security updates

### Final conclusion

The proposed app is:

> A Windows-only WPF application with a native account sidebar, one persistent WebView2 profile per Gmail account, and only one live Gmail webpage at a time.

It should preserve the familiar Gmail interface and convenient account switching while using considerably less memory than keeping several Gmail tabs open.
