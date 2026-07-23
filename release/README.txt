Genshin Browser Portable Release

How to run:
1. Run GenshinBrowser.exe.
2. If WebView2 Runtime is not installed, the app will automatically install it.
3. Windows will request administrator permission on startup to preserve global hotkey behavior over elevated games.

Global hotkeys:
- K: play/pause current page video
- F8: switch Browsing / Floating mode

Notes:
- Login state, autofill, saved passwords, history, favorites, and browser data are stored under:
  %LocalAppData%\GenshinBrowser\
- Error logs are stored under:
  %LocalAppData%\GenshinBrowser\logs\
- This build is self-contained, so .NET does not need to be installed separately.
- Windows App SDK is also self-contained; no separate runtime install is required.
- The UI uses WinUI 3 and the browser uses the standard WinUI WebView2 control.
