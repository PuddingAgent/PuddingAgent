# Code Intelligence MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first C#-first code intelligence slice for PuddingAgent: multi-project directory registration, semantic indexing, SQLite-backed symbol graph queries, LSP service integration scaffolding, Roslyn C# indexing, and read-only code tools.

**Architecture:** Add a new `PuddingCodeIntelligence` library that owns a workspace-keyed registry of arbitrary local project directories, SQLite code graph storage, LSP service abstractions, Roslyn C# indexing, and semantic query services. `PuddingRuntime` exposes native Pudding tools and DI registration only; agents add/remove/list project directories through tools, while indexing and retrieval stay transparent. A project directory may live outside the Pudding workspace directory; workspace is the registration and authorization context, not the filesystem boundary. C# semantic facts come from Roslyn; LSP integration starts as a service boundary for future cross-language capabilities.

**Tech Stack:** .NET 10, MSTest, `Microsoft.Data.Sqlite` 10.0.9, Roslyn Workspaces 5.3.0, `Microsoft.Build.Locator` 1.11.2, Pudding native tool SDK.

---

## File Structure

- Create: `Source/PuddingCodeIntelligence/PuddingCodeIntelligence.csproj`
  - Owns the implementation library and package references.
- Create: `Source/PuddingCodeIntelligence/Contracts/CodeIntelligenceModels.cs`
  - Shared DTOs and enums for projects, files, symbols, relations, references, index status, query requests, and IDE action requests.
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeProjectRegistry.cs`
  - Add/remove/list/status contract for project directories registered under a Pudding workspace.
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeWorkspaceResolver.cs`
  - Resolves registered code projects into solution/project descriptors.
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeIndexer.cs`
  - Index and sync entry point.
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeIndexStore.cs`
  - SQLite-independent storage contract.
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeQueryService.cs`
  - Symbol search, node detail, callers, callees, impact queries.
- Create: `Source/PuddingCodeIntelligence/Contracts/ILanguageServerService.cs`
  - Language-server integration contract for future cross-language semantic providers.
- Create: `Source/PuddingCodeIntelligence/Storage/SqliteCodeIndexStore.cs`
  - SQLite schema creation, project registry writes, replacement writes, and read queries.
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynWorkspaceBootstrapper.cs`
  - Process-wide MSBuild locator registration guard.
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynSymbolId.cs`
  - Stable C# symbol id generation.
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynCSharpIndexer.cs`
  - C# project/solution loader, declaration indexing, reference indexing, and call relation extraction.
- Create: `Source/PuddingCodeIntelligence/Lsp/NoOpLanguageServerService.cs`
  - LSP service integration scaffold with explicit unsupported responses until concrete language servers are wired.
- Create: `Source/PuddingCodeIntelligence/Services/DefaultCodeWorkspaceResolver.cs`
  - Registered project root validation and `.sln`/`.csproj` discovery.
- Create: `Source/PuddingCodeIntelligence/Services/CodeProjectRegistry.cs`
  - Workspace-scoped project directory add/remove/list implementation.
- Create: `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs`
  - Thin query layer over `ICodeIndexStore`.
- Create: `Source/PuddingCodeIntelligence/DependencyInjection.cs`
  - `AddPuddingCodeIntelligence(...)` service registration.
- Create: `Source/PuddingCodeIntelligenceTests/PuddingCodeIntelligenceTests.csproj`
  - Dedicated test project for code intelligence core behavior.
- Create: `Source/PuddingCodeIntelligenceTests/Contracts/CodeIntelligenceContractTests.cs`
  - DTO and API shape tests.
- Create: `Source/PuddingCodeIntelligenceTests/Storage/SqliteCodeIndexStoreTests.cs`
  - SQLite store tests.
- Create: `Source/PuddingCodeIntelligenceTests/CSharp/RoslynCSharpIndexerTests.cs`
  - C# indexer integration tests using temporary mini projects.
- Create: `Source/PuddingCodeIntelligenceTests/Services/CodeProjectRegistryTests.cs`
  - Project add/remove/list tests.
- Create: `Source/PuddingCodeIntelligenceTests/Lsp/NoOpLanguageServerServiceTests.cs`
  - LSP integration scaffold tests.
- Modify: `PuddingAgentNetwork.slnx`
  - Add the new library and test project.
- Modify: `Source/PuddingRuntime/PuddingRuntime.csproj`
  - Reference `PuddingCodeIntelligence`.
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
  - Call `AddPuddingCodeIntelligence(...)`.
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Code/CodeIntelligenceTools.cs`
  - Native Pudding code tools.
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`
  - Add descriptor/permission/execution tests for code tools.
- Modify: `Source/PuddingAgent/BuiltInAgentTemplates.cs`
  - Add read-only code query tools to built-in templates only after descriptor tests are green.
- Modify: `Docs/07架构/50ADR-049代码语义索引与LSP编辑服务ADR.md`
  - Mark implementation decisions confirmed by MVP, including Tool category choice.
- Modify: `Docs/Memory/2026-06-11.md`
  - Record implementation progress and verification commands.

## Implementation Notes

- Do not add `ToolCategory.Code` in the MVP. Use `ToolCategory.Query` for read-only code graph tools and `ToolCategory.FileSystem` for rename execution.
- Expose `code_project_add`, `code_project_remove`, and `code_project_list` so agents can manage project directories without knowing index internals.
- Do not implement rename preview, rename apply, or template exposure in this iteration. IDE actions remain a documented follow-up.
- Keep database paths workspace-keyed and outside source projects by default: `PuddingDataPaths.DatabasesRoot/code-index/<workspaceId>/code_index.db`.
- Query tools default to all registered projects in the workspace. `ProjectId` is optional for narrowing results, not required for basic use.
- Do not assume registered project directories live under the Pudding workspace directory. Every source path check must use the registered project root, not workspace root.
- Do not write source code in this iteration. Registry/index DB writes live under `PuddingDataPaths`.
- Index generated files only when they are source files inside the registered project root and not under ignored directories; do not index compiler-generated `obj` files.
- Removing a project deletes only Pudding registry/index rows, never source files.

## Task 1: Project Skeleton And Contracts

**Files:**
- Create: `Source/PuddingCodeIntelligenceTests/PuddingCodeIntelligenceTests.csproj`
- Create: `Source/PuddingCodeIntelligenceTests/Contracts/CodeIntelligenceContractTests.cs`
- Create: `Source/PuddingCodeIntelligence/PuddingCodeIntelligence.csproj`
- Create: `Source/PuddingCodeIntelligence/Contracts/CodeIntelligenceModels.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeProjectRegistry.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeWorkspaceResolver.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeIndexer.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeIndexStore.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeQueryService.cs`
- Create: `Source/PuddingCodeIntelligence/Contracts/ICodeIdeActionService.cs`
- Modify: `PuddingAgentNetwork.slnx`

- [ ] **Step 1: Create the failing contract test project**

Create `Source/PuddingCodeIntelligenceTests/PuddingCodeIntelligenceTests.csproj`:

```xml
<Project Sdk="MSTest.Sdk/4.0.1">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseVSTest>true</UseVSTest>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\PuddingCodeIntelligence\PuddingCodeIntelligence.csproj" />
  </ItemGroup>
</Project>
```

Create `Source/PuddingCodeIntelligenceTests/Contracts/CodeIntelligenceContractTests.cs`:

```csharp
using PuddingCode.CodeIntelligence;

namespace PuddingCodeIntelligenceTests.Contracts;

[TestClass]
public sealed class CodeIntelligenceContractTests
{
    [TestMethod]
    public void SymbolRecord_Carries_Stable_Identity_And_Source_Span()
    {
        var span = new CodeSpan("Source/App/OrderService.cs", 10, 5, 12, 6);
        var symbol = new CodeSymbolRecord(
            WorkspaceId: "workspace-1",
            ProjectId: "project-1",
            SymbolId: "csharp:T:App.OrderService",
            Language: CodeLanguage.CSharp,
            Kind: CodeSymbolKind.Class,
            Name: "OrderService",
            QualifiedName: "App.OrderService",
            Signature: "public sealed class OrderService",
            FilePath: "Source/App/OrderService.cs",
            Span: span,
            ParentSymbolId: null);

        Assert.AreEqual("csharp:T:App.OrderService", symbol.SymbolId);
        Assert.AreEqual(CodeSymbolKind.Class, symbol.Kind);
        Assert.AreEqual(10, symbol.Span.StartLine);
    }

    [TestMethod]
    public void CodeProjectRecord_Carries_Project_Directory_Metadata()
    {
        var project = new CodeProjectRecord(
            WorkspaceId: "workspace-1",
            ProjectId: "project-1",
            Name: "PuddingAgent",
            RootPath: "E:/github/AgentNetworkPlan/PuddingAgent",
            SolutionPath: "E:/github/AgentNetworkPlan/PuddingAgent/PuddingAgentNetwork.slnx",
            ProjectPaths: ["E:/github/AgentNetworkPlan/PuddingAgent/Source/PuddingCore/PuddingCore.csproj"],
            Status: CodeProjectStatus.Ready,
            AddedAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            RemovedAtUtc: null);

        Assert.AreEqual("project-1", project.ProjectId);
        Assert.AreEqual(CodeProjectStatus.Ready, project.Status);
    }

    [TestMethod]
    public void RenameActionRequest_Targets_A_Registered_Project()
    {
        var request = new CodeIdeRenameRequest(
            WorkspaceId: "workspace-1",
            ProjectId: "project-1",
            SymbolId: null,
            FilePath: "Source/App/OrderService.cs",
            Line: 10,
            Column: 18,
            NewName: "PurchaseService",
            ApplyChanges: false);

        Assert.AreEqual("project-1", request.ProjectId);
        Assert.AreEqual("PurchaseService", request.NewName);
        Assert.AreEqual(10, request.Line);
    }
}
```

- [ ] **Step 2: Add the projects to `PuddingAgentNetwork.slnx`**

Add these entries:

```xml
<Project Path="Source/PuddingCodeIntelligence/PuddingCodeIntelligence.csproj" />
```

under `/Source/`, and:

```xml
<Project Path="Source/PuddingCodeIntelligenceTests/PuddingCodeIntelligenceTests.csproj" />
```

under `/Tests/`.

- [ ] **Step 3: Run the contract tests to verify RED**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --no-restore --logger "console;verbosity=minimal"
```

Expected: restore/build fails because `Source/PuddingCodeIntelligence/PuddingCodeIntelligence.csproj` and `PuddingCode.CodeIntelligence` types do not exist.

- [ ] **Step 4: Create the implementation project**

Create `Source/PuddingCodeIntelligence/PuddingCodeIntelligence.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Build.Locator" Version="1.11.2" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.3.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.3.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PuddingCore\PuddingCore.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Add model contracts**

Create `Source/PuddingCodeIntelligence/Contracts/CodeIntelligenceModels.cs`:

```csharp
namespace PuddingCode.CodeIntelligence;

public enum CodeLanguage { Unknown = 0, CSharp = 1 }
public enum CodeProjectStatus { Pending = 0, Indexing, Ready, Stale, Failed, Removed }
public enum CodeSymbolKind { Unknown = 0, Namespace, Class, Struct, Interface, Enum, Delegate, Method, Constructor, Property, Field, Event, Local, Parameter }
public enum CodeRelationKind { Contains = 0, Calls, References, Extends, Implements, Overrides, TypeOf, Returns }
public enum CodeRelationDirection { Outgoing = 0, Incoming = 1 }
public enum CodeIdeActionKind { RenameSymbol = 0, FormatDocument, OrganizeImports, CodeAction, ExtractMethod, MoveSymbol }

public sealed record CodeSpan(string FilePath, int StartLine, int StartColumn, int EndLine, int EndColumn);

public sealed record CodeWorkspaceDescriptor(
    string WorkspaceId,
    string ProjectId,
    string ProjectRoot,
    string? SolutionPath,
    IReadOnlyList<string> ProjectPaths);

public sealed record CodeProjectRecord(
    string WorkspaceId,
    string ProjectId,
    string Name,
    string RootPath,
    string? SolutionPath,
    IReadOnlyList<string> ProjectPaths,
    CodeProjectStatus Status,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? RemovedAtUtc);

public sealed record CodeProjectAddRequest(string WorkspaceId, string RootPath, string? Name);
public sealed record CodeProjectRemoveRequest(string WorkspaceId, string ProjectId);

public sealed record CodeFileRecord(
    string WorkspaceId,
    string ProjectId,
    string FilePath,
    CodeLanguage Language,
    string Sha256,
    DateTimeOffset LastWriteTimeUtc);

public sealed record CodeSymbolRecord(
    string WorkspaceId,
    string ProjectId,
    string SymbolId,
    CodeLanguage Language,
    CodeSymbolKind Kind,
    string Name,
    string QualifiedName,
    string? Signature,
    string FilePath,
    CodeSpan Span,
    string? ParentSymbolId);

public sealed record CodeRelationRecord(
    string WorkspaceId,
    string ProjectId,
    string SourceSymbolId,
    string TargetSymbolId,
    CodeRelationKind Kind,
    CodeSpan? Span,
    string? MetadataJson);

public sealed record CodeReferenceRecord(
    string WorkspaceId,
    string ProjectId,
    string SymbolId,
    string FilePath,
    CodeSpan Span,
    bool IsDefinition,
    string ReferenceKind);

public sealed record CodeIndexResult(string WorkspaceId, string ProjectId, int FileCount, int SymbolCount, int RelationCount, int ReferenceCount, IReadOnlyList<string> Warnings);
public sealed record CodeIndexStatus(string WorkspaceId, string? ProjectId, bool IsIndexed, bool IsStale, int FileCount, int SymbolCount, DateTimeOffset? LastIndexedAtUtc, IReadOnlyList<string> Warnings);
public sealed record CodeSymbolSearchRequest(string WorkspaceId, string? ProjectId, string Query, CodeLanguage? Language, CodeSymbolKind? Kind, int MaxResults);
public sealed record CodeSymbolDetail(string SymbolId, CodeSymbolRecord Symbol, IReadOnlyList<CodeRelationRecord> Incoming, IReadOnlyList<CodeRelationRecord> Outgoing);
public sealed record CodeIdeRenameRequest(string WorkspaceId, string ProjectId, string? SymbolId, string? FilePath, int? Line, int? Column, string NewName, bool ApplyChanges);
public sealed record CodeFileChangePreview(string FilePath, string OriginalText, string NewText);
public sealed record CodeIdeActionPreview(string WorkspaceId, string ProjectId, CodeIdeActionKind ActionKind, string TargetSymbolId, string OldName, string NewName, IReadOnlyList<CodeFileChangePreview> Changes, IReadOnlyList<string> Warnings);
```

- [ ] **Step 6: Add service contracts**

Create the interface files:

```csharp
namespace PuddingCode.CodeIntelligence;

public interface ICodeWorkspaceResolver
{
    Task<CodeWorkspaceDescriptor> ResolveAsync(CodeProjectRecord project, CancellationToken ct = default);
}

public interface ICodeProjectRegistry
{
    Task<CodeProjectRecord> AddProjectAsync(CodeProjectAddRequest request, CancellationToken ct = default);
    Task RemoveProjectAsync(CodeProjectRemoveRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(string workspaceId, bool includeRemoved = false, CancellationToken ct = default);
    Task<CodeProjectRecord?> GetProjectAsync(string workspaceId, string projectId, CancellationToken ct = default);
}

public interface ICodeIndexer
{
    Task<CodeIndexResult> IndexWorkspaceAsync(CodeWorkspaceDescriptor workspace, CancellationToken ct = default);
    Task<CodeIndexStatus> GetStatusAsync(string workspaceId, string? projectId = null, CancellationToken ct = default);
}

public interface ICodeIndexStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task UpsertProjectAsync(CodeProjectRecord project, CancellationToken ct = default);
    Task RemoveProjectAsync(string workspaceId, string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(string workspaceId, bool includeRemoved, CancellationToken ct = default);
    Task<CodeProjectRecord?> GetProjectAsync(string workspaceId, string projectId, CancellationToken ct = default);
    Task ReplaceFileIndexAsync(CodeFileRecord file, IReadOnlyList<CodeSymbolRecord> symbols, IReadOnlyList<CodeRelationRecord> relations, IReadOnlyList<CodeReferenceRecord> references, CancellationToken ct = default);
    Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(CodeSymbolSearchRequest request, CancellationToken ct = default);
    Task<CodeSymbolRecord?> GetSymbolAsync(string workspaceId, string symbolId, string? projectId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CodeRelationRecord>> GetRelationsAsync(string workspaceId, string symbolId, string? projectId, CodeRelationDirection direction, CodeRelationKind? kind, CancellationToken ct = default);
    Task<CodeIndexStatus> GetStatusAsync(string workspaceId, string? projectId = null, CancellationToken ct = default);
}

public interface ICodeQueryService
{
    Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(CodeSymbolSearchRequest request, CancellationToken ct = default);
    Task<CodeSymbolDetail?> GetSymbolDetailAsync(string workspaceId, string symbolId, string? projectId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(string workspaceId, string symbolId, string? projectId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(string workspaceId, string symbolId, string? projectId = null, CancellationToken ct = default);
}

public interface ICodeIdeActionService
{
    Task<CodeIdeActionPreview> PrepareRenameAsync(CodeIdeRenameRequest request, CancellationToken ct = default);
    Task<CodeIdeActionPreview> RenameSymbolAsync(CodeIdeRenameRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 7: Run contract tests to verify GREEN**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --logger "console;verbosity=minimal"
```

Expected: contract tests pass.

## Task 2: SQLite Code Index Store

**Files:**
- Create: `Source/PuddingCodeIntelligence/Storage/SqliteCodeIndexStore.cs`
- Create: `Source/PuddingCodeIntelligenceTests/Storage/SqliteCodeIndexStoreTests.cs`

- [ ] **Step 1: Write failing SQLite store tests**

Create tests that:

- Initialize a temp database.
- Add one code project.
- Replace one file index with two symbols and one `Calls` relation.
- Search symbols by partial name.
- Read incoming/outgoing call relations.
- Replace the same file with a smaller symbol set and verify stale rows for that file are removed.
- Remove the project and verify source files are untouched while index rows disappear.

Use this test skeleton:

```csharp
using PuddingCode.CodeIntelligence;

namespace PuddingCodeIntelligenceTests.Storage;

[TestClass]
public sealed class SqliteCodeIndexStoreTests
{
    [TestMethod]
    public async Task ReplaceFileIndexAsync_Replaces_Symbols_And_Relations_For_File()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "pudding-code-tests", $"{Guid.NewGuid():N}.db");
        var store = new SqliteCodeIndexStore(dbPath);
        await store.InitializeAsync();

        var project = new CodeProjectRecord(
            "workspace-1",
            "project-1",
            "Demo",
            "C:/repo/demo",
            "C:/repo/demo/Demo.csproj",
            ["C:/repo/demo/Demo.csproj"],
            CodeProjectStatus.Ready,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);
        await store.UpsertProjectAsync(project);

        var file = new CodeFileRecord("workspace-1", "project-1", "Source/App/A.cs", CodeLanguage.CSharp, "hash-1", DateTimeOffset.UtcNow);
        var a = Symbol("workspace-1", "project-1", "csharp:T:App.A", "A", "App.A", CodeSymbolKind.Class, file.FilePath, 1);
        var m = Symbol("workspace-1", "project-1", "csharp:M:App.A.Run", "Run", "App.A.Run()", CodeSymbolKind.Method, file.FilePath, 3);
        await store.ReplaceFileIndexAsync(
            file,
            [a, m],
            [new CodeRelationRecord("workspace-1", "project-1", a.SymbolId, m.SymbolId, CodeRelationKind.Contains, null, null)],
            []);

        var results = await store.SearchSymbolsAsync(new CodeSymbolSearchRequest("workspace-1", null, "Run", CodeLanguage.CSharp, null, 10));
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(m.SymbolId, results[0].SymbolId);

        await store.ReplaceFileIndexAsync(file with { Sha256 = "hash-2" }, [a], [], []);
        var afterReplace = await store.SearchSymbolsAsync(new CodeSymbolSearchRequest("workspace-1", null, "Run", CodeLanguage.CSharp, null, 10));
        Assert.AreEqual(0, afterReplace.Count);
    }

    private static CodeSymbolRecord Symbol(string workspaceId, string projectId, string symbolId, string name, string qualifiedName, CodeSymbolKind kind, string filePath, int line) =>
        new(workspaceId, projectId, symbolId, CodeLanguage.CSharp, kind, name, qualifiedName, null, filePath, new CodeSpan(filePath, line, 1, line, 10), null);
}
```

- [ ] **Step 2: Run store tests to verify RED**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~SqliteCodeIndexStoreTests" --logger "console;verbosity=minimal"
```

Expected: compile fails because `SqliteCodeIndexStore` does not exist.

- [ ] **Step 3: Implement SQLite schema and store**

Create `Source/PuddingCodeIntelligence/Storage/SqliteCodeIndexStore.cs` with:

- Constructor `SqliteCodeIndexStore(string dbPath)`.
- `InitializeAsync` creating `CodeProjects`, `CodeFiles`, `CodeSymbols`, `CodeRelations`, `CodeReferences`, `CodeIndexRuns`, and `CodeIdeActions`.
- Indexes on `(WorkspaceId, ProjectId)`, `(WorkspaceId, ProjectId, SymbolId)`, `(WorkspaceId, ProjectId, Name)`, `(WorkspaceId, ProjectId, QualifiedName)`, relation source/target.
- `UpsertProjectAsync`, `ListProjectsAsync`, `GetProjectAsync`, and `RemoveProjectAsync`.
- `ReplaceFileIndexAsync` transaction that deletes old symbols/references/relations for the file, upserts `CodeFiles`, inserts the new rows, and records `CodeIndexRuns`.
- `SearchSymbolsAsync` using `LIKE` over `Name`, `QualifiedName`, and `Signature`, ordered by exact name match then qualified name.
- `GetRelationsAsync` honoring incoming/outgoing and optional relation kind.
- `GetStatusAsync` returning indexed/stale counts.
- `RemoveProjectAsync` marks `CodeProjects.RemovedAtUtc` and deletes index rows for that project, without touching source files.

Use `Microsoft.Data.Sqlite` directly. Store enum values as strings to keep the database readable during early debugging.

- [ ] **Step 4: Run store tests to verify GREEN**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~SqliteCodeIndexStoreTests" --logger "console;verbosity=minimal"
```

Expected: SQLite store tests pass.

## Task 3: Project Registry, Project Resolution, And DI

**Files:**
- Create: `Source/PuddingCodeIntelligence/Services/DefaultCodeWorkspaceResolver.cs`
- Create: `Source/PuddingCodeIntelligence/Services/CodeProjectRegistry.cs`
- Create: `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs`
- Create: `Source/PuddingCodeIntelligence/DependencyInjection.cs`
- Create: `Source/PuddingCodeIntelligenceTests/Services/CodeProjectRegistryTests.cs`
- Create: `Source/PuddingCodeIntelligenceTests/Services/DefaultCodeWorkspaceResolverTests.cs`

- [ ] **Step 1: Write failing project registry and resolver tests**

Create registry tests for:

- Adding a valid directory creates one `CodeProjectRecord` with a stable `ProjectId`.
- Adding the same directory twice in the same workspace returns the existing active project instead of duplicating it.
- Adding a directory outside the Pudding workspace succeeds when the caller provides that absolute directory path.
- Removing a project marks/removes registry/index rows without deleting source files.
- Listing projects returns only active projects by default and can include removed projects when requested.

Create resolver tests for:

- A registered project root containing one `.slnx` and one `.csproj` resolves both.
- A registered project root containing multiple solution files returns the lexicographically first solution and all project paths.
- Missing registered project root fails with `DirectoryNotFoundException`.
- Paths under `bin`, `obj`, `.git`, `.pudding-code`, and `node_modules` are ignored.

- [ ] **Step 2: Run registry and resolver tests to verify RED**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~CodeProjectRegistryTests|FullyQualifiedName~DefaultCodeWorkspaceResolverTests" --logger "console;verbosity=minimal"
```

Expected: compile fails because registry and resolver types do not exist.

- [ ] **Step 3: Implement project registry, resolver, and DI registration**

`CodeProjectRegistry` behavior:

- Accepts `CodeProjectAddRequest`.
- Normalizes `RootPath` with `Path.GetFullPath`.
- Does not compare the project root against Pudding workspace filesystem paths.
- Rejects missing directories.
- Builds a stable `ProjectId` from workspace id + normalized root path hash.
- Uses `DefaultCodeWorkspaceResolver` to discover solution/project metadata.
- Stores the project via `ICodeIndexStore.UpsertProjectAsync`.
- `RemoveProjectAsync` delegates to `ICodeIndexStore.RemoveProjectAsync` and never deletes source files.
- `ListProjectsAsync` delegates to the store.

`DefaultCodeWorkspaceResolver` behavior:

- Normalize `project.RootPath` with `Path.GetFullPath`.
- Reject missing roots.
- Search for `*.sln`, `*.slnx`, and `*.csproj`.
- Skip ignored directory segments.
- Prefer `.slnx`, then `.sln`, then project-only mode.

Create `DependencyInjection.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PuddingCode.CodeIntelligence;

public static class CodeIntelligenceServiceCollectionExtensions
{
    public static IServiceCollection AddPuddingCodeIntelligence(this IServiceCollection services)
    {
        services.TryAddSingleton<ICodeProjectRegistry, CodeProjectRegistry>();
        services.TryAddSingleton<ICodeWorkspaceResolver, DefaultCodeWorkspaceResolver>();
        services.TryAddSingleton<ICodeQueryService, CodeQueryService>();
        return services;
    }
}
```

`CodeQueryService` should delegate to `ICodeIndexStore` and implement callers/callees as `Calls` incoming/outgoing relations.

- [ ] **Step 4: Run resolver tests to verify GREEN**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~DefaultCodeWorkspaceResolverTests" --logger "console;verbosity=minimal"
```

Expected: registry and resolver tests pass.

## Task 4: C# Roslyn Semantic Indexer

**Files:**
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynWorkspaceBootstrapper.cs`
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynSymbolId.cs`
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynCSharpIndexer.cs`
- Create: `Source/PuddingCodeIntelligenceTests/CSharp/RoslynCSharpIndexerTests.cs`
- Modify: `Source/PuddingCodeIntelligence/DependencyInjection.cs`

- [ ] **Step 1: Write failing C# indexer integration tests**

Create a temp C# project with:

```csharp
namespace Demo;

public sealed class Caller
{
    public string Run()
    {
        var service = new Callee();
        return service.Format("ok");
    }
}

public sealed class Callee
{
    public string Format(string value) => value.ToUpperInvariant();
}
```

Assert after indexing:

- `Caller` and `Callee` class symbols exist.
- `Run` and `Format` method symbols exist.
- There is a `Calls` relation from `Run` to `Format`.
- The status reports at least one file and four symbols.

- [ ] **Step 2: Run indexer tests to verify RED**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~RoslynCSharpIndexerTests" --logger "console;verbosity=minimal"
```

Expected: compile fails because Roslyn indexer types do not exist.

- [ ] **Step 3: Implement MSBuild bootstrap and symbol id helper**

`RoslynWorkspaceBootstrapper`:

- Uses a static lock.
- Calls `MSBuildLocator.RegisterDefaults()` only when `MSBuildLocator.IsRegistered` is false.
- Catches duplicate registration and logs a warning only when the process is already registered.

`RoslynSymbolId`:

- For symbols with documentation IDs, return `csharp:` + `DocumentationCommentId.CreateDeclarationId(symbol)`.
- For local symbols without documentation IDs, return `csharp:local:` + normalized file path + span + symbol name.
- For fallback symbols, return `csharp:display:` + `symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)`.

- [ ] **Step 4: Implement Roslyn indexer**

`RoslynCSharpIndexer` should:

- Implement `ICodeIndexer`.
- Load `.sln`/`.slnx` through `MSBuildWorkspace.OpenSolutionAsync` when `SolutionPath` exists.
- Load each `.csproj` through `MSBuildWorkspace.OpenProjectAsync` in project-only mode.
- Walk documents with file paths inside the registered project root and outside ignored directories.
- Extract declared symbols for namespaces, types, methods, constructors, properties, fields, events, locals, and parameters.
- Build `Contains` relations from type to member and namespace to type when parent symbols exist.
- Extract invocation call edges by visiting `InvocationExpressionSyntax`; resolve target with `SemanticModel.GetSymbolInfo`.
- Insert only call edges where both source and target symbols are in the current index batch.
- Write per-file batches through `ICodeIndexStore.ReplaceFileIndexAsync`.

- [ ] **Step 5: Register indexer services**

Update `AddPuddingCodeIntelligence`:

```csharp
services.TryAddSingleton<ICodeIndexer, RoslynCSharpIndexer>();
```

Register `ICodeIndexStore` later from the runtime composition root because it needs the workspace-keyed registry/index DB path. The DB path is under `PuddingDataPaths`, not under any registered project directory.

- [ ] **Step 6: Run C# indexer tests to verify GREEN**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~RoslynCSharpIndexerTests" --logger "console;verbosity=minimal"
```

Expected: Roslyn indexer tests pass.

## Task 5: Read-Only Runtime Code Tools

**Files:**
- Modify: `Source/PuddingRuntime/PuddingRuntime.csproj`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Code/CodeIntelligenceTools.cs`
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [ ] **Step 1: Write failing tool descriptor tests**

Add tests asserting:

- `code_project_add`, `code_project_remove`, and `code_project_list` have valid tool ids.
- `code_project_add` and `code_project_remove` are not source-code write tools; they write only Pudding registry/index state.
- `code_project_list` is `ToolCategory.Query`, `ToolPermissionLevel.Low`, and `ReadOnly | ConcurrencySafe`.
- `code_project_add` is `ToolCategory.Orchestration`, `ToolPermissionLevel.Medium`, and `ConcurrencySafe`.
- `code_project_remove` is `ToolCategory.Orchestration`, `ToolPermissionLevel.Medium`, and `Destructive` because it removes registry/index state, even though it never deletes source files.
- `code_index_status`, `code_symbol_search`, `code_explore`, `code_callers`, `code_callees`, `code_impact` have valid tool ids.
- They are `ToolCategory.Query`.
- They are `ToolPermissionLevel.Low`.
- They include `ReadOnly | ConcurrencySafe`.
- `code_prepare_rename` is read-only but `ToolPermissionLevel.Medium`.
- `code_rename_symbol` is `ToolCategory.FileSystem`, `ToolPermissionLevel.High`, and includes `RequiresFileWrite | Destructive`.

- [ ] **Step 2: Run tool tests to verify RED**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests" --logger "console;verbosity=minimal"
```

Expected: new code tool tests fail because tool classes do not exist.

- [ ] **Step 3: Add project reference**

Update `Source/PuddingRuntime/PuddingRuntime.csproj`:

```xml
<ProjectReference Include="..\PuddingCodeIntelligence\PuddingCodeIntelligence.csproj" />
```

- [ ] **Step 4: Add runtime DI registration**

Update `Source/PuddingRuntime/DependencyInjection.cs`:

```csharp
using PuddingCode.CodeIntelligence;
```

and call before `AddPuddingToolsFromAssembly(...)`:

```csharp
services.AddPuddingCodeIntelligence();
```

Add a runtime factory for `ICodeIndexStore` after `PuddingDataPaths` is available. If no `PuddingDataPaths` exists in tests, use an in-memory/temp fallback under `Path.GetTempPath()`:

```csharp
services.TryAddSingleton<ICodeIndexStore>(sp =>
{
    var paths = sp.GetService<PuddingDataPaths>();
    var root = paths is null
        ? Path.Combine(Path.GetTempPath(), "pudding-code-index")
        : Path.Combine(paths.DatabasesRoot, "code-index");
    Directory.CreateDirectory(root);
    return new SqliteCodeIndexStore(Path.Combine(root, "code_index.db"));
});
```

- [ ] **Step 5: Implement read-only tools**

Create `CodeIntelligenceTools.cs` with:

- `CodeProjectAddTool`
- `CodeProjectRemoveTool`
- `CodeProjectListTool`
- `CodeIndexStatusTool`
- `CodeSymbolSearchTool`
- `CodeExploreTool`
- `CodeCallersTool`
- `CodeCalleesTool`
- `CodeImpactTool`

Each tool should:

- Derive from `PuddingToolBase<TArgs>`.
- Accept `WorkspaceId` and either `ProjectId`, `RootPath`, `SymbolId`, or query arguments.
- Never require the project directory to be under the Pudding workspace directory.
- Project add/remove/list tools call `ICodeProjectRegistry`.
- Query/status tools call `ICodeQueryService` or `ICodeIndexer`.
- Return compact JSON using `JsonSerializer.Serialize` with web defaults.
- Query tools never write source files. Project add/remove may write registry/index DB state under `PuddingDataPaths`.

- [ ] **Step 6: Run tool tests to verify GREEN**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests" --logger "console;verbosity=minimal"
```

Expected: descriptor tests pass and existing tool infrastructure tests remain green.

## Task 6: First IDE Action - Rename Preview

**Files:**
- Create: `Source/PuddingCodeIntelligence/CSharp/RoslynIdeActionService.cs`
- Create: `Source/PuddingCodeIntelligenceTests/CSharp/RoslynIdeActionServiceTests.cs`
- Modify: `Source/PuddingCodeIntelligence/DependencyInjection.cs`
- Modify: `Source/PuddingRuntime/Tools/BuiltIns/Code/CodeIntelligenceTools.cs`

- [ ] **Step 1: Write failing rename preview tests**

Create a temp project:

```csharp
namespace Demo;

public sealed class OrderService
{
    public string Execute() => nameof(OrderService);
}

public sealed class Consumer
{
    public string Run() => new OrderService().Execute();
}
```

Register the temp project directory through `ICodeProjectRegistry`, then call `PrepareRenameAsync` for `OrderService` with new name `PurchaseService`. Assert:

- The original files are unchanged.
- Preview contains at least one file change.
- Preview new text contains `PurchaseService`.
- Preview old text contains `OrderService`.

- [ ] **Step 2: Run rename preview tests to verify RED**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~RoslynIdeActionServiceTests" --logger "console;verbosity=minimal"
```

Expected: compile fails because `RoslynIdeActionService` does not exist.

- [ ] **Step 3: Implement prepare rename**

`RoslynIdeActionService.PrepareRenameAsync` should:

- Resolve the target project from `WorkspaceId` + `ProjectId`.
- Use the registered project root as the only source-code path boundary; do not compare against the Pudding workspace directory.
- Locate the symbol by `SymbolId` from the current compilation, or by `FilePath` + `Line` + `Column`.
- Reject empty or invalid C# identifiers using `SyntaxFacts.IsValidIdentifier`.
- Call `Renamer.RenameSymbolAsync`.
- Compare old/new solution documents.
- Build `CodeFileChangePreview` for changed source documents only.
- Reject any changed document outside the registered project root or ignored directories.
- Return warnings for generated documents and unsupported linked files.
- Not write to disk.

- [ ] **Step 4: Register edit service**

Update `AddPuddingCodeIntelligence`:

```csharp
services.TryAddSingleton<ICodeIdeActionService, RoslynIdeActionService>();
```

- [ ] **Step 5: Add `code_prepare_rename` tool**

In `CodeIntelligenceTools.cs`, add `CodePrepareRenameTool`:

- Category: `Query`
- Permission: `Medium`
- Safety: `ReadOnly`
- Output: JSON preview with changed file paths, old/new names, warnings, and a capped preview length per file.

- [ ] **Step 6: Run rename preview tests and tool tests**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~RoslynIdeActionServiceTests" --logger "console;verbosity=minimal"
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests" --logger "console;verbosity=minimal"
```

Expected: rename preview tests pass, and tool descriptor tests include `code_prepare_rename`.

## Task 7: First IDE Action - Rename Apply With Authorization Surface

**Files:**
- Modify: `Source/PuddingCodeIntelligence/CSharp/RoslynIdeActionService.cs`
- Modify: `Source/PuddingCodeIntelligenceTests/CSharp/RoslynIdeActionServiceTests.cs`
- Modify: `Source/PuddingRuntime/Tools/BuiltIns/Code/CodeIntelligenceTools.cs`
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [ ] **Step 1: Write failing rename apply tests**

Extend rename tests:

- `PrepareRenameAsync` leaves files unchanged.
- `RenameSymbolAsync` changes files on disk.
- `RenameSymbolAsync` rejects changes outside the registered project root.
- `RenameSymbolAsync` rejects invalid identifier names.

- [ ] **Step 2: Run rename apply tests to verify RED**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~RoslynIdeActionServiceTests" --logger "console;verbosity=minimal"
```

Expected: apply tests fail because `RenameSymbolAsync` does not write validated changes yet.

- [ ] **Step 3: Implement rename apply**

`RenameSymbolAsync` should:

- Reuse the same symbol resolution and preview logic as `PrepareRenameAsync`.
- Validate every changed path before writing.
- Validate file hash or last-write time immediately before write when the file was read during preview.
- Write changed documents using `File.WriteAllTextAsync`.
- Return the same preview object after write so the tool result is auditable.
- Mark index stale by recording status in the store or by writing a warning that reindex is required. If a stale flag is not yet in schema, run `IndexWorkspaceAsync` for the workspace after apply.

- [ ] **Step 4: Add `code_rename_symbol` tool**

Add `CodeRenameSymbolTool`:

- Category: `FileSystem`
- Permission: `High`
- Safety: `RequiresFileWrite | Destructive`
- Calls `ICodeIdeActionService.RenameSymbolAsync`.
- Returns changed file list, old/new symbol names, warnings, and reindex status.

The existing `ToolPermissionPolicyService` should require runtime authorization because of `High`, `RequiresFileWrite`, and `Destructive`; add assertions in `PuddingToolInfrastructureTests`.

- [ ] **Step 5: Run focused verification**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --filter "FullyQualifiedName~RoslynIdeActionServiceTests" --logger "console;verbosity=minimal"
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests" --logger "console;verbosity=minimal"
```

Expected: rename apply and permission tests pass.

## Task 8: Built-In Template Exposure And Docs

**Files:**
- Modify: `Source/PuddingAgent/BuiltInAgentTemplates.cs`
- Modify: `Docs/07架构/50ADR-049代码语义索引与LSP编辑服务ADR.md`
- Modify: `Docs/Memory/2026-06-11.md`

- [ ] **Step 1: Add read-only code tools to built-in templates**

Add only these tool ids to default template tool lists where `file_search` and `search_grep` already appear:

```text
code_project_add
code_project_remove
code_project_list
code_index_status
code_symbol_search
code_explore
code_callers
code_callees
code_impact
code_prepare_rename
```

Do not add `code_rename_symbol` to built-in defaults.

- [ ] **Step 2: Update ADR implementation notes**

In `Docs/07架构/50ADR-049代码语义索引与LSP编辑服务ADR.md`, add an MVP implementation note:

- MVP kept `ToolCategory.Code` out of scope.
- Query tools use `ToolCategory.Query`.
- Project registry tools use `ToolCategory.Orchestration`; registered project directories may live outside the Pudding workspace directory.
- Rename execution uses `ToolCategory.FileSystem` + high-risk safety flags.
- DB path is `PuddingDataPaths.DatabasesRoot/code-index/<workspaceId>/code_index.db`; it is not written under the registered project directory.

- [ ] **Step 3: Update daily memory**

Append to `Docs/Memory/2026-06-11.md`:

- Implemented code intelligence MVP tasks completed.
- Verification commands and results.
- Any known test or build gaps.

## Task 9: Full Verification

**Files:**
- No edits unless verification exposes an issue.

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj --logger "console;verbosity=minimal"
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests" --logger "console;verbosity=minimal"
```

Expected: all focused tests pass.

- [ ] **Step 2: Run project builds**

Run:

```powershell
dotnet build Source\PuddingCodeIntelligence\PuddingCodeIntelligence.csproj
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj
dotnet build PuddingAgentNetwork.slnx
```

Expected: all builds exit 0.

- [ ] **Step 3: Review diff**

Run:

```powershell
git diff --stat
git diff --check
```

Expected: no whitespace errors; diff contains only code intelligence, runtime tool integration, template exposure, tests, and docs.

- [ ] **Step 4: Manual smoke with a temp project**

Create a temp C# project under `temp/code-intelligence-smoke`, run `code_symbol_search` through the tool execution service or a small test harness, then run `code_prepare_rename`. Expected: search returns indexed symbols and prepare rename returns a preview without changing files.

## Self-Review

- Spec coverage: ADR-049 goals map to tasks: module skeleton (Task 1), SQLite code graph and project registry rows (Task 2), project registry/resolution and DI (Task 3), C# Roslyn indexing (Task 4), project/query tools (Task 5), first IDE action preview with rename (Task 6), first IDE action apply with authorization (Task 7), template/docs (Task 8), verification (Task 9).
- Placeholder scan: no open-ended placeholders, deferred-work markers, or unspecified test commands remain.
- Type consistency: all planned DTOs use the `PuddingCode.CodeIntelligence` namespace and are consumed by the planned services/tools.
- Scope control: C/C++, JS/TS, Python, HTML/CSS, and LSP adapters are intentionally deferred until the C# semantic path is verified.
