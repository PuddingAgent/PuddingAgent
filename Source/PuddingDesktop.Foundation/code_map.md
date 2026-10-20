# Desktop visual Foundation

BCL-only layout and appearance preferences, with compiler-enforced absence of package/project dependencies.

- `ShellLayout.cs`: bounded layout dimensions and responsive allocation.
- `SkeletonSettingsStore.cs`: atomic appearance settings, corrupt-file preservation.
- `PanelCanvasDocument.cs`: embedded/exported panel HTML wrappers and the injected annotation/compose script.
- `PanelAnnotation.cs`: canvas payload validation and normalized model-coordinate projection.
- `PanelCraftStore.cs`: current Desktop craft records, local image copies and trash maintenance.
- `PanelImageSource.cs` / `PanelCanvasFeed.cs`: local-image URI policy and Core panel snapshot parsing.
- `StartupPanelPresenter.cs`: single mapping source for the Shell startup state panel (title/phase/3-step strip/elapsed + slow-start hint/actions/error summary/status-bar text and tone) across all 13 launch phases; `StartupErrorText.Describe` turns supervisor errors into user-facing Chinese while keeping raw text for the collapsible detail.
- `StartupPhaseActivity.cs`: parses `[StartupPhase] <phase> total=… delta=…` lines from the Core log tail into an aggregated human phase plus "latest output"; keeps the log format out of XAML and UI code.
- `Source/PuddingDesktop.FoundationTests`: independent layout, persistence, startup-panel mapping/phase-parsing and dependency boundary tests.

Role selection, native drafts, document business ownership and in-process kernel contracts were removed during master recovery. Business UI stays in Web.
