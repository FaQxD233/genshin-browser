Genshin Browser Portable Release

Packages:
- GenshinBrowser-<version>-win-x64.zip (standard): uses the WebView2 Runtime installed on the system.
  If it is not installed, the app installs it automatically on first start.
- GenshinBrowser-<version>-win-x64-full.zip (full): includes a fixed-version WebView2 Runtime in the
  WebView2Runtime folder. The app uses only that copy, so system WebView2 updates do not change its behavior.
  Keep the WebView2Runtime folder next to GenshinBrowser.exe, and do not run it from a network share.

How to run:
1. Extract the zip and run GenshinBrowser.exe.
2. Windows will request administrator permission on startup.

Global hotkeys:
- K: play/pause current page video
- F8: open/close control panel

Notes:
- Login state, autofill, saved passwords, history, favorites, and browser data are stored under:
  %LocalAppData%\GenshinBrowser\
- Error logs are stored under:
  %LocalAppData%\GenshinBrowser\logs\
- This build is self-contained, so .NET does not need to be installed separately.
