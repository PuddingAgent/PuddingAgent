# ADR-028 Memory Library Correction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring ADR-028 from partially implemented to verifiably implemented by fixing failing tests, closing workspace isolation, synchronizing schema initialization, generalizing pointers, routing subconscious writes through `IMemoryLibrarian`, and making recall source-aware.

**Architecture:** Keep `IMemoryLibrary` as deterministic storage infrastructure. Put policy and ingestion decisions in `IMemoryLibrarian`. Runtime memory tools must receive workspace scope from `SkillInvokeRequest`, never from LLM prompt reliability alone.

**Tech Stack:** .NET 10, MSTest, EF Core SQLite, SQLite FTS5, existing PuddingCore/PuddingMemoryEngine/PuddingRuntime contracts.

---

## File Structure

Create:

- `Source/PuddingMemoryEngineTests/MemoryToolsWorkspaceTests.cs`
- `Source/PuddingMemoryEngineTests/MemoryLibrarySchemaTests.cs`
- `Source/PuddingMemoryEngineTests/MemoryLibrarianTests.cs`
- `Source/PuddingMemoryEngineTests/MemoryRecallServiceTests.cs`

Modify:

- `Docs/07架构/29ADR-028记忆图书馆基础设施重构ADR.md`
- `Docs/context.md`
- `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs`
- `Source/PuddingMemoryEngine/Schema/init_library.sql`
- `Source/PuddingMemoryEngine/Data/MemoryLibraryDbInitializer.cs`
- `Source/PuddingCore/Abstractions/IMemoryLibrary.cs`
- `Source/PuddingCore/Abstractions/MemoryLibraryDtos.cs`
- `Source/PuddingMemoryEngine/Entities/LibraryEntities.cs`
- `Source/PuddingRuntime/Services/Tools/MemoryTools.cs`
- `Source/PuddingRuntime/Services/Skills/SkillRuntime.cs`
- `Source/PuddingCore/Abstractions/IMemoryLibrarian.cs`
- `Source/PuddingMemoryEngine/Services/MemoryLibrarian.cs`
- `Source/PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs`
- `Source/PuddingMemoryEngine/Services/MemoryRecallService.cs`
- `Source/PuddingMemoryEngineTests/MemoryLibraryTests.cs`

---

## Task 1: Correct ADR-028 Status and Record ADR-029

**Files:**
- Modify: `Docs/07架构/29ADR-028记忆图书馆基础设施重构ADR.md`
- Modify: `Docs/context.md`
- Create: `Docs/07架构/30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md`

- [ ] **Step 1: Change ADR-028 status**

Change the header in `29ADR-028记忆图书馆基础设施重构ADR.md` from:

```markdown
> 状态：**implemented**
```

to:

```markdown
> 状态：**partially-implemented**（基础结构已部分落地；workspace scope、schema 初始化、Pointer 泛化、Librarian 分层、source-aware recall 和测试验收仍由 ADR-029 收敛）
```

- [ ] **Step 2: Update context status**

In `Docs/context.md`, replace ADR-028 completion language with:

```markdown
ADR-028 当前为 partially-implemented。不得再把提交数量视为完成证据；ADR-028 重新标记 implemented 前必须通过 ADR-029 的测试和验收清单。
```

- [ ] **Step 3: Verify docs**

Run:

```powershell
Select-String -Path Docs\07架构\29ADR-028记忆图书馆基础设施重构ADR.md -Pattern "partially-implemented"
Select-String -Path Docs\07架构\30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md -Pattern "状态"
```

Expected: both commands find the corrected status language.

- [ ] **Step 4: Commit**

```powershell
git add Docs\07架构\29ADR-028记忆图书馆基础设施重构ADR.md Docs\07架构\30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md Docs\context.md
git commit -m "docs: mark ADR-028 partial and add ADR-029 correction plan"
```

---

## Task 2: Restore FTS Test Baseline

**Files:**
- Modify: `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs`
- Modify: `Source/PuddingMemoryEngineTests/MemoryLibraryTests.cs`

- [ ] **Step 1: Reproduce failing tests**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter "SearchBooksFts_ShouldFindByTitle|SearchChaptersFts_ShouldFindByContent|UpsertExperience_ShouldCreateBookAndChapter" --logger "console;verbosity=minimal"
```

Expected: fail with `System.InvalidOperationException: The data is NULL at ordinal`.

- [ ] **Step 2: Replace `SELECT b.*` with explicit columns**

In `SearchBooksFtsAsync`, use this SQL:

```csharp
var sql = """
    SELECT b.BookId, b.LibraryId, b.Title, b.Summary, b.Status, b.Version,
           b.AccessCount, b.LastAccessedAt, b.CreatedAt, b.UpdatedAt
    FROM Books_fts f
    JOIN Books b ON b.BookId = f.BookId
    WHERE Books_fts MATCH @query
    ORDER BY rank
    LIMIT @topK
    """;
```

Keep the mapper aligned with these ten columns:

```csharp
results.Add(new BookRecord(
    reader.GetString(0),
    reader.GetString(1),
    reader.GetString(2),
    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
    reader.GetString(4),
    reader.GetInt32(5),
    reader.GetInt32(6),
    reader.IsDBNull(7) ? null : reader.GetInt64(7),
    reader.GetInt64(8),
    reader.GetInt64(9)));
```

- [ ] **Step 3: Replace `SELECT c.*` with explicit columns**

In `SearchChaptersFtsAsync`, use this SQL:

```csharp
var sql = """
    SELECT c.ChapterId, c.BookId, c.Title, c.ChapterOrder, c.Content,
           c.ContentType, c.Importance, c.SourceSessionId, c.WordCount,
           c.CreatedAt, c.UpdatedAt, c.SourceReference, c.ReferenceType
    FROM Chapters_fts f
    JOIN Chapters c ON c.ChapterId = f.ChapterId
    WHERE Chapters_fts MATCH @query
    ORDER BY rank
    LIMIT @topK
    """;
```

Map the optional ADR-028 fields:

```csharp
results.Add(new ChapterRecord(
    reader.GetString(0),
    reader.GetString(1),
    reader.GetString(2),
    reader.GetInt32(3),
    reader.GetString(4),
    reader.GetString(5),
    reader.GetDouble(6),
    reader.IsDBNull(7) ? null : reader.GetString(7),
    reader.GetInt32(8),
    reader.GetInt64(9),
    reader.GetInt64(10),
    reader.IsDBNull(11) ? null : reader.GetString(11),
    reader.IsDBNull(12) ? null : reader.GetString(12)));
```

- [ ] **Step 4: Add a source field regression assertion**

Add a focused test to `MemoryLibraryTests.cs`:

```csharp
[TestMethod]
public async Task SearchChaptersFts_ShouldReturnSourceFieldsWhenPresent()
{
    await using var scope = await MemoryLibraryTestScope.CreateAsync();
    var library = await scope.Library.CreateLibraryAsync("ws-fts-source", "主库", null);
    var book = await scope.Library.CreateBookAsync(library.LibraryId, "来源测试", "验证来源字段");
    await scope.Library.AddChapterWithSourceAsync(
        book.BookId,
        "来源章节",
        "番茄种植来源字段验证",
        "session:abc",
        "session",
        ct: default);

    var hits = await scope.Library.SearchChaptersFtsAsync("番茄", 10);

    Assert.AreEqual("session:abc", hits.Single().SourceReference);
    Assert.AreEqual("session", hits.Single().ReferenceType);
}
```

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter "SearchBooksFts_ShouldFindByTitle|SearchChaptersFts_ShouldFindByContent|UpsertExperience_ShouldCreateBookAndChapter|SearchChaptersFts_ShouldReturnSourceFieldsWhenPresent" --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingMemoryEngine\Data\MemoryLibrary.cs Source\PuddingMemoryEngineTests\MemoryLibraryTests.cs
git commit -m "fix: stabilize memory library FTS mapping"
```

---

## Task 3: Close Workspace Scope in Memory Tools

**Files:**
- Modify: `Source/PuddingRuntime/Services/Tools/MemoryTools.cs`
- Modify: `Source/PuddingRuntime/Services/Skills/SkillRuntime.cs`
- Create: `Source/PuddingMemoryEngineTests/MemoryToolsWorkspaceTests.cs`

- [ ] **Step 1: Add effective workspace helper**

In each memory tool class, add this private helper:

```csharp
private static JsonObject WithEffectiveWorkspace(SkillInvokeRequest request)
{
    var obj = new JsonObject();
    foreach (var kv in request.Parameters)
    {
        obj[kv.Key] = JsonSerializer.SerializeToNode(kv.Value);
    }

    if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
    {
        obj["workspace_id"] = request.WorkspaceId;
    }

    return obj;
}
```

If `JsonObject` is not already imported, add:

```csharp
using System.Text.Json.Nodes;
```

- [ ] **Step 2: Use the helper in skill execution**

Replace each memory tool `IAgentSkill.ExecuteAsync` body that currently serializes `request.Parameters` with:

```csharp
public async Task<SkillInvokeResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
{
    try
    {
        var argumentsJson = JsonSerializer.Serialize(WithEffectiveWorkspace(request));
        var result = await ExecuteAsync(argumentsJson, ct);
        return SkillInvokeResult.Ok(result);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "[{SkillId}] skill invocation failed", SkillId);
        return SkillInvokeResult.Fail(ex.Message);
    }
}
```

- [ ] **Step 3: Remove unscoped grep fallback**

In `GrepMemoryTool`, replace:

```csharp
if (results.Count == 0)
{
    results = (await _library.SmartSearchAsync(query, topK, ct)).ToList();
}
```

with:

```csharp
if (results.Count == 0)
{
    _logger.LogDebug("[GrepMemory] no scoped hits workspace={WorkspaceId} query={Query}", workspaceId, query);
}
```

- [ ] **Step 4: Keep schema honest**

In `SkillRuntime.cs`, add `workspace_id` to the schemas for `save_memory`, `manage_memory`, and `grep_memory` as an optional compatibility parameter. The description must say runtime injects workspace automatically:

```csharp
["workspace_id"] = new { type = "string", description = "Optional compatibility override; runtime injects the active workspace automatically." }
```

- [ ] **Step 5: Add workspace tests**

Create `MemoryToolsWorkspaceTests.cs` with two tests:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.Tools;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class MemoryToolsWorkspaceTests
{
    [TestMethod]
    public async Task SaveMemorySkill_ShouldUseRequestWorkspace_WhenParameterMissing()
    {
        await using var scope = await MemoryLibraryTestScope.CreateAsync();
        var tool = new SaveMemoryTool(scope.LibraryConvenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);

        var result = await ((IAgentSkill)tool).ExecuteAsync(new SkillInvokeRequest
        {
            WorkspaceId = "ws-tool",
            Parameters = new Dictionary<string, object?>
            {
                ["type"] = "note",
                ["book"] = "工具测试",
                ["content"] = "workspace 注入验证"
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        var books = await scope.Library.ListLibrariesAsync("ws-tool");
        Assert.AreEqual(1, books.Count);
    }

    [TestMethod]
    public async Task GrepMemorySkill_ShouldNotFallbackToOtherWorkspace()
    {
        await using var scope = await MemoryLibraryTestScope.CreateAsync();
        var wsB = await scope.Library.CreateLibraryAsync("ws-b", "B", null);
        var bookB = await scope.Library.CreateBookAsync(wsB.LibraryId, "B记忆", "隔离");
        await scope.Library.AddChapterAsync(bookB.BookId, "B章节", "跨空间唯一关键词");

        var tool = new GrepMemoryTool(scope.LibraryConvenience, scope.Library, NullLogger<GrepMemoryTool>.Instance);
        var result = await ((IAgentSkill)tool).ExecuteAsync(new SkillInvokeRequest
        {
            WorkspaceId = "ws-a",
            Parameters = new Dictionary<string, object?>
            {
                ["action"] = "search",
                ["query"] = "跨空间唯一关键词",
                ["top_k"] = 10
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(JsonSerializer.Serialize(result.Data).Contains("跨空间唯一关键词"));
    }
}
```

- [ ] **Step 6: Verify**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter MemoryToolsWorkspaceTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add Source\PuddingRuntime\Services\Tools\MemoryTools.cs Source\PuddingRuntime\Services\Skills\SkillRuntime.cs Source\PuddingMemoryEngineTests\MemoryToolsWorkspaceTests.cs
git commit -m "fix: enforce workspace scope in memory tools"
```

---

## Task 4: Synchronize SQLite Schema Initialization

**Files:**
- Modify: `Source/PuddingMemoryEngine/Schema/init_library.sql`
- Modify: `Source/PuddingMemoryEngine/Data/MemoryLibraryDbInitializer.cs`
- Create: `Source/PuddingMemoryEngineTests/MemoryLibrarySchemaTests.cs`

- [ ] **Step 1: Add missing chapter columns**

Append idempotent alter statements:

```sql
ALTER TABLE Chapters ADD COLUMN SourceReference TEXT;
ALTER TABLE Chapters ADD COLUMN ReferenceType TEXT;
```

`MemoryLibraryDbInitializer` already ignores duplicate column errors; keep that behavior.

- [ ] **Step 2: Add ADR-028 tables**

Add:

```sql
CREATE TABLE IF NOT EXISTS SourceReferences (
  SourceReferenceId TEXT PRIMARY KEY,
  WorkspaceId TEXT NOT NULL,
  OwnerType TEXT NOT NULL,
  OwnerId TEXT NOT NULL,
  TargetType TEXT NOT NULL,
  TargetId TEXT NOT NULL,
  TargetRange TEXT NULL,
  Label TEXT NULL,
  Description TEXT NULL,
  CreatedAt INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_SourceReferences_Owner
  ON SourceReferences (OwnerType, OwnerId);

CREATE INDEX IF NOT EXISTS IX_SourceReferences_Workspace_Target
  ON SourceReferences (WorkspaceId, TargetType, TargetId);

CREATE TABLE IF NOT EXISTS MemoryTreeNodes (
  NodeId TEXT PRIMARY KEY,
  WorkspaceId TEXT NOT NULL,
  LibraryId TEXT NOT NULL,
  ParentNodeId TEXT NULL,
  Path TEXT NOT NULL,
  Name TEXT NOT NULL,
  Summary TEXT NULL,
  NodeType TEXT NOT NULL,
  Status TEXT NOT NULL,
  SortOrder INTEGER NOT NULL,
  CreatedAt INTEGER NOT NULL,
  UpdatedAt INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_MemoryTreeNodes_Workspace_Library_Parent
  ON MemoryTreeNodes (WorkspaceId, LibraryId, ParentNodeId);

CREATE UNIQUE INDEX IF NOT EXISTS UX_MemoryTreeNodes_Workspace_Library_Path
  ON MemoryTreeNodes (WorkspaceId, LibraryId, Path);

CREATE TABLE IF NOT EXISTS BookTreeMounts (
  Id TEXT PRIMARY KEY,
  BookId TEXT NOT NULL,
  NodeId TEXT NOT NULL,
  Weight REAL NOT NULL,
  CreatedAt INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_BookTreeMounts_Book_Node
  ON BookTreeMounts (BookId, NodeId);
```

- [ ] **Step 3: Add generalized pointer columns**

Append:

```sql
ALTER TABLE Pointers ADD COLUMN WorkspaceId TEXT;
ALTER TABLE Pointers ADD COLUMN SourceType TEXT;
ALTER TABLE Pointers ADD COLUMN SourceId TEXT;

CREATE INDEX IF NOT EXISTS IX_Pointers_Workspace_Source
  ON Pointers (WorkspaceId, SourceType, SourceId);
```

- [ ] **Step 4: Add schema test**

Create a test that runs `MemoryLibraryDbInitializer.InitializeAsync()` on a temp SQLite file, then writes source references, tree nodes, and mounts through `MemoryLibrary`.

Use assertions:

```csharp
Assert.IsFalse(string.IsNullOrWhiteSpace(source.SourceReferenceId));
Assert.AreEqual("session", source.TargetType);
Assert.AreEqual("root", node.NodeType);
Assert.AreEqual(book.BookId, mount.BookId);
```

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter MemoryLibrarySchemaTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingMemoryEngine\Schema\init_library.sql Source\PuddingMemoryEngine\Data\MemoryLibraryDbInitializer.cs Source\PuddingMemoryEngineTests\MemoryLibrarySchemaTests.cs
git commit -m "fix: synchronize memory library sqlite schema"
```

---

## Task 5: Generalize Pointer Model with Backward Compatibility

**Files:**
- Modify: `Source/PuddingCore/Abstractions/IMemoryLibrary.cs`
- Modify: `Source/PuddingCore/Abstractions/MemoryLibraryDtos.cs`
- Modify: `Source/PuddingMemoryEngine/Entities/LibraryEntities.cs`
- Modify: `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs`
- Modify: `Source/PuddingMemoryEngineTests/MemoryLibraryTests.cs`

- [ ] **Step 1: Extend DTO**

Change `PointerRecord` to include nullable generalized fields:

```csharp
public sealed record PointerRecord(
    string PointerId,
    string ChapterId,
    string TargetType,
    string TargetId,
    string? TargetLabel,
    string? Description,
    double Relevance,
    long CreatedAt,
    string? WorkspaceId = null,
    string? SourceType = null,
    string? SourceId = null);
```

- [ ] **Step 2: Extend interface**

Add overloads:

```csharp
Task<PointerRecord> CreatePointerAsync(
    string workspaceId,
    string sourceType,
    string sourceId,
    string targetType,
    string targetId,
    string? label = null,
    string? description = null,
    CancellationToken ct = default);

Task<IReadOnlyList<PointerRecord>> GetPointersAsync(
    string workspaceId,
    string sourceType,
    string sourceId,
    CancellationToken ct = default);
```

- [ ] **Step 3: Extend entity**

Add fields to `PointerEntity`:

```csharp
public string? WorkspaceId { get; set; }
public string? SourceType { get; set; }
public string? SourceId { get; set; }
```

- [ ] **Step 4: Implement old API as compatibility wrapper**

In old `CreatePointerAsync(chapterId, ...)`, resolve workspace through chapter -> book -> library, then write:

```csharp
WorkspaceId = workspaceId,
SourceType = "chapter",
SourceId = chapterId,
ChapterId = chapterId,
```

- [ ] **Step 5: Implement new API**

Write a new `PointerEntity` with:

```csharp
WorkspaceId = workspaceId,
SourceType = sourceType,
SourceId = sourceId,
ChapterId = sourceType.Equals("chapter", StringComparison.OrdinalIgnoreCase) ? sourceId : string.Empty,
TargetType = targetType,
TargetId = targetId,
TargetLabel = label,
Description = description,
Relevance = 1.0,
CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
```

- [ ] **Step 6: Add generalized pointer tests**

Add tests:

```csharp
[TestMethod]
public async Task CreatePointer_NewApi_ShouldSupportBookToUrl()
{
    await using var scope = await MemoryLibraryTestScope.CreateAsync();
    var lib = await scope.Library.CreateLibraryAsync("ws-pointer", "库", null);
    var book = await scope.Library.CreateBookAsync(lib.LibraryId, "书", "摘要");

    var pointer = await scope.Library.CreatePointerAsync("ws-pointer", "book", book.BookId, "url", "https://example.com", "参考");
    var pointers = await scope.Library.GetPointersAsync("ws-pointer", "book", book.BookId);

    Assert.AreEqual(pointer.PointerId, pointers.Single().PointerId);
    Assert.AreEqual("book", pointers.Single().SourceType);
    Assert.AreEqual(book.BookId, pointers.Single().SourceId);
}
```

Keep existing pointer tests unchanged and passing.

- [ ] **Step 7: Verify**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter "CreatePointer|GetPointers|ResolveBacklinks" --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 8: Commit**

```powershell
git add Source\PuddingCore\Abstractions\IMemoryLibrary.cs Source\PuddingCore\Abstractions\MemoryLibraryDtos.cs Source\PuddingMemoryEngine\Entities\LibraryEntities.cs Source\PuddingMemoryEngine\Data\MemoryLibrary.cs Source\PuddingMemoryEngineTests\MemoryLibraryTests.cs
git commit -m "feat: generalize memory library pointers"
```

---

## Task 6: Make MemoryLibrarian the Subconscious Write Path

**Files:**
- Modify: `Source\PuddingCore\Abstractions\IMemoryLibrarian.cs`
- Modify: `Source\PuddingMemoryEngine\Services\MemoryLibrarian.cs`
- Modify: `Source\PuddingMemoryEngine\Services\SubconsciousOrchestrator.cs`
- Create: `Source\PuddingMemoryEngineTests\MemoryLibrarianTests.cs`

- [ ] **Step 1: Refactor `MemoryLibrarian` dependencies**

Change constructor dependencies from convenience-first to core-first:

```csharp
public MemoryLibrarian(
    IMemoryLibrary library,
    ILogger<MemoryLibrarian> logger)
{
    _library = library;
    _logger = logger;
}
```

- [ ] **Step 2: Implement ingestion using primitives**

In `IngestExperienceAsync`, perform:

```csharp
var libraries = await _library.ListLibrariesAsync(request.WorkspaceId, ct);
var library = libraries.FirstOrDefault()
    ?? await _library.CreateLibraryAsync(request.WorkspaceId, "Default Memory Library", "Workspace memory library", ct);

await _library.EnsureDefaultBooksAsync(library.LibraryId, ct);
var books = await _library.ListBooksScopedAsync(request.WorkspaceId, 200, ct);
var book = books.FirstOrDefault(b => b.Title.Equals(request.Package.BookTitle, StringComparison.OrdinalIgnoreCase))
    ?? await _library.CreateBookAsync(library.LibraryId, request.Package.BookTitle, request.Package.Summary, ct: ct);

var chapter = await _library.AddChapterWithSourceAsync(
    book.BookId,
    request.Package.ChapterTitle,
    request.Package.Content,
    request.Package.SourceReference,
    request.Package.ReferenceType,
    request.Package.SourceSessionId,
    request.Package.Importance,
    ct);
```

Then create a `SourceReference` when `SourceReference` is present.

- [ ] **Step 3: Route orchestrator through librarian**

In `SubconsciousOrchestrator`, replace direct `_libraryConvenience.UpsertExperienceAsync(...)` calls with:

```csharp
var writeResult = await _memoryLibrarian.IngestExperienceAsync(new MemoryIngestionRequest
{
    WorkspaceId = workspaceId,
    Package = package,
    RequestedBy = "subconscious",
    Reason = "session_learning"
}, ct);
```

Keep `IMemoryLibraryConvenience` only as a legacy fallback when `IMemoryLibrarian` is unavailable in test-only construction.

- [ ] **Step 4: Replace placeholder tree maintenance behavior**

For unsupported `MemoryTreeOperation` values, throw:

```csharp
throw new NotSupportedException($"Memory tree operation '{operation.OperationType}' is not supported by MemoryLibrarian.");
```

Do not silently log and continue.

- [ ] **Step 5: Add librarian ingestion tests**

Create `MemoryLibrarianTests.cs`:

```csharp
[TestMethod]
public async Task IngestExperienceAsync_ShouldCreateBookChapterAndSourceReference()
{
    await using var scope = await MemoryLibraryTestScope.CreateAsync();
    var librarian = new MemoryLibrarian(scope.Library, NullLogger<MemoryLibrarian>.Instance);

    var result = await librarian.IngestExperienceAsync(new MemoryIngestionRequest
    {
        WorkspaceId = "ws-librarian",
        Package = new ExperiencePackage
        {
            BookTitle = "航海日志",
            ChapterTitle = "一次重要决定",
            Content = "用户决定优先修复 workspace 隔离。",
            SourceReference = "session:abc",
            ReferenceType = "session",
            Importance = 0.9
        },
        RequestedBy = "test",
        Reason = "verification"
    });

    Assert.IsTrue(result.Success);
    Assert.IsFalse(string.IsNullOrWhiteSpace(result.BookId));
    Assert.IsFalse(string.IsNullOrWhiteSpace(result.ChapterId));

    var sources = await scope.Library.GetSourceReferencesAsync("chapter", result.ChapterId!);
    Assert.AreEqual("session:abc", sources.Single().TargetId);
}
```

- [ ] **Step 6: Verify**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter MemoryLibrarianTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add Source\PuddingCore\Abstractions\IMemoryLibrarian.cs Source\PuddingMemoryEngine\Services\MemoryLibrarian.cs Source\PuddingMemoryEngine\Services\SubconsciousOrchestrator.cs Source\PuddingMemoryEngineTests\MemoryLibrarianTests.cs
git commit -m "feat: route subconscious memory writes through librarian"
```

---

## Task 7: Make Recall Source-Aware

**Files:**
- Modify: `Source\PuddingMemoryEngine\Services\MemoryRecallService.cs`
- Create: `Source\PuddingMemoryEngineTests\MemoryRecallServiceTests.cs`

- [ ] **Step 1: Add source lookup helper**

In `MemoryRecallService`, add:

```csharp
private async Task<IReadOnlyList<SourceReferenceSummary>> GetChapterSourcesAsync(string chapterId, CancellationToken ct)
{
    var sources = await _memoryLibrary.GetSourceReferencesAsync("chapter", chapterId, ct);
    return sources.Select(s => new SourceReferenceSummary
    {
        SourceReferenceId = s.SourceReferenceId,
        TargetType = s.TargetType,
        TargetId = s.TargetId,
        TargetRange = s.TargetRange,
        Label = s.Label,
        ResolveStatus = SourceResolveStatus.Unsupported
    }).ToList();
}
```

- [ ] **Step 2: Populate library recall sources**

Replace the synchronous `libraryResults.Select(...)` mapping with an async loop:

```csharp
var libraryMemories = new List<RecalledMemory>();
foreach (var l in libraryResults)
{
    var sources = await GetChapterSourcesAsync(l.ChapterId, ct);
    libraryMemories.Add(new RecalledMemory
    {
        MemoryId = l.ChapterId,
        Source = "library",
        Content = l.Content,
        Score = 1.0,
        Importance = l.Importance,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(l.CreatedAt),
        BookId = l.BookId,
        ChapterId = l.ChapterId,
        TreePath = l.BookTitle,
        Sources = sources
    });
}

AddRrf(libraryMemories, 1.0);
```

- [ ] **Step 3: Add recall source test**

Create a test that writes a chapter, attaches a source reference, calls recall, and asserts `Sources` is non-empty:

```csharp
[TestMethod]
public async Task RecallAsync_ShouldReturnSourceSummariesForLibraryHits()
{
    await using var scope = await MemoryLibraryTestScope.CreateAsync();
    var lib = await scope.Library.CreateLibraryAsync("ws-recall-source", "库", null);
    var book = await scope.Library.CreateBookAsync(lib.LibraryId, "番茄", "种植");
    var chapter = await scope.Library.AddChapterAsync(book.BookId, "土壤", "番茄需要排水良好的土壤。");
    await scope.Library.AddSourceReferenceAsync(new SourceReferenceCreateRequest
    {
        WorkspaceId = "ws-recall-source",
        OwnerType = "chapter",
        OwnerId = chapter.ChapterId,
        TargetType = "session",
        TargetId = "session-1",
        Label = "原始会话"
    });

    var recall = new MemoryRecallService(scope.Library, scope.FactStore, scope.PreferenceStore, NullLogger<MemoryRecallService>.Instance);
    var result = await recall.RecallAsync("ws-recall-source", "agent", "番茄土壤", 5);

    var hit = result.Items.Single(i => i.ChapterId == chapter.ChapterId);
    Assert.AreEqual("session", hit.Sources.Single().TargetType);
    Assert.AreEqual("session-1", hit.Sources.Single().TargetId);
}
```

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter MemoryRecallServiceTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingMemoryEngine\Services\MemoryRecallService.cs Source\PuddingMemoryEngineTests\MemoryRecallServiceTests.cs
git commit -m "feat: include source references in memory recall"
```

---

## Task 8: Final Verification and ADR-028 Reclose

**Files:**
- Modify: `Docs/07架构/29ADR-028记忆图书馆基础设施重构ADR.md`
- Modify: `Docs/07架构/30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md`
- Modify: `Docs/context.md`

- [ ] **Step 1: Run complete targeted tests**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --nologo
```

Expected: PASS.

- [ ] **Step 2: Run agent build**

Run:

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: `0 个错误`.

- [ ] **Step 3: Reclose docs only after evidence passes**

If both commands pass, update ADR-028:

```markdown
> 状态：**implemented**（ADR-029 纠偏验收通过）
```

Update ADR-029:

```markdown
> 状态：**implemented**
```

Add evidence lines to ADR-029:

```markdown
验收：
- `dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --nologo`：PASS
- `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo`：PASS
```

- [ ] **Step 4: Commit**

```powershell
git add Docs\07架构\29ADR-028记忆图书馆基础设施重构ADR.md Docs\07架构\30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md Docs\context.md
git commit -m "docs: close ADR-028 after ADR-029 verification"
```

---

## Self-Review Checklist

- ADR-028 completion status is not restored until tests pass.
- Every runtime memory path is workspace scoped.
- FTS queries do not depend on `SELECT *` ordinal shape.
- SQLite initialization can create and upgrade ADR-028 structures.
- Old pointer APIs continue to work while new generalized APIs exist.
- Subconscious library writes go through `IMemoryLibrarian`.
- Recall results include source summaries.
- Final closure requires both test and build evidence.

