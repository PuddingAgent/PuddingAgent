# Memory Graph Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only local knowledge graph projection to the Memory Library admin page without adding graph database tables or risky graph write operations.

**Architecture:** Add a focused `MemoryGraphAdminService` and `MemoryGraphAdminController` that project existing `Library / Book / Chapter / SourceReference / Pointer` data into graph DTOs. The Admin SPA adds a `Pages / Graph` segmented view; Graph view renders a local one-hop neighborhood for the currently selected Library, Book, Chapter, or TreeNode. This phase deliberately does not persist `GraphEntity`, `GraphFact`, `GraphRelation`, `GraphEvidence`, or `GraphProposal` tables.

**Tech Stack:** .NET 10, ASP.NET Core controllers, EF Core SQLite, MSTest, Moq, React, TypeScript, Umi request, Ant Design, `@ant-design/pro-components`.

---

## File Structure

Create:

- `Source/PuddingPlatform/Services/MemoryGraphAdminService.cs`  
  Owns read-only graph DTOs, scope validation, and projection from existing memory-library data.
- `Source/PuddingPlatform/Controllers/Api/MemoryGraphAdminController.cs`  
  Exposes `/api/admin/memory-graph/...` read-only endpoints.
- `Source/PuddingPlatformTests/Services/MemoryGraphAdminServiceTests.cs`  
  Tests local graph projection and scope isolation.
- `Source/PuddingWebApiTests/MemoryGraphAdminApiControllerTests.cs`  
  Tests API validation and error mapping.
- `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryGraphView.tsx`  
  Renders compact read-only graph nodes, edges, and evidence list.

Modify:

- `Source/PuddingAgent/Program.cs`  
  Register `IMemoryGraphAdminService`.
- `Source/PuddingPlatformAdmin/src/pages/memory-library/types.ts`  
  Add graph DTO TypeScript interfaces.
- `Source/PuddingPlatformAdmin/src/services/platform/api.ts`  
  Add graph API client functions.
- `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx`  
  Add view switching and graph data loading.
- `Source/PuddingPlatformAdmin/src/pages/memory-library/styles.less`  
  Add graph workbench styles.

Do not modify:

- `Source/PuddingMemoryEngine/Entities/LibraryEntities.cs`
- `Source/PuddingMemoryEngine/Entities/SubconsciousEntities.cs`
- SQLite schema or migrations
- Runtime memory tools

---

## Task 1: Backend Graph Projection Service

**Files:**
- Create: `Source/PuddingPlatform/Services/MemoryGraphAdminService.cs`
- Create: `Source/PuddingPlatformTests/Services/MemoryGraphAdminServiceTests.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] **Step 1: Write failing service tests**

Create `Source/PuddingPlatformTests/Services/MemoryGraphAdminServiceTests.cs` with this content:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class MemoryGraphAdminServiceTests
{
    [TestMethod]
    public async Task GetNeighborhoodAsync_Book_ShouldProjectBookChaptersSourcesAndPointers()
    {
        await using var scope = await CreateScopeAsync();
        var library = await CreateAgentLibraryAsync(scope.Factory, "ws-graph", "agent-a", "Graph Library");
        var book = await scope.Library.CreateBookAsync(library.LibraryId, "用户画像", "长期用户画像");
        var chapter = await scope.Library.AddChapterAsync(book.BookId, "地点偏好", "用户喜欢在上海工作。");
        await scope.Library.AddSourceReferenceAsync(new SourceReferenceCreateRequest(
            "ws-graph",
            "chapter",
            chapter.ChapterId,
            "session",
            "session-1",
            "msg-1..msg-2",
            "会话片段",
            "用户直接表达地点偏好"));
        await scope.Library.CreateGeneralPointerAsync(
            "ws-graph",
            "chapter",
            chapter.ChapterId,
            "book",
            book.BookId,
            "用户画像",
            "章节属于用户画像");

        var graph = await scope.Service.GetNeighborhoodAsync(
            "ws-graph",
            "agent-a",
            library.LibraryId,
            "book",
            book.BookId,
            1);

        Assert.AreEqual("book", graph.CenterType);
        Assert.AreEqual(book.BookId, graph.CenterId);
        Assert.IsTrue(graph.Nodes.Any(n => n.Id == $"book:{book.BookId}" && n.Type == "book"));
        Assert.IsTrue(graph.Nodes.Any(n => n.Id == $"chapter:{chapter.ChapterId}" && n.Type == "chapter"));
        Assert.IsTrue(graph.Nodes.Any(n => n.Id == "source:session:session-1" && n.Type == "source"));
        Assert.IsTrue(graph.Edges.Any(e => e.SourceId == $"book:{book.BookId}" && e.TargetId == $"chapter:{chapter.ChapterId}" && e.Type == "contains"));
        Assert.IsTrue(graph.Edges.Any(e => e.SourceId == $"chapter:{chapter.ChapterId}" && e.TargetId == "source:session:session-1" && e.Type == "evidenced_by"));
        Assert.IsTrue(graph.Edges.Any(e => e.Type == "pointer"));
        Assert.HasCount(1, graph.Evidence);
    }

    [TestMethod]
    public async Task GetNeighborhoodAsync_ShouldRejectBookFromOtherAgent()
    {
        await using var scope = await CreateScopeAsync();
        await CreateAgentLibraryAsync(scope.Factory, "ws-graph-scope", "agent-a", "Agent A");
        var otherLibrary = await CreateAgentLibraryAsync(scope.Factory, "ws-graph-scope", "agent-b", "Agent B");
        var otherBook = await scope.Library.CreateBookAsync(otherLibrary.LibraryId, "Other", "Other");

        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() =>
            scope.Service.GetNeighborhoodAsync(
                "ws-graph-scope",
                "agent-a",
                otherLibrary.LibraryId,
                "book",
                otherBook.BookId,
                1));
    }

    [TestMethod]
    public async Task GetNeighborhoodAsync_Library_ShouldExposeUnmountedBooksAsChildren()
    {
        await using var scope = await CreateScopeAsync();
        var library = await CreateAgentLibraryAsync(scope.Factory, "ws-graph-library", "agent-a", "Graph Library");
        var book = await scope.Library.CreateBookAsync(library.LibraryId, "未挂载资料", "还没有目录节点");

        var graph = await scope.Service.GetNeighborhoodAsync(
            "ws-graph-library",
            "agent-a",
            library.LibraryId,
            "library",
            library.LibraryId,
            1);

        Assert.IsTrue(graph.Nodes.Any(n => n.Id == $"library:{library.LibraryId}" && n.Type == "library"));
        Assert.IsTrue(graph.Nodes.Any(n => n.Id == $"book:{book.BookId}" && n.Type == "book"));
        Assert.IsTrue(graph.Edges.Any(e => e.Type == "contains" && e.TargetId == $"book:{book.BookId}"));
    }

    private static async Task<LibraryRecord> CreateAgentLibraryAsync(
        IDbContextFactory<MemoryLibraryDbContext> factory,
        string workspaceId,
        string agentId,
        string name)
    {
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new LibraryEntity
        {
            LibraryId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            AgentId = agentId,
            Name = name,
            Description = null,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Libraries.Add(entity);
        await db.SaveChangesAsync();
        return new LibraryRecord(
            entity.LibraryId,
            entity.WorkspaceId,
            entity.Name,
            entity.Description,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.AgentId);
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;
        var factory = new TestDbContextFactory(options);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var library = new MemoryLibrary(factory);
        var service = new MemoryGraphAdminService(library, factory);
        return new TestScope(connection, factory, library, service);
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestScope(
            SqliteConnection connection,
            IDbContextFactory<MemoryLibraryDbContext> factory,
            IMemoryLibrary library,
            IMemoryGraphAdminService service)
        {
            _connection = connection;
            Factory = factory;
            Library = library;
            Service = service;
        }

        public IDbContextFactory<MemoryLibraryDbContext> Factory { get; }
        public IMemoryLibrary Library { get; }
        public IMemoryGraphAdminService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
```

- [ ] **Step 2: Run service tests to verify they fail**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --filter MemoryGraphAdminServiceTests --logger "console;verbosity=minimal"
```

Expected: FAIL because `IMemoryGraphAdminService` and `MemoryGraphAdminService` do not exist.

- [ ] **Step 3: Create the projection service**

Create `Source/PuddingPlatform/Services/MemoryGraphAdminService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingPlatform.Services;

/// <summary>知识图谱节点 DTO。Phase 1 只读投影，不代表持久化 GraphEntity。</summary>
public sealed record MemoryGraphNodeDto(
    string Id,
    string Type,
    string Label,
    string? Subtitle,
    string Status,
    double? Confidence,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>知识图谱边 DTO。Phase 1 只读投影，不代表持久化 GraphRelation。</summary>
public sealed record MemoryGraphEdgeDto(
    string Id,
    string SourceId,
    string TargetId,
    string Type,
    string Label,
    double? Confidence);

/// <summary>图谱证据 DTO，用于展示事实或关系的来源。</summary>
public sealed record MemoryGraphEvidenceDto(
    string Id,
    string ClaimType,
    string ClaimId,
    string SourceType,
    string SourceId,
    string? SourceRange,
    string? Label,
    string? Description);

/// <summary>局部图谱结果。默认用于 1-2 跳邻域，不用于全量图谱。</summary>
public sealed record MemoryGraphNeighborhoodDto(
    string WorkspaceId,
    string AgentId,
    string LibraryId,
    string CenterType,
    string CenterId,
    int Depth,
    IReadOnlyList<MemoryGraphNodeDto> Nodes,
    IReadOnlyList<MemoryGraphEdgeDto> Edges,
    IReadOnlyList<MemoryGraphEvidenceDto> Evidence);

/// <summary>记忆图谱 Admin 服务。Phase 1 只从现有 MemoryLibrary 数据投影只读图。</summary>
public interface IMemoryGraphAdminService
{
    Task<MemoryGraphNeighborhoodDto> GetNeighborhoodAsync(
        string workspaceId,
        string agentId,
        string libraryId,
        string sourceType,
        string sourceId,
        int depth,
        CancellationToken ct = default);
}

/// <summary>
/// 记忆图谱只读投影服务。
/// 不创建图谱表，不修改记忆数据，只把 Library/Book/Chapter/SourceReference/Pointer 投影为局部图。
/// </summary>
public sealed class MemoryGraphAdminService : IMemoryGraphAdminService
{
    private readonly IMemoryLibrary _library;
    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbFactory;

    public MemoryGraphAdminService(
        IMemoryLibrary library,
        IDbContextFactory<MemoryLibraryDbContext> dbFactory)
    {
        _library = library;
        _dbFactory = dbFactory;
    }

    public async Task<MemoryGraphNeighborhoodDto> GetNeighborhoodAsync(
        string workspaceId,
        string agentId,
        string libraryId,
        string sourceType,
        string sourceId,
        int depth,
        CancellationToken ct = default)
    {
        if (depth is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be between 1 and 2.");

        var library = await ValidateLibraryAsync(workspaceId, agentId, libraryId, ct);
        var nodes = new Dictionary<string, MemoryGraphNodeDto>(StringComparer.Ordinal);
        var edges = new Dictionary<string, MemoryGraphEdgeDto>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, MemoryGraphEvidenceDto>(StringComparer.Ordinal);

        AddNode(nodes, new MemoryGraphNodeDto(
            $"library:{library.LibraryId}",
            "library",
            library.Name,
            library.Description,
            "active",
            1,
            new Dictionary<string, string>
            {
                ["libraryId"] = library.LibraryId,
                ["workspaceId"] = library.WorkspaceId,
                ["agentId"] = library.AgentId ?? string.Empty
            }));

        switch (sourceType.Trim().ToLowerInvariant())
        {
            case "library":
                if (!string.Equals(sourceId, libraryId, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("The requested library center does not match the scoped library.");
                await ProjectLibraryAsync(workspaceId, libraryId, nodes, edges, ct);
                break;
            case "book":
                await ProjectBookAsync(workspaceId, libraryId, sourceId, nodes, edges, evidence, ct);
                break;
            case "chapter":
                await ProjectChapterAsync(workspaceId, libraryId, sourceId, nodes, edges, evidence, ct);
                break;
            case "tree_node":
                await ProjectTreeNodeAsync(workspaceId, libraryId, sourceId, nodes, edges, ct);
                break;
            default:
                throw new ArgumentException("sourceType must be library, book, chapter, or tree_node.", nameof(sourceType));
        }

        return new MemoryGraphNeighborhoodDto(
            workspaceId,
            agentId,
            libraryId,
            sourceType.Trim().ToLowerInvariant(),
            sourceId,
            depth,
            nodes.Values.OrderBy(n => n.Type).ThenBy(n => n.Label, StringComparer.Ordinal).ToList(),
            edges.Values.OrderBy(e => e.Type).ThenBy(e => e.Label, StringComparer.Ordinal).ToList(),
            evidence.Values.OrderBy(e => e.SourceType).ThenBy(e => e.SourceId, StringComparer.Ordinal).ToList());
    }

    private async Task<LibraryRecord> ValidateLibraryAsync(
        string workspaceId,
        string agentId,
        string libraryId,
        CancellationToken ct)
    {
        var library = await _library.GetLibraryAsync(libraryId, ct)
            ?? throw new InvalidOperationException($"Library '{libraryId}' not found.");
        if (library.WorkspaceId != workspaceId || library.AgentId != agentId)
        {
            throw new UnauthorizedAccessException(
                $"Library '{libraryId}' does not belong to workspace '{workspaceId}' and agent '{agentId}'.");
        }

        return library;
    }

    private async Task ProjectLibraryAsync(
        string workspaceId,
        string libraryId,
        IDictionary<string, MemoryGraphNodeDto> nodes,
        IDictionary<string, MemoryGraphEdgeDto> edges,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var books = await db.Books.AsNoTracking()
            .Where(b => b.LibraryId == libraryId && b.Status == "active")
            .OrderBy(b => b.Title)
            .Take(40)
            .ToListAsync(ct);

        foreach (var book in books)
        {
            AddNode(nodes, BookNode(book.BookId, book.Title, book.Summary, book.Status));
            AddEdge(edges, $"contains:library:{libraryId}:book:{book.BookId}", $"library:{libraryId}", $"book:{book.BookId}", "contains", "包含", 1);
        }
    }

    private async Task ProjectTreeNodeAsync(
        string workspaceId,
        string libraryId,
        string nodeId,
        IDictionary<string, MemoryGraphNodeDto> nodes,
        IDictionary<string, MemoryGraphEdgeDto> edges,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var node = await db.MemoryTreeNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.WorkspaceId == workspaceId && n.LibraryId == libraryId && n.NodeId == nodeId, ct)
            ?? throw new InvalidOperationException($"Tree node '{nodeId}' not found.");

        AddNode(nodes, new MemoryGraphNodeDto(
            $"tree_node:{node.NodeId}",
            "tree_node",
            node.Name,
            node.Summary,
            node.Status,
            1,
            new Dictionary<string, string>
            {
                ["nodeId"] = node.NodeId,
                ["nodeType"] = node.NodeType,
                ["path"] = node.Path
            }));
        AddEdge(edges, $"contains:library:{libraryId}:tree_node:{node.NodeId}", $"library:{libraryId}", $"tree_node:{node.NodeId}", "contains", "包含", 1);

        var mounts = await db.BookTreeMounts.AsNoTracking()
            .Where(m => m.NodeId == node.NodeId)
            .OrderByDescending(m => m.Weight)
            .Take(40)
            .ToListAsync(ct);
        var bookIds = mounts.Select(m => m.BookId).ToHashSet(StringComparer.Ordinal);
        var books = await db.Books.AsNoTracking()
            .Where(b => bookIds.Contains(b.BookId) && b.LibraryId == libraryId && b.Status == "active")
            .ToListAsync(ct);

        foreach (var book in books)
        {
            AddNode(nodes, BookNode(book.BookId, book.Title, book.Summary, book.Status));
            AddEdge(edges, $"mount:{node.NodeId}:{book.BookId}", $"tree_node:{node.NodeId}", $"book:{book.BookId}", "mounts", "挂载", 1);
        }
    }

    private async Task ProjectBookAsync(
        string workspaceId,
        string libraryId,
        string bookId,
        IDictionary<string, MemoryGraphNodeDto> nodes,
        IDictionary<string, MemoryGraphEdgeDto> edges,
        IDictionary<string, MemoryGraphEvidenceDto> evidence,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var book = await db.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BookId == bookId && b.LibraryId == libraryId, ct)
            ?? throw new InvalidOperationException($"Book '{bookId}' not found.");

        AddNode(nodes, BookNode(book.BookId, book.Title, book.Summary, book.Status));
        AddEdge(edges, $"contains:library:{libraryId}:book:{book.BookId}", $"library:{libraryId}", $"book:{book.BookId}", "contains", "包含", 1);

        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.BookId == book.BookId)
            .OrderBy(c => c.ChapterOrder)
            .Take(40)
            .ToListAsync(ct);

        foreach (var chapter in chapters)
        {
            AddNode(nodes, ChapterNode(chapter.ChapterId, chapter.Title, chapter.ContentType, chapter.Importance));
            AddEdge(edges, $"contains:book:{book.BookId}:chapter:{chapter.ChapterId}", $"book:{book.BookId}", $"chapter:{chapter.ChapterId}", "contains", "包含", 1);
            await ProjectChapterSourcesAndPointersAsync(workspaceId, chapter.ChapterId, nodes, edges, evidence, ct);
        }
    }

    private async Task ProjectChapterAsync(
        string workspaceId,
        string libraryId,
        string chapterId,
        IDictionary<string, MemoryGraphNodeDto> nodes,
        IDictionary<string, MemoryGraphEdgeDto> edges,
        IDictionary<string, MemoryGraphEvidenceDto> evidence,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var chapter = await db.Chapters.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChapterId == chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter '{chapterId}' not found.");
        var book = await db.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BookId == chapter.BookId && b.LibraryId == libraryId, ct)
            ?? throw new UnauthorizedAccessException($"Chapter '{chapterId}' does not belong to library '{libraryId}'.");

        AddNode(nodes, BookNode(book.BookId, book.Title, book.Summary, book.Status));
        AddNode(nodes, ChapterNode(chapter.ChapterId, chapter.Title, chapter.ContentType, chapter.Importance));
        AddEdge(edges, $"contains:book:{book.BookId}:chapter:{chapter.ChapterId}", $"book:{book.BookId}", $"chapter:{chapter.ChapterId}", "contains", "包含", 1);
        await ProjectChapterSourcesAndPointersAsync(workspaceId, chapter.ChapterId, nodes, edges, evidence, ct);
    }

    private async Task ProjectChapterSourcesAndPointersAsync(
        string workspaceId,
        string chapterId,
        IDictionary<string, MemoryGraphNodeDto> nodes,
        IDictionary<string, MemoryGraphEdgeDto> edges,
        IDictionary<string, MemoryGraphEvidenceDto> evidence,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sources = await db.SourceReferences.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.OwnerType == "chapter" && s.OwnerId == chapterId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var source in sources)
        {
            var sourceNodeId = $"source:{source.TargetType}:{source.TargetId}";
            AddNode(nodes, new MemoryGraphNodeDto(
                sourceNodeId,
                "source",
                source.Label ?? $"{source.TargetType}:{source.TargetId}",
                source.Description,
                "active",
                1,
                new Dictionary<string, string>
                {
                    ["sourceReferenceId"] = source.SourceReferenceId,
                    ["targetType"] = source.TargetType,
                    ["targetId"] = source.TargetId,
                    ["targetRange"] = source.TargetRange ?? string.Empty
                }));
            AddEdge(edges, $"evidence:{chapterId}:{source.SourceReferenceId}", $"chapter:{chapterId}", sourceNodeId, "evidenced_by", "来源", 1);
            evidence[$"source:{source.SourceReferenceId}"] = new MemoryGraphEvidenceDto(
                source.SourceReferenceId,
                "chapter",
                chapterId,
                source.TargetType,
                source.TargetId,
                source.TargetRange,
                source.Label,
                source.Description);
        }

        var pointers = await db.Pointers.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.SourceType == "chapter" && p.SourceId == chapterId)
            .OrderByDescending(p => p.Relevance)
            .Take(20)
            .ToListAsync(ct);

        foreach (var pointer in pointers)
        {
            var targetNodeId = $"{pointer.TargetType}:{pointer.TargetId}";
            AddNode(nodes, new MemoryGraphNodeDto(
                targetNodeId,
                pointer.TargetType,
                pointer.TargetLabel ?? pointer.TargetId,
                pointer.Description,
                "active",
                pointer.Relevance / 10.0,
                new Dictionary<string, string>
                {
                    ["targetType"] = pointer.TargetType,
                    ["targetId"] = pointer.TargetId
                }));
            AddEdge(edges, $"pointer:{pointer.PointerId}", $"chapter:{chapterId}", targetNodeId, "pointer", pointer.TargetLabel ?? pointer.TargetType, pointer.Relevance / 10.0);
        }
    }

    private static MemoryGraphNodeDto BookNode(string bookId, string title, string? summary, string status)
        => new(
            $"book:{bookId}",
            "book",
            title,
            summary,
            status,
            1,
            new Dictionary<string, string> { ["bookId"] = bookId });

    private static MemoryGraphNodeDto ChapterNode(string chapterId, string title, string contentType, double importance)
        => new(
            $"chapter:{chapterId}",
            "chapter",
            title,
            contentType,
            "active",
            importance,
            new Dictionary<string, string> { ["chapterId"] = chapterId });

    private static void AddNode(
        IDictionary<string, MemoryGraphNodeDto> nodes,
        MemoryGraphNodeDto node)
    {
        nodes.TryAdd(node.Id, node);
    }

    private static void AddEdge(
        IDictionary<string, MemoryGraphEdgeDto> edges,
        string id,
        string sourceId,
        string targetId,
        string type,
        string label,
        double? confidence)
    {
        edges.TryAdd(id, new MemoryGraphEdgeDto(id, sourceId, targetId, type, label, confidence));
    }
}
```

- [ ] **Step 4: Register the service**

In `Source/PuddingAgent/Program.cs`, find the existing `IMemoryLibraryAdminService` registration and add:

```csharp
builder.Services.AddScoped<IMemoryGraphAdminService, MemoryGraphAdminService>();
```

- [ ] **Step 5: Run service tests to verify they pass**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --filter MemoryGraphAdminServiceTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 6: Commit Task 1**

Run:

```powershell
git add Source\PuddingPlatform\Services\MemoryGraphAdminService.cs Source\PuddingPlatformTests\Services\MemoryGraphAdminServiceTests.cs Source\PuddingAgent\Program.cs
git commit -m "feat: add memory graph projection service"
```

---

## Task 2: Backend Read-only Graph API

**Files:**
- Create: `Source/PuddingPlatform/Controllers/Api/MemoryGraphAdminController.cs`
- Create: `Source/PuddingWebApiTests/MemoryGraphAdminApiControllerTests.cs`

- [ ] **Step 1: Write failing controller tests**

Create `Source/PuddingWebApiTests/MemoryGraphAdminApiControllerTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Moq;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Services;

namespace PuddingWebApiTests;

[TestClass]
public sealed class MemoryGraphAdminApiControllerTests
{
    private readonly Mock<IMemoryGraphAdminService> _service = new();

    private MemoryGraphAdminController CreateController() => new(_service.Object);

    [TestMethod]
    public async Task GetNeighborhood_MissingAgentId_Returns400()
    {
        var controller = CreateController();

        var result = await controller.GetNeighborhood("ws-1", " ", "lib-1", "book", "book-1", 1, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetNeighborhood_InvalidDepth_Returns400()
    {
        var controller = CreateController();

        var result = await controller.GetNeighborhood("ws-1", "agent-1", "lib-1", "book", "book-1", 3, CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetNeighborhood_Unauthorized_Returns403()
    {
        _service.Setup(s => s.GetNeighborhoodAsync("ws-1", "agent-1", "lib-1", "book", "book-1", 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("forbidden"));
        var controller = CreateController();

        var result = await controller.GetNeighborhood("ws-1", "agent-1", "lib-1", "book", "book-1", 1, CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        Assert.IsNotNull(objectResult);
        Assert.AreEqual(403, objectResult.StatusCode);
    }

    [TestMethod]
    public async Task GetNeighborhood_ValidRequest_ReturnsOk()
    {
        var graph = new MemoryGraphNeighborhoodDto(
            "ws-1",
            "agent-1",
            "lib-1",
            "book",
            "book-1",
            1,
            Array.Empty<MemoryGraphNodeDto>(),
            Array.Empty<MemoryGraphEdgeDto>(),
            Array.Empty<MemoryGraphEvidenceDto>());
        _service.Setup(s => s.GetNeighborhoodAsync("ws-1", "agent-1", "lib-1", "book", "book-1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(graph);
        var controller = CreateController();

        var result = await controller.GetNeighborhood("ws-1", "agent-1", "lib-1", "book", "book-1", 1, CancellationToken.None);

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
    }
}
```

- [ ] **Step 2: Run controller tests to verify they fail**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore --filter MemoryGraphAdminApiControllerTests --logger "console;verbosity=minimal"
```

Expected: FAIL because `MemoryGraphAdminController` does not exist.

- [ ] **Step 3: Create the controller**

Create `Source/PuddingPlatform/Controllers/Api/MemoryGraphAdminController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>记忆知识图谱 Admin API。Phase 1 只读，不提供图谱写入。</summary>
[Authorize]
[ApiController]
[Route("api/admin/memory-graph")]
public sealed class MemoryGraphAdminController : ControllerBase
{
    private readonly IMemoryGraphAdminService _service;

    public MemoryGraphAdminController(IMemoryGraphAdminService service)
    {
        _service = service;
    }

    /// <summary>获取当前 Library 内某个对象的一跳或两跳局部图谱。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/neighborhood")]
    public async Task<ActionResult<MemoryGraphNeighborhoodDto>> GetNeighborhood(
        string workspaceId,
        string agentId,
        string libraryId,
        [FromQuery] string sourceType,
        [FromQuery] string sourceId,
        [FromQuery] int depth = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) ||
            string.IsNullOrWhiteSpace(agentId) ||
            string.IsNullOrWhiteSpace(libraryId))
        {
            return BadRequest(new { error = "workspaceId, agentId, and libraryId are required." });
        }

        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceId))
            return BadRequest(new { error = "sourceType and sourceId are required." });

        if (depth is < 1 or > 2)
            return BadRequest(new { error = "depth must be between 1 and 2." });

        try
        {
            var graph = await _service.GetNeighborhoodAsync(
                workspaceId,
                agentId,
                libraryId,
                sourceType,
                sourceId,
                depth,
                ct);
            return Ok(graph);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
```

- [ ] **Step 4: Run controller tests to verify they pass**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore --filter MemoryGraphAdminApiControllerTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 5: Commit Task 2**

Run:

```powershell
git add Source\PuddingPlatform\Controllers\Api\MemoryGraphAdminController.cs Source\PuddingWebApiTests\MemoryGraphAdminApiControllerTests.cs
git commit -m "feat: add memory graph admin api"
```

---

## Task 3: Frontend API Types and Graph Component

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/memory-library/types.ts`
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryGraphView.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/memory-library/styles.less`

- [ ] **Step 1: Add graph TypeScript DTOs**

Append to `Source/PuddingPlatformAdmin/src/pages/memory-library/types.ts`:

```ts
export interface MemoryGraphNodeDto {
  id: string;
  type: 'library' | 'tree_node' | 'book' | 'chapter' | 'source' | string;
  label: string;
  subtitle?: string;
  status: string;
  confidence?: number;
  metadata: Record<string, string>;
}

export interface MemoryGraphEdgeDto {
  id: string;
  sourceId: string;
  targetId: string;
  type: string;
  label: string;
  confidence?: number;
}

export interface MemoryGraphEvidenceDto {
  id: string;
  claimType: string;
  claimId: string;
  sourceType: string;
  sourceId: string;
  sourceRange?: string;
  label?: string;
  description?: string;
}

export interface MemoryGraphNeighborhoodDto {
  workspaceId: string;
  agentId: string;
  libraryId: string;
  centerType: string;
  centerId: string;
  depth: number;
  nodes: MemoryGraphNodeDto[];
  edges: MemoryGraphEdgeDto[];
  evidence: MemoryGraphEvidenceDto[];
}
```

- [ ] **Step 2: Add API client**

In `Source/PuddingPlatformAdmin/src/services/platform/api.ts`, add near the memory-library API functions:

```ts
export async function getAgentMemoryGraphNeighborhood(
  workspaceId: string,
  agentId: string,
  libraryId: string,
  sourceType: string,
  sourceId: string,
  depth = 1,
): Promise<{
  workspaceId: string;
  agentId: string;
  libraryId: string;
  centerType: string;
  centerId: string;
  depth: number;
  nodes: {
    id: string;
    type: string;
    label: string;
    subtitle?: string;
    status: string;
    confidence?: number;
    metadata: Record<string, string>;
  }[];
  edges: {
    id: string;
    sourceId: string;
    targetId: string;
    type: string;
    label: string;
    confidence?: number;
  }[];
  evidence: {
    id: string;
    claimType: string;
    claimId: string;
    sourceType: string;
    sourceId: string;
    sourceRange?: string;
    label?: string;
    description?: string;
  }[];
}> {
  return request(
    `/api/admin/memory-graph/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/libraries/${encodeURIComponent(libraryId)}/neighborhood`,
    {
      params: { sourceType, sourceId, depth },
    },
  );
}
```

- [ ] **Step 3: Create graph view component**

Create `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryGraphView.tsx`:

```tsx
import { Empty, List, Space, Spin, Tag, Typography } from 'antd';
import React from 'react';
import type { MemoryGraphNeighborhoodDto } from '../types';

const { Text } = Typography;

interface MemoryGraphViewProps {
  graph: MemoryGraphNeighborhoodDto | null;
  loading: boolean;
}

const nodeColor: Record<string, string> = {
  library: 'blue',
  tree_node: 'cyan',
  book: 'green',
  chapter: 'purple',
  source: 'gold',
};

const MemoryGraphView: React.FC<MemoryGraphViewProps> = ({ graph, loading }) => {
  if (loading) {
    return (
      <div className="memory-graph-state">
        <Spin />
      </div>
    );
  }

  if (!graph || graph.nodes.length === 0) {
    return (
      <div className="memory-graph-state">
        <Empty description="选择 Library、Book 或 Chapter 后查看局部图谱" />
      </div>
    );
  }

  return (
    <div className="memory-graph-view">
      <div className="memory-graph-canvas" aria-label="Memory graph neighborhood">
        {graph.nodes.map((node) => (
          <div key={node.id} className={`memory-graph-node memory-graph-node-${node.type}`}>
            <Space size={6} wrap>
              <Tag color={nodeColor[node.type] ?? 'default'}>{node.type}</Tag>
              {node.confidence !== undefined && (
                <Text type="secondary">{Math.round(node.confidence * 100)}%</Text>
              )}
            </Space>
            <Text strong ellipsis={{ tooltip: node.label }} className="memory-graph-node-title">
              {node.label}
            </Text>
            {node.subtitle && (
              <Text type="secondary" ellipsis={{ tooltip: node.subtitle }} className="memory-graph-node-subtitle">
                {node.subtitle}
              </Text>
            )}
          </div>
        ))}
      </div>

      <div className="memory-graph-side">
        <section>
          <Text strong>关系</Text>
          <List
            size="small"
            dataSource={graph.edges}
            locale={{ emptyText: '暂无关系' }}
            renderItem={(edge) => (
              <List.Item key={edge.id}>
                <Space direction="vertical" size={2}>
                  <Text>{edge.label}</Text>
                  <Text type="secondary">{edge.sourceId} -> {edge.targetId}</Text>
                </Space>
              </List.Item>
            )}
          />
        </section>

        <section>
          <Text strong>证据</Text>
          <List
            size="small"
            dataSource={graph.evidence}
            locale={{ emptyText: '暂无证据' }}
            renderItem={(item) => (
              <List.Item key={item.id}>
                <Space direction="vertical" size={2}>
                  <Text>{item.label ?? `${item.sourceType}:${item.sourceId}`}</Text>
                  {item.description && <Text type="secondary">{item.description}</Text>}
                  {item.sourceRange && <Text type="secondary">{item.sourceRange}</Text>}
                </Space>
              </List.Item>
            )}
          />
        </section>
      </div>
    </div>
  );
};

export default MemoryGraphView;
```

- [ ] **Step 4: Add graph styles**

Append to `Source/PuddingPlatformAdmin/src/pages/memory-library/styles.less`:

```less
.memory-graph-state {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 360px;
}

.memory-graph-view {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 320px;
  gap: 16px;
  min-height: 420px;
}

.memory-graph-canvas {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  align-content: start;
  gap: 12px;
  padding: 12px;
  border: 1px solid #ece7de;
  border-radius: 8px;
  background: #fbfaf7;
}

.memory-graph-node {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-height: 104px;
  padding: 10px;
  border: 1px solid #e5ded2;
  border-radius: 8px;
  background: #fff;
}

.memory-graph-node-title {
  display: block;
}

.memory-graph-node-subtitle {
  display: block;
}

.memory-graph-side {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
  padding: 12px;
  border: 1px solid #ece7de;
  border-radius: 8px;
  background: #fff;
}

@media (max-width: 960px) {
  .memory-graph-view {
    grid-template-columns: 1fr;
  }
}
```

- [ ] **Step 5: Run frontend lint for new files**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec biome lint src/pages/memory-library src/services/platform/api.ts
```

Expected: PASS.

- [ ] **Step 6: Commit Task 3**

Run:

```powershell
git add Source\PuddingPlatformAdmin\src\pages\memory-library\types.ts Source\PuddingPlatformAdmin\src\services\platform\api.ts Source\PuddingPlatformAdmin\src\pages\memory-library\components\MemoryGraphView.tsx Source\PuddingPlatformAdmin\src\pages\memory-library\styles.less
git commit -m "feat: add memory graph view component"
```

---

## Task 4: Integrate Graph View Into Memory Library Page

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx`

- [ ] **Step 1: Add imports**

In `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx`, update Ant Design and API imports:

```tsx
import { Select, Input, Button, Alert, Space, Typography, Modal, Form, Popconfirm, message, Segmented } from 'antd';
```

Add API import:

```tsx
getAgentMemoryGraphNeighborhood,
```

Add type import:

```tsx
MemoryGraphNeighborhoodDto,
```

Add component import:

```tsx
import MemoryGraphView from './components/MemoryGraphView';
```

- [ ] **Step 2: Add view and graph state**

Near the current `LibraryScope` type, add:

```tsx
type MemoryLibraryView = 'pages' | 'graph';
```

Inside `MemoryLibraryPage`, add state near existing tree/book state:

```tsx
const [activeView, setActiveView] = useState<MemoryLibraryView>('pages');
const [graph, setGraph] = useState<MemoryGraphNeighborhoodDto | null>(null);
const [graphLoading, setGraphLoading] = useState(false);
```

- [ ] **Step 3: Add graph center resolver**

Inside `MemoryLibraryPage`, before handlers, add:

```tsx
const resolveGraphCenter = useCallback((): { sourceType: string; sourceId: string } | null => {
  if (!selectedLibraryId) return null;
  if (selectedNode?.type === 'book_page' && selectedNode.bookId) {
    return { sourceType: 'book', sourceId: selectedNode.bookId };
  }
  if (selectedNode?.type === 'tree_node' || selectedNode?.type === 'system') {
    return { sourceType: 'tree_node', sourceId: selectedNode.id };
  }
  if (bookPage?.bookId) {
    return { sourceType: 'book', sourceId: bookPage.bookId };
  }
  return { sourceType: 'library', sourceId: selectedLibraryId };
}, [bookPage?.bookId, selectedLibraryId, selectedNode]);
```

- [ ] **Step 4: Load graph on graph view changes**

Add this effect after the existing tree loading effect:

```tsx
useEffect(() => {
  if (
    activeView !== 'graph' ||
    libraryScope !== 'agent' ||
    !selectedWorkspaceId ||
    !selectedAgentId ||
    !selectedLibraryId
  ) {
    setGraph(null);
    return;
  }

  const center = resolveGraphCenter();
  if (!center) {
    setGraph(null);
    return;
  }

  setGraphLoading(true);
  getAgentMemoryGraphNeighborhood(
    selectedWorkspaceId,
    selectedAgentId,
    selectedLibraryId,
    center.sourceType,
    center.sourceId,
    1,
  )
    .then(setGraph)
    .catch(() => {
      setGraph(null);
      message.error('无法加载记忆图谱');
    })
    .finally(() => setGraphLoading(false));
}, [
  activeView,
  libraryScope,
  selectedWorkspaceId,
  selectedAgentId,
  selectedLibraryId,
  resolveGraphCenter,
]);
```

- [ ] **Step 5: Reset graph when scope changes**

In `handleWorkspaceChange`, `handleAgentChange`, `handleLibraryChange`, `handleOpenLegacyLibrary`, and `handleReturnToAgentLibrary`, add:

```tsx
setActiveView('pages');
setGraph(null);
```

For `handleOpenLegacyLibrary`, keep `activeView` as `pages`; Phase 1 Graph view is agent-scoped only.

- [ ] **Step 6: Add segmented view control**

In the page toolbar area, place this next to the search and refresh controls:

```tsx
<Segmented
  value={activeView}
  onChange={(value) => setActiveView(value as MemoryLibraryView)}
  options={[
    { label: 'Pages', value: 'pages' },
    { label: 'Graph', value: 'graph', disabled: libraryScope === 'legacy' },
  ]}
/>
```

- [ ] **Step 7: Render graph in the center panel**

Replace the current center panel block:

```tsx
<div className="memory-page-editor-panel">
  {bookPage && !isReadOnlyLegacy && (
    <div style={{ marginBottom: 16, display: 'flex', gap: 8 }}>
      <Button size="small" icon={<EditOutlined />} onClick={() => {
        editBookForm.setFieldsValue({ title: bookPage.title, summary: bookPage.summary });
        setEditBookModalOpen(true);
      }}>编辑信息</Button>
      <Button size="small" icon={<PlusOutlined />} onClick={() => setNewChapterModalOpen(true)}>添加章节</Button>
      <Popconfirm title="归档后将不可见，确认归档？" onConfirm={handleArchiveBook}>
        <Button size="small" danger icon={<DeleteOutlined />}>归档 Book</Button>
      </Popconfirm>
    </div>
  )}
  <MemoryPageEditor
    loading={bookLoading}
    book={bookPage ?? undefined}
    nodeTitle={selectedNode?.title}
    nodeSummary={selectedNode?.summary}
    nodeType={selectedNode?.type}
    onArchiveChapter={isReadOnlyLegacy ? undefined : handleArchiveChapter}
  />
</div>
```

with this block:

```tsx
<div className="memory-page-editor-panel">
  {activeView === 'graph' ? (
    <MemoryGraphView graph={graph} loading={graphLoading} />
  ) : (
    <>
      {bookPage && !isReadOnlyLegacy && (
        <div style={{ marginBottom: 16, display: 'flex', gap: 8 }}>
          <Button size="small" icon={<EditOutlined />} onClick={() => {
            editBookForm.setFieldsValue({ title: bookPage.title, summary: bookPage.summary });
            setEditBookModalOpen(true);
          }}>编辑信息</Button>
          <Button size="small" icon={<PlusOutlined />} onClick={() => setNewChapterModalOpen(true)}>添加章节</Button>
          <Popconfirm title="归档后将不可见，确认归档？" onConfirm={handleArchiveBook}>
            <Button size="small" danger icon={<DeleteOutlined />}>归档 Book</Button>
          </Popconfirm>
        </div>
      )}
      <MemoryPageEditor
        loading={bookLoading}
        book={bookPage ?? undefined}
        nodeTitle={selectedNode?.title}
        nodeSummary={selectedNode?.summary}
        nodeType={selectedNode?.type}
        onArchiveChapter={isReadOnlyLegacy ? undefined : handleArchiveChapter}
      />
    </>
  )}
</div>
```

- [ ] **Step 8: Run frontend lint**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec biome lint src/pages/memory-library src/services/platform/api.ts
```

Expected: PASS.

- [ ] **Step 9: Commit Task 4**

Run:

```powershell
git add Source\PuddingPlatformAdmin\src\pages\memory-library\index.tsx
git commit -m "feat: integrate memory graph view"
```

---

## Task 5: Verification and Browser Smoke Test

**Files:**
- No required file changes.

- [ ] **Step 1: Run backend service tests**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --filter "MemoryGraphAdminServiceTests|MemoryLibraryAdminServiceTests" --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 2: Run backend API tests**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore --filter "MemoryGraphAdminApiControllerTests|MemoryLibraryAdminApiControllerTests" --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 3: Run frontend targeted lint**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec biome lint src/pages/memory-library src/services/platform/api.ts
```

Expected: PASS.

- [ ] **Step 4: Run TypeScript check and record known unrelated failures**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm tsc
```

Expected for the current dirty worktree: this may still fail on pre-existing unrelated type errors in shared admin components and agent-template tests. If it fails, record the first 10 errors and verify none reference `src/pages/memory-library` or the new memory graph API code.

- [ ] **Step 5: Browser smoke test**

With the app running, open:

```text
http://localhost/admin/memory-library
```

Manual checks:

- Select a workspace and agent with an agent-scoped memory library.
- Confirm `Pages` view still renders the tree and book editor.
- Switch to `Graph`.
- Confirm Graph view shows at least the library node and book nodes.
- Select a Book in the tree, switch to `Graph`, and confirm chapter/source/pointer nodes appear when data exists.
- Open a legacy workspace-only library and confirm the `Graph` option is disabled.

- [ ] **Step 6: Final diff review**

Run:

```powershell
git diff --stat
git diff --check -- Source/PuddingPlatform/Services/MemoryGraphAdminService.cs Source/PuddingPlatform/Controllers/Api/MemoryGraphAdminController.cs Source/PuddingPlatformTests/Services/MemoryGraphAdminServiceTests.cs Source/PuddingWebApiTests/MemoryGraphAdminApiControllerTests.cs Source/PuddingPlatformAdmin/src/pages/memory-library Source/PuddingPlatformAdmin/src/services/platform/api.ts Source/PuddingAgent/Program.cs
```

Expected: no whitespace errors. The stat should only include Memory Graph Phase 1 files plus the existing `Program.cs` service registration.

- [ ] **Step 7: Commit verification cleanup**

If verification required small fixes, commit them:

```powershell
git add Source/PuddingPlatform/Services/MemoryGraphAdminService.cs Source/PuddingPlatform/Controllers/Api/MemoryGraphAdminController.cs Source/PuddingPlatformTests/Services/MemoryGraphAdminServiceTests.cs Source/PuddingWebApiTests/MemoryGraphAdminApiControllerTests.cs Source/PuddingPlatformAdmin/src/pages/memory-library Source/PuddingPlatformAdmin/src/services/platform/api.ts Source/PuddingAgent/Program.cs
git commit -m "test: verify memory graph phase 1"
```

If no cleanup was needed, do not create an empty commit.

---

## Self-review Checklist

- Spec coverage:
  - ADR-047 Phase 1 read-only graph projection is covered by Tasks 1-4.
  - No new graph tables are introduced.
  - LLM Proposal workflow is not implemented in this phase.
  - Legacy workspace-only libraries remain read-only and Graph is disabled for them in the UI.
  - Runtime recall is not changed in this phase.

- Type consistency:
  - Backend DTO names use `MemoryGraph*Dto`.
  - Frontend DTO names mirror backend DTO names.
  - Endpoint path is `/api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/neighborhood`.
  - API client name is `getAgentMemoryGraphNeighborhood`.

- Verification:
  - Backend service tests cover projection and agent scope rejection.
  - Backend controller tests cover validation, 403, and success.
  - Frontend lint covers the new component, page integration, and API client.
  - Browser smoke validates the actual admin page.
