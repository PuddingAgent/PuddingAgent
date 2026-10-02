# WinUI Shell / independent Core launcher

- `App.xaml` retains native theme brushes, rounded controls and Mica styling from desktop development B.
- `App.xaml.cs`: single-instance activation; creates the launcher coordinator. No in-process business Host.
- `MainWindow.xaml[.cs]`: trusted Web workbench, separate Agent browser, runtime controls and launcher-only settings. Business configuration remains Web.
- Runtime Center navigation lives beside Workbench and Browser. Colored status, startup duration/time, uptime, Core CPU and memory cards sit above a stretchable log viewport with optional follow mode. Metrics sample only while this page and window are visible; unchanged logs are not reassigned.
- Memory card reports the **private working set** (Task Manager's 内存 column), not `Process.WorkingSet64` (total working set, which also counts shared image/mapped/JIT pages and reads 30%–120% higher). The subtitle always discloses the counter used and the concurrent total working set; see `Docs/14_reports/Core内存口径与占用归因-2026-10-02.md`.
- `PuddingDesktop.WpfArchive/Runtime/CoreProcessMetricsSampler.cs`: shared BCL process sampling and monotonic CPU deltas; unavailable samples are empty, PID/start-time changes reset the CPU baseline. No business Host or database access.
- `PuddingDesktop.WpfArchive/Runtime/ProcessMemoryReader.cs`: private working set via `NtQuerySystemInformation(SystemProcessInformation)` → `WorkingSetPrivateSize` (same source as Task Manager); returns null off x64 or when unreadable so callers fall back explicitly.
- `PuddingDesktop.WpfArchive/Runtime/CoreMemoryDisplay.cs`: pure mapping from `CoreProcessMetrics` to the memory card's value/subtitle, including the shared-page fallback wording. Unit-tested without a UI.
- `Hosting/DesktopApplicationCoordinator.cs`: former launcher coordinator with WinUI dispatcher/window adaptation. Process supervisor, restart policy, token/config handling and Bridge logic compile from shared sources in `PuddingDesktop.WpfArchive`.
- `Assets/Pudding.ico`: multi-size Web brand avatar, embedded in EXE and copied to build/publish output; regenerate with `TestScripts/update-pudding-desktop-icon.ps1`.
- `DesktopIcon.cs`: shared icon path and private Win32 tray HICON loading; `MainWindow` sets the AppWindow icon for the taskbar/title bar.
- `DesktopTrayIcon.cs`: native tray open/exit and close-policy adapter; owns and releases its Pudding icon handle.
- Tool DropDownButton styles use the control's default template without the nonexistent `DefaultDropDownButtonStyle` resource key.
- `PuddingDesktop.csproj`: rejects Host/Runtime/Platform compiler references; both Build and Publish bundle an independently executable Core in `core/`. VS fast up-to-date skipping is disabled so Core-only edits also reach the current build. PuddingCore reference is for existing configuration contracts, not Host composition.
- `TestScripts/test-pudding-desktop-launcher.ps1`: isolated real-process smoke and external lifecycle checks; default Core path is empty to verify automatic discovery of the build's bundled Core. Explicit `-CoreExe` remains available for diagnostic overrides.

The WPF archive is a reference/test host, not loaded into the WinUI product. Browser driver behavior is shared with `PuddingBrowser.WinUI` through source links.
