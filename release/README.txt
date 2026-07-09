Genshin Browser Portable Release

How to run (recommended):
1. Run install.ps1 (right-click → Run with PowerShell).
   It will automatically install WebView2 Runtime if needed, then launch the app.

How to run (manual):
1. Run MicrosoftEdgeWebview2Setup.exe first (silent install, no UI shown).
2. Run GenshinBrowser.exe.
3. Windows will request administrator permission on startup.

Global hotkeys:
- K: play/pause current page video
- F8: open/close control panel

Notes:
- Login state, cache, history, and favorites are stored under:
  %LocalAppData%\GenshinBrowser\
- Error logs are stored under:
  %LocalAppData%\GenshinBrowser\logs\
- This build is self-contained, so .NET does not need to be installed separately.
- WebView2 Runtime is bundled as bootstrapper and installed automatically on first run.
