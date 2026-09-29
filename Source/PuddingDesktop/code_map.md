# WinUI Shell / independent Core launcher

- `App.xaml` retains native theme brushes, rounded controls and Mica styling from desktop development B.
- `App.xaml.cs`: single-instance activation; creates the launcher coordinator. No in-process business Host.
- `MainWindow.xaml[.cs]`: trusted Web workbench, separate Agent browser, runtime controls and launcher-only settings. Business configuration remains Web.
- `Hosting/DesktopApplicationCoordinator.cs`: former launcher coordinator with WinUI dispatcher/window adaptation. Process supervisor, restart policy, token/config handling and Bridge logic compile from shared sources in `PuddingDesktop.WpfArchive`.
- `DesktopTrayIcon.cs`: native tray open/exit and close-policy adapter.
- `PuddingDesktop.csproj`: rejects Host/Runtime/Platform compiler references; both Build and Publish bundle an independently executable Core in `core/`. VS fast up-to-date skipping is disabled so Core-only edits also reach the current build. PuddingCore reference is for existing configuration contracts, not Host composition.
- `TestScripts/test-pudding-desktop-launcher.ps1`: isolated real-process smoke and external lifecycle checks; default Core path is empty to verify automatic discovery of the build's bundled Core. Explicit `-CoreExe` remains available for diagnostic overrides.

The WPF archive is a reference/test host, not loaded into the WinUI product. Browser driver behavior is shared with `PuddingBrowser.WinUI` through source links.
