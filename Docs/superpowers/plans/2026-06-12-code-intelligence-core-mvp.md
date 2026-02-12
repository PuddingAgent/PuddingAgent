# Code Intelligence Core MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan serially. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the core code intelligence path: project skeleton, SQLite code graph, LSP service integration boundary, initial project directory add/remove/list, project directory resolution and indexing, Roslyn C# indexer, and read-only query tools.

**Out of scope for this iteration:** rename preview, rename apply, built-in template exposure, broad/full-solution verification beyond focused builds and tests.

**Architecture:** `PuddingCodeIntelligence` owns index scope registration, semantic index storage, language-service boundaries, Roslyn indexing, and query services. `PuddingRuntime` exposes read-only query tools plus low-risk index state operations. A Pudding workspace is a registration and authorization context; indexed project directories may live outside the workspace directory. The next design correction is to make query tools auto-ensure index coverage so agent workflows do not require explicit folder registration.

**Tech Stack:** .NET 10, MSTest, `Microsoft.Data.Sqlite` 10.0.9, Roslyn Workspaces 5.3.0, `Microsoft.Build.Locator` 1.11.2, Pudding native tool SDK.

---

## Current Status

Updated: 2026-06-13

- [x] Task 1: Project skeleton and contracts are implemented.
- [x] Task 2: SQLite code graph and project registry store are implemented.
- [x] Task 3: Project registration, workspace resolution, query service, LSP boundary, and DI are implemented.
- [x] Task 4: Roslyn C# indexer is implemented.
- [x] Task 5: Runtime initial index registration and read-only query tools are implemented.
- [ ] Task 6: Focused verification and documentation closes this slice.
- [ ] Follow-up design correction: replace explicit project-registration workflow with automatic index coverage, background indexing, watcher-driven refresh, and repair tools.

Latest focused evidence:

- `dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --logger "console;verbosity=minimal"`: PASS, 28/28 (22 previous + 6 new indexer tests), with existing MSTEST0001/MSTEST0037 analyzer warnings.
- Task 4 implementation details:
  - `RoslynWorkspaceBootstrapper`: registers MSBuild once, creates `MSBuildWorkspace`, tries `.sln`/`.slnx` then falls back to `.csproj` enumeration; uses `RegisterWorkspaceFailedHandler` for diagnostics.
  - `RoslynSymbolId`: uses Roslyn `GetDocumentationCommentId()` with fallback composite id.
  - `RoslynCSharpIndexer`: extracts namespaces, types, methods, constructors, properties, fields, events, parameters; Contains relations for hierarchy; Calls relations for invocations/object creation/member access; References with source location.
  - `IndexCompilationAsync` (internal): bypasses MSBuild for test contexts via `AdhocWorkspace`.
  - DI: `ICodeIndexer` registered as `RoslynCSharpIndexer` in `AddPuddingCodeIntelligence`.
  - Guardrails respected: no `bin`/`obj`/`.git`/`.pudding-code`/`node_modules` indexing; no source writes; no rename/format/code actions. MSBuild Locator kept as `ExcludeAssets="runtime"` since tests bypass it.
- Task 5 implementation details:
  - `PuddingRuntime.csproj`: added `ProjectReference` to `PuddingCodeIntelligence`.
  - `DependencyInjection.cs`: calls `AddPuddingCodeIntelligence()` and registers `ICodeIndexStore` via `SqliteCodeIndexStore` at `PuddingDataPaths.DatabasesRoot/code-index/code_index.db`.
  - 9 tools in `Tools/BuiltIns/CodeIntelligence/`: `CodeProjectManagementTools.cs` (add/remove/list) and `CodeQueryTools.cs` (index_status, symbol_search, explore, callers, callees, impact).
  - Tools use `PuddingToolBase<TArgs>` + `[Tool]` attribute with optional constructor dependencies for graceful degradation when CodeIntelligence services are not registered.
  - `code_project_add` resolves arbitrary local paths with `Path.GetFullPath`, does not require the path under the workspace directory.
  - `code_project_add` accepts optional `index` boolean to trigger `ICodeIndexer.IndexWorkspaceAsync` after registration.
  - Build: 0 errors, pre-existing warnings only.
  - Test: `PuddingToolInfrastructureTests` 104/104 pass including auto-discovery of new tools.



Key design updates from implementation:

- `CodeProjectAddRequest.ProjectId` is optional. If omitted, `CodeProjectRegistry` generates a stable id from `WorkspaceId` and normalized project root.
- Project roots are normalized with `Path.GetFullPath`, but are not required to live under the Codex or Pudding workspace directory.
- Path identity uses a shared policy: Windows paths compare case-insensitively; non-Windows paths compare case-sensitively.
- Resolver ignores `.git`, `.pudding-code`, `bin`, `obj`, and `node_modules`, and does not recurse into reparse points/symbolic links.
- `AddPuddingCodeIntelligence` registers registry, resolver, query service, and no-op LSP only. `ICodeIndexStore` remains a runtime composition concern because the SQLite path depends on `PuddingDataPaths`.

Design correction accepted after Task 5 QA:

- Agent-facing code tools should not require explicit folder registration before use. Query tools should resolve and ensure index coverage automatically.
- If no registered `project_index` / index scope matches the query context, Pudding should run project root detection from the file/current directory upward before creating an auto scope.
- `code_project_add` and `code_project_remove` are poor public names because they operate on index coverage, not source projects. They should be renamed or replaced with index-scope semantics.
- Add/remove/forget index coverage is low risk: it writes rebuildable Pudding index state only, does not modify source files, and does not change the runtime environment.
- Index creation and refresh should move to background scheduling. Tool calls can return status such as `indexing_scheduled`, `not_ready`, `stale`, or `refreshing`.

## Execution Scope

Implement only these components:

- `Source/PuddingCodeIntelligence`
- `Source/PuddingCodeIntelligenceTests`
- Runtime DI reference to code intelligence
- Runtime read-only and initial index registration tools:
  - `code_project_add`
  - `code_project_remove`
  - `code_project_list`
  - `code_index_status`
  - `code_symbol_search`
  - `code_explore`
  - `code_callers`
  - `code_callees`
  - `code_impact`

Do not modify `Source/PuddingAgent/BuiltInAgentTemplates.cs` in this iteration.
Do not implement rename, format, organize imports, code action, extract method, or source-code write tools.

Next-iteration public tool direction:

- Keep query tools agent-facing: `code_index_status`, `code_symbol_search`, `code_explore`, `code_callers`, `code_callees`, `code_impact`.
- Replace explicit project-registration workflow with index-scope operations: `code_index_list_scopes`, `code_index_refresh`, `code_index_rebuild`, `code_index_forget_scope`.
- Query tools should accept optional `scope_path`, `file_path`, or `project_id`; when omitted, Pudding resolves scope from context and schedules indexing if needed.
- Scope resolution should first search existing `project_index` / index scopes. If none match, run project root detection upward from the query context and ensure the detected root.
- `code_project_add` / `code_project_remove` should not remain the normal public workflow name. If retained for compatibility, descriptions must say they affect only Pudding index state and are low-risk.

## Task 1: Project Skeleton And Contracts

**Files:**
- Create: `Source/PuddingCodeIntelligence/PuddingCodeIntelligence.csproj`
- Create: `Source/PuddingCodeIntelligence/Contracts/CodeIntelligenceModels.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeProjectRegistry.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeWorkspaceResolver.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeIndexer.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeIndexStore.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeQueryService.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ILanguageServerService.cs`
- Create: `Source/PuddingCodeIntelligenceTests/PuddingCodeIntelligenceTests.csproj`
- Create: `Source/PuddingCodeIntelligenceTests/Contracts/CodeIntelligenceContractTests.cs`
- Modify: `PuddingAgentNetwork.slnx`

- [x] Add the new library with package references:
  - `Microsoft.Build.Locator` 1.11.2
  - `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.3.0
  - `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.3.0
  - `Microsoft.Data.Sqlite` 10.0.9
  - `Microsoft.Extensions.DependencyInjection.Abstractions` 9.0.0
  - `Microsoft.Extensions.Logging.Abstractions` 9.0.0
- [x] Add contracts for code projects, files, symbols, relations, references, index status, symbol search, project registry, workspace resolver, indexer, store, query service, and LSP service.
- [x] Add a dedicated MSTest project and contract tests.
- [x] Add both projects to `PuddingAgentNetwork.slnx`.
- [x] Run `dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --logger "console;verbosity=minimal"`.

## Task 2: SQLite Code Graph And Project Registry Store

**Files:**
- Create: `Source/PuddingCodeIntelligence/Storage/SqliteCodeIndexStore.cs`
- Create: `Source/PuddingCodeIntelligenceTests/Storage/SqliteCodeIndexStoreTests.cs`

- [x] Implement tables:
  - `CodeProjects`
  - `CodeFiles`
  - `CodeSymbols`
  - `CodeRelations`
  - `CodeReferences`
  - `CodeIndexRuns`
- [x] Implement project upsert/remove/list/get.
- [x] Implement file index replacement that clears stale symbols/relations/references for the replaced file.
- [x] Implement symbol search by workspace and optional project.
- [x] Implement relation reads for callers/callees/impact.
- [x] Add tests covering project add/remove, file replacement, symbol search, and relation traversal.
- [x] Run `dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~SqliteCodeIndexStoreTests" --logger "console;verbosity=minimal"`.

## Task 3: Project Registration, Resolution, LSP Boundary, And DI

**Files:**
- Create: `Source/PuddingCodeIntelligence/Services/CodeProjectRegistry.cs`
- Create: `Source/PuddingCodeIntelligence/Services/DefaultCodeWorkspaceResolver.cs`
- Create: `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs`
- Create: `Source/PuddingCodeIntelligence/Lsp/NoOpLanguageServerService.cs`
- Create: `Source/PuddingCodeIntelligence/DependencyInjection.cs`
- Create: `Source/PuddingCodeIntelligenceTests/Services/CodeProjectRegistryTests.cs`
- Create: `Source/PuddingCodeIntelligenceTests/Services/DefaultCodeWorkspaceResolverTests.cs`
- Create: `Source/PuddingCodeIntelligenceTests/Lsp/NoOpLanguageServerServiceTests.cs`

- [x] `CodeProjectRegistry` must add/remove/list project directories without assuming they live under the Pudding workspace directory.
- [x] `DefaultCodeWorkspaceResolver` must resolve `.slnx`, `.sln`, and `.csproj` under the registered project root while ignoring `bin`, `obj`, `.git`, `.pudding-code`, and `node_modules`.
- [x] `NoOpLanguageServerService` must expose the LSP integration boundary and return explicit unsupported responses until concrete language servers are added.
- [x] `CodeQueryService` must delegate to `ICodeIndexStore` for search/explore/callers/callees/impact.
- [x] `AddPuddingCodeIntelligence` must register core services.
- [x] Add focused tests for registry, resolver, query service, and LSP no-op behavior.
- [x] Run focused tests for these classes.

## Task 4: Roslyn C# Indexer

**Files:**
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynWorkspaceBootstrapper.cs`
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynSymbolId.cs`
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynCSharpIndexer.cs`
- Create: `Source/PuddingCodeIntelligenceTests/CSharp/RoslynCSharpIndexerTests.cs`
- Modify: `Source/PuddingCodeIntelligence/DependencyInjection.cs`

- [x] Add tests first with a temporary C# project containing one caller method, one callee method, and at least one nested namespace/type relationship.
- [x] Add `RoslynWorkspaceBootstrapper` that registers MSBuild once, creates `MSBuildWorkspace`, opens `.sln`/`.slnx` when supported, and falls back to explicit `.csproj` enumeration from `CodeWorkspaceDescriptor.ProjectFilePaths`.
- [x] Add `RoslynSymbolId` using Roslyn documentation comment id or a stable fallback from symbol kind, containing symbol, name, and source span.
- [x] Add `RoslynCSharpIndexer` implementing `ICodeIndexer`.
- [x] Extract declarations for namespaces, types, methods, constructors, properties, fields, events, locals, and parameters where practical.
- [x] Extract `Contains` relations for symbol hierarchy.
- [x] Extract `Calls` relations for invocation/object creation/member access cases where Roslyn resolves both source and target symbols inside the indexed project.
- [x] Persist files, symbols, relations, and references through `ICodeIndexStore`.
- [x] Register `ICodeIndexer` as `RoslynCSharpIndexer` in `AddPuddingCodeIntelligence`; keep `ICodeIndexStore` external.
- [x] Run `dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~RoslynCSharpIndexerTests" --logger "console;verbosity=minimal"`.

Task 4 guardrails:

- Do not index `bin`, `obj`, `.git`, `.pudding-code`, `node_modules`, generated intermediate output, or symlink/junction loops.
- Do not write source files.
- Do not implement rename, format, organize imports, or code actions.
- If `MSBuildWorkspace` cannot load `.slnx` in the local SDK/Roslyn combination, record a warning and index discovered `.csproj` files instead of failing the whole project.

## Task 5: Runtime Read-Only And Initial Index Registration Tools

**Files:**
- Modify: `Source/PuddingRuntime/PuddingRuntime.csproj`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Code/CodeIntelligenceTools.cs`
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [x] Add project reference from Runtime to `PuddingCodeIntelligence`.
- [x] Register `AddPuddingCodeIntelligence`.
- [x] Register `ICodeIndexStore` under `PuddingDataPaths.DatabasesRoot/code-index`.
- [x] Ensure `code_project_add` resolves arbitrary local project paths without assuming the path is under the Pudding workspace directory.
- [x] Ensure `code_project_add` can optionally trigger `ICodeIndexer.IndexWorkspaceAsync` after registration, while keeping indexing implementation transparent to the agent.
- [x] Add tools:
  - `code_project_add`
  - `code_project_remove`
  - `code_project_list`
  - `code_index_status`
  - `code_symbol_search`
  - `code_explore`
  - `code_callers`
  - `code_callees`
  - `code_impact`
- [x] Tool behavior:
  - Project tools write only Pudding registry/index DB state, never source files.
  - Query tools are `ToolCategory.Query`, `ToolPermissionLevel.Low`, `ReadOnly | ConcurrencySafe`.
  - `code_project_list` is read-only.
  - `code_project_add` is a low-risk index state operation and not source-destructive.
  - `code_project_remove` is a low-risk index state operation. It may delete local index records, but source files and the runtime environment are untouched.
- [x] Add descriptor and basic execution tests.
- [x] Run `dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests" --logger "console;verbosity=minimal"`.

## Follow-up Design Correction: Auto Index Coverage

This section records the next implementation slice. It is not part of the completed Core MVP acceptance unless explicitly pulled into a follow-up task.

**Code-level changes:**

- [ ] Add `ICodeIndexScopeResolver` to resolve `scope_path`, `file_path`, `project_id`, current working directory, and workspace context into a canonical index scope.
- [ ] Add `ICodeProjectRootDetector` used by the scope resolver when no registered scope matches.
- [ ] Implement upward-only project root detection:
  - Start from `file_path` parent directory, explicit `scope_path`, current working directory, or recent code context.
  - Check the start directory first, then each parent directory.
  - Stop on `.git` directory/file and return that directory as the project root.
  - Stop on known project files and return that directory as the project root.
  - Do not recursively search child directories during detection.
- [ ] Include initial project markers:
  - .NET/C/C++: `.sln`, `.slnx`, `*.csproj`, `*.fsproj`, `*.vbproj`, `*.vcxproj`, `CMakeLists.txt`, `Makefile`.
  - JS/TS/frontend: `package.json`, `pnpm-workspace.yaml`, `nx.json`, `turbo.json`, `vite.config.*`, `next.config.*`, `angular.json`, `vue.config.*`, `svelte.config.*`.
  - Python: `pyproject.toml`, `setup.py`, `setup.cfg`, `requirements.txt`, `Pipfile`, `poetry.lock`.
  - Go/Java/JVM: `go.mod`, `pom.xml`, `build.gradle`, `build.gradle.kts`, `settings.gradle`, `settings.gradle.kts`.
  - Other/IDE: `Cargo.toml`, `composer.json`, `Gemfile`, `mix.exs`, `pubspec.yaml`, `deno.json`, `deno.jsonc`, `*.xcodeproj`, `*.xcworkspace`, `*.iml`.
- [ ] Add `ICodeIndexScopeRegistry` or evolve `ICodeProjectRegistry` with scope source/state fields: `ScopeSource = Auto | Manual | Pinned`, `ScopeState = Active | Covered | Removed | Failed`.
- [ ] Implement idempotent ensure for the same canonical folder.
- [ ] Implement parent/child overlap rules:
  - Existing parent + new child: child is covered by parent by default.
  - Existing auto child + new parent: parent becomes active; child is marked covered/merged.
  - Future pinned child + parent: parent excludes the pinned child subtree.
- [ ] Add `ICodeIndexScheduler` and `CodeIndexWorkerService` so ensure/register only enqueues indexing work.
- [ ] Update query tools to call the scope resolver before querying. If no ready index exists, return a structured status and enqueue background indexing instead of blocking.
- [ ] Add stale-while-revalidate metadata for query responses served from old indexes.
- [ ] Add `CodeIndexWatcherService` using `FileSystemWatcher` on Windows with debouncing, overflow handling, and fallback full reconcile.
- [ ] Add periodic/adaptive reconcile based on project size, index age, recent file changes, failure count, and queue pressure.
- [ ] Replace or supplement `code_project_add/remove/list` with `code_index_list_scopes`, `code_index_refresh`, `code_index_rebuild`, and `code_index_forget_scope`.
- [ ] Mark index-scope add/remove/forget/rebuild tools as low risk because they modify rebuildable Pudding index state only.
- [ ] Add tests for project root detection via `.git`, `.sln/.slnx`, `package.json`, `pyproject.toml`, `go.mod`, `pom.xml`, idempotent same-folder ensure, parent/child overlap, low-risk tool descriptors, background scheduling, and watcher overflow -> stale/full-reconcile behavior.

## Task 6: Focused Verification And Documentation

**Files:**
- Modify: `Docs/Memory/2026-06-12.md`
- Modify: `Docs/context.md`

- [ ] Run focused tests:
  - `dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --logger "console;verbosity=minimal"`
  - `dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests" --logger "console;verbosity=minimal"`
- [ ] Run focused builds:
  - `dotnet build Source\PuddingCodeIntelligence\PuddingCodeIntelligence.csproj`
  - `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj`
- [ ] Run `git diff --check`.
- [ ] Update docs with implemented scope, verification commands, and known gaps.

## Acceptance Criteria

Current Core MVP:

- A workspace can register a project directory outside the workspace path.
- Registered project directories can be listed and removed without deleting source files.
- A registered C# project can be indexed into SQLite.
- Symbol search and call relation queries read from the semantic index.
- Runtime exposes initial index registration and read-only query tools.
- LSP integration has a real service boundary, even if concrete language server operations are unsupported in this iteration.
- No rename/template/source-write tools are implemented in this iteration.

Follow-up auto-coverage acceptance:

- Agent can call code query tools without explicitly registering a folder first.
- If no registered scope matches, Pudding detects the project root by walking upward from the query context and stopping at `.git` or a known project file.
- Repeated ensure/register for the same canonical folder is idempotent.
- Parent/child scope overlap does not double-index the same file.
- Indexing is scheduled in the background and does not block normal tool calls.
- Query tools return clear `indexing_scheduled`, `not_ready`, `stale`, or `refreshing` status when the index is missing or stale.
- Rebuild/refresh/forget scope tools exist, are low-risk, and clearly state that source files are not modified or deleted.
