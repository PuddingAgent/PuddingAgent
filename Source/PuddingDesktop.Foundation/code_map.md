# Desktop visual Foundation
> 源指纹: *.cs=18-ffc6f552c437, *.csproj=1-92f91f3fbd34 · 条目数: 0 · 最近整理: 2026-10-22

BCL-only layout and appearance preferences, with compiler-enforced absence of package/project dependencies.

- `ShellLayout.cs`: bounded layout dimensions and responsive allocation.
- `Design/DesignTokens.cs`: the Shell's only font-size / corner-radius vocabulary - 6 tiers (11/12/15/18/24/28)
  and 2 radii (8/12), plus `MapFontSize` / `MapCornerRadius` for migrating a literal to its tier.
  Tiers are named by ROLE (`FontSizeBody`), never by value, so a later revalue touches one file.
  This class is the numeric source of truth; `MainWindow.xaml` carries a copy of the values as resources,
  and a contract test fails the moment the two disagree.
- `Design/XamlLiteralLint.cs`: text-level gate (`PDT-TOKEN-001/002/003`) flagging raw `FontSize`,
  `CornerRadius` and `#rrggbb` on background/foreground/brush attributes - in BOTH shapes: on the element
  (`FontSize="13"`) and inside a style (`<Setter Property="FontSize" Value="13"/>`, any attribute order).
  `{StaticResource ...}` passes and a brush DEFINITION site (`<SolidColorBrush Color="#...">`) stays legal,
  otherwise the token file could not pass itself.
- `Design/ThemeResourceAudit.cs`: structural gate for `{ThemeResource ...}`. WinUI resolves theme keys at
  RUNTIME, so a misspelt key compiles and only fails on launch; this audit cross-checks every reference against
  the keys declared in the same file, the keys declared by `App.xaml`, and an explicit `PlatformResources`
  allowlist of WinUI built-ins (11 keys, ordinal) — an unknown key is reported with its 1-based line.
  `FindBrushKeySetDrift` compares the `Default` vs `Light` brush key SETS only: the same token legitimately has
  different values per theme, so comparing values is wrong (the first draft did and was red on 8 real keys).
  `HighContrast` is intentionally excluded — a missing key falls back to `Default` by WinUI rule.
- `SkeletonSettingsStore.cs`: atomic appearance settings, corrupt-file preservation.
- `PanelCanvasDocument.cs`: embedded/exported panel HTML wrappers plus the injected annotation script:
  five primitive tools (box/point/pen/arrow/text), per-item delete, undo, `compose()` (burn ink into pixels),
  `take()` (always emits normalized 0-1000 via C# `PanelAnnotation`) and `load()` (redraw saved marks on reopen); per-tool cursors, in-page shortcuts (V/P/B/A/T,
  Ctrl+Z, Del, Esc - WebView2 keys never reach XAML accelerators) and a `state()` snapshot the host
  polls to correct its toolbar selection without any message bridge.
- `PanelCanvasState.cs`: fail-closed parse of the page's `state()` snapshot (tool/active/count/canUndo).
  An unknown tool name is accepted (the page may lead the host) but a blank one is rejected - a wrong
  guess would silently light up the wrong toolbar button.
- `PanelAnnotation.cs`: canvas payload validation, normalized model-coordinate projection, and the five
  primitive kinds including scribble ink trails (arrow keeps start/end order — sorting would flip its head).
- `PanelCanvasRestore.cs`: serializes saved annotations into a `puddingAnnotation.load([...])` payload wrapped
  in a self-retrying inline `<script>` (injection order is not guaranteed, so it waits for the layer itself).
  `BuildPayload` is the only "annotations -> JSON" implementation: craft records embed its output verbatim.
- `PanelCraftStore.cs`: craft records (save/list/rename/continue-editing/delete to `_trash`), strict id and
  file-name validation before any move, title normalization shared by save and rename; also `PanelCraftEditPanel`
  (edit-canvas id <-> craft id, so lineage needs no extra tab state) and `PanelCraftLineage` (version depth,
  cycle-guarded, tolerates a deleted ancestor). Craft `annotations` reuse `PanelCanvasRestore.BuildPayload`
  verbatim instead of a second serializer, so save and reopen cannot disagree on field names or kinds.
- `PanelMarkRetention.cs`: burn-in JPEG retention — `.used` sidecar marks a mark that was inserted into a
  conversation (never pruned), used marks do not consume the keep budget, orphan markers cleaned up.
- `PanelImageSource.cs` / `PanelCanvasFeed.cs`: local-image URI policy and Core panel snapshot parsing.
- `PanelAnnotationSnapshotWriter.cs`: Desktop→Core annotation handoff; one envelope per panel at
  `{DataRoot}/panels/panel-{id}.annotations.json` (`schemaVersion=1`, camelCase `payload`, atomic `.tmp` + move).
  `TryPublish` returns `false` + reason instead of throwing (it runs on the 2s UI poll timer), and refuses an empty payload
  so a transient blank read can never erase the previous drawing; callers dedupe on payload text so only real edits hit disk.
- `ToolAvailabilityCatalog.cs`: the single truth for tool-home cards. `For(kind)` decides availability and
  `CardLabel` / `CardSubtitle` / `IsEntryEnabled` all derive from it, so a card can never claim 「可用」 while its
  copy still says 等待接入, and an unwired entry is disabled instead of opening an empty tab. 制成品 stays Ready
  because `PanelCraftStore` really backs it; only Terminal remains Deferred. Unit-tested across every enum member
  (invariant + enabled derivation), see `ToolAvailabilityCatalogTests`.
- `StartupPanelPresenter.cs`: single mapping source for the Shell startup state panel (title/phase/3-step strip/elapsed + slow-start hint/actions/error summary/status-bar text and tone) across all 13 launch phases;
  `StartupErrorText.Describe` turns supervisor errors into user-facing Chinese while keeping raw text for the collapsible detail.
- `StartupPhaseActivity.cs`: parses `[StartupPhase] <phase> total=… delta=…` lines from the Core log tail into an aggregated human phase plus "latest output"; keeps the log format out of XAML and UI code.
- `Source/PuddingDesktop.FoundationTests`: independent layout, persistence, startup-panel mapping/phase-parsing,
  and dependency boundary tests; plus annotation normalization, craft store (save/rename/lineage/trash),
  burn-in retention, canvas-restore payload and annotation-handoff writer tests, plus the design-token contract
  (tier coverage of every literal seen in MainWindow.xaml, lint red cases, a frozen baseline of raw literals in
  the real desktop XAML, XAML-token/model value parity, the documented residual delta, and the resolve check that
  every `{StaticResource FontSize*|Radius*}` names a declared key), plus the theme-resource gate
  (`{ThemeResource}` resolvability against file ∪ App.xaml ∪ platform allowlist, and Default/Light brush
  key-set parity for both shell XAML files) (513 cases as of 2026-10-22).

Role selection, native drafts, document business ownership and in-process kernel contracts were removed during master recovery. Business UI stays in Web.
