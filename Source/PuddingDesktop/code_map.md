# WinUI Shell / independent Core launcher
> 源指纹: Assets/**=1-03d89cb968fa, Hosting/**=2-875ce62093f7, *.cs=7-4ae9cc102ef8, *.csproj=1-b7bc4cc878eb, *.manifest=1-c50e9539699b, *.xaml=2-ab5498f2451a · 条目数: 0 · 最近整理: 2026-10-22

- `App.xaml` retains native theme brushes, rounded controls and Mica styling from desktop development B.
- `App.xaml.cs`: single-instance activation and launcher composition; wires UI/AppDomain/task exceptions, process-exit callback, five-second durable scene capture and the independent exit observer.
- `Hosting/DesktopApplicationCoordinator.Guardian.cs`: generates token-free guardian settings, starts/adopts the independent process, checks installation identity and routes bootstrap commands. Authenticated UI-dispatch health and graceful exit remain in the shared HTTP endpoint. Successful adoption disables competing in-process automatic recovery.
- `PuddingDesktop.csproj`: serially publishes Core, observer and `guardian/`; honors isolated artifacts paths and skips guardian self-replacement builds. `GuardianTestAssembly.cs` grants only the WinUI integration test access to existing test hooks.
- `../PuddingDesktop.Guardian/code_map.md`: independent build/deployment supervisor; [operation contract](../../Docs/13_runbooks/Desktop-Guardian.md).
- `../PuddingDesktop.Diagnostics/code_map.md`: bounded, redacted per-run evidence and next-start recovery; no UI or Core dependencies.
- `../PuddingDesktop.ExitObserver/code_map.md`: separate observer in `observer/`; preserves actual exit code even when managed callbacks do not run.
- `MainWindow.xaml[.cs]`: trusted Web workbench, separate Agent browser, runtime controls and launcher-only settings. Business configuration remains Web.
- Tool home cards (`*CardSubtitle` blocks + `ApplyToolAvailabilityLabels`): chip label, subtitle and
  clickability are all pasted from `PuddingDesktop.Foundation.ToolAvailabilityCatalog`, and the 「+」menu's
  terminal item shares the same decision — a Deferred capability is disabled in both places instead of opening a
  placeholder tab. The stale `DeferredNotice(Panel)` branch (said 「交互面板尚未接入」 while the card said 可用)
  was removed; `DeferredNotice` now covers only genuinely unwired kinds.
- `MainWindow.xaml.cs` panel methods: current canvas polling, annotation commands, composer forwarding and native craft-library wiring.
- `PanelImageBridge.cs`: serves allowed local image resources to panel WebView2 instances.
- [Agent-driven panel ADR-099](../../Docs/07_architecture/113ADR-099交互面板编辑状态与制成品归属ADR.md): proposed interactive HTML/JS runtime, bidirectional SDK events, recoverable state and Core asset ownership.
- Startup state panel (`LoadingPanel` in `MainWindow.xaml`): replaces the old static "等待 Core 就绪" copy with a state-driven panel for starting / slow start (≥15 s, latest `[StartupPhase]` output) / loading the Web workbench / failure. All
  copy, visibility and status-bar text come from `PuddingDesktop.Foundation.StartupPanelPresenter`; steps/colors are pre-declared XAML visuals switched by visibility so Light/Dark/HighContrast re-resolve. The Shell only maps
  `DesktopStartupState` → presenter input, drives a 200 ms stopwatch timer, and routes primary actions (`StartCore` / `CancelStartup` via `StopCoreAsync`→`_startCts` / `Retry` / `OpenSettings`). `StartupPhaseActivity` is the only place
  that understands the `[StartupPhase]` log format. A 20 s WebView2 watchdog downgrades a hung workbench load to `WorkbenchFailed`; retries reload the WebView through `_webGate` instead of closing it directly. The Debug smoke captures
  `smoke.startup.png` before `WorkbenchReady` and asserts the panel/ring state.
- Runtime Center navigation lives beside Workbench and Browser. Colored status, startup duration/time, uptime, Core CPU and memory cards sit above a stretchable log viewport with optional follow mode. Metrics sample only while this page and window are visible; unchanged logs are not reassigned.
- Memory card reports the **private working set** (Task Manager's 内存 column), not `Process.WorkingSet64` (total working set, which also counts shared image/mapped/JIT pages and reads 30%–120% higher). The subtitle always discloses the counter used and the concurrent total working set; see
 `Docs/14_reports/Core内存口径与占用归因-2026-10-02.md`.
- `PuddingDesktop.WpfArchive/Runtime/CoreProcessMetricsSampler.cs`: shared BCL process sampling and monotonic CPU deltas; unavailable samples are empty, PID/start-time changes reset the CPU baseline. No business Host or database access.
- `PuddingDesktop.WpfArchive/Runtime/ProcessMemoryReader.cs`: private working set via `NtQuerySystemInformation(SystemProcessInformation)` → `WorkingSetPrivateSize` (same source as Task Manager); returns null off x64 or when unreadable so callers fall back explicitly.
- `PuddingDesktop.WpfArchive/Runtime/CoreMemoryDisplay.cs`: pure mapping from `CoreProcessMetrics` to the memory card's value/subtitle, including the shared-page fallback wording. Unit-tested without a UI.
- `Hosting/DesktopApplicationCoordinator.cs`: former launcher coordinator with WinUI dispatcher/window adaptation. Process supervisor, restart policy, token/config handling and Bridge logic compile from shared sources in `PuddingDesktop.WpfArchive`.
- `Assets/Pudding.ico`: multi-size Web brand avatar, embedded in EXE and copied to build/publish output; regenerate with `TestScripts/update-pudding-desktop-icon.ps1`.
- `DesktopIcon.cs`: shared icon path and private Win32 tray HICON loading; `MainWindow` sets the AppWindow icon for the taskbar/title bar.
- `DesktopTrayIcon.cs`: native tray open/exit and close-policy adapter; records Windows session-ending message flags separately from user exit, and owns its icon handle.
- Tool DropDownButton styles use the control's default template without the nonexistent `DefaultDropDownButtonStyle` resource key.
- `PuddingDesktop.csproj`: rejects Host/Runtime/Platform compiler references; both Build and Publish bundle an independently executable Core in `core/`. VS fast up-to-date skipping is disabled so Core-only edits also reach the current build. PuddingCore reference is for existing
 configuration contracts, not Host composition.
- `TestScripts/test-pudding-desktop-launcher.ps1`: isolated real-process smoke and external lifecycle checks; default Core path is empty to verify automatic discovery of the build's bundled Core. Explicit `-CoreExe` remains available for diagnostic overrides; `-CapabilityChannelEnabled`
 enables the named-pipe channel in both isolated configurations and verifies preflight plus redirected Chinese log decoding in the Debug smoke.

The WPF archive is a reference/test host, not loaded into the WinUI product. Browser driver behavior is shared with `PuddingBrowser.WinUI` through source links.

`Hosting/DesktopApplicationCoordinator.Capability.cs` connects workspace target snapshots through `BrowserWorkspaceTargetBridge.ObserveAgentTarget`; empty or repeated UI state is not a user revocation command. Product surface management
requires the workspace's `IBrowserWorkspaceLifecycle`; context events and each tab's owning ContextId drive registration and cleanup. Explicit control actions use the lifecycle command ports.

- `../../TestScripts/build-browser-companion.ps1`: serial Desktop + bundled Core build in a dedicated output directory; records per-file hashes, process identities and protocol revisions in `browser-companion.json`; `-VerifyOnly` validates
  copied packages, `-AllowDirty` marks development repairs explicitly.
