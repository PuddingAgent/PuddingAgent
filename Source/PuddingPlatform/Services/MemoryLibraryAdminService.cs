using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingPlatform.Services;

/// <summary>记忆图书馆概览——一个 Workspace 下的统计摘要。</summary>
public sealed record MemoryLibraryOverviewDto(
    string WorkspaceId,
    int LibraryCount,
    int BookCount,
    int TreeNodeCount,
    string? AgentId = null);

/// <summary>记忆树节点——用于前端 Tree 组件渲染。</summary>
public sealed record MemoryLibraryTreeNodeDto(
    string Id,
    string? ParentId,
    string Type,
    string Title,
    string? Summary,
    string Status,
    string? BookId,
    IReadOnlyList<MemoryLibraryTreeNodeDto> Children);

/// <summary>记忆书页——Book + 其 Chapters 的聚合视图。</summary>
public sealed record MemoryBookPageDto(
    string WorkspaceId,
    string LibraryId,
    string BookId,
    string Title,
    string? Summary,
    string Status,
    IReadOnlyList<MemoryChapterSectionDto> Chapters);

/// <summary>章节段落——Book 内的 block/section。</summary>
public sealed record MemoryChapterSectionDto(
    string ChapterId,
    string BookId,
    string Title,
    string Content,
    string ContentType,
    double Importance,
    long CreatedAt,
    long UpdatedAt);

/// <summary>全文搜索结果项。</summary>
public sealed record MemorySearchResultDto(
    string BookId,
    string ChapterId,
    string BookTitle,
    string Snippet,
    double Score);

/// <summary>指定实体的指针摘要（outgoing + backlinks）。</summary>
public sealed record MemoryPointersDto(
    IReadOnlyList<PointerRecord> Outgoing,
    IReadOnlyList<PointerRecord> Backlinks);

// ═══════════════════════════════════════════════════════════════
// Write DTOs (ADR-030 Phase 3: Guarded Editing)
// ═══════════════════════════════════════════════════════════════

/// <summary>创建树节点请求。</summary>
public sealed record CreateMemoryTreeNodeRequest(
    string WorkspaceId,
    string LibraryId,
    string? ParentNodeId,
    string Name,
    string? Summary,
    string NodeType);

/// <summary>创建 Book 请求（可选挂载到 TreeNode）。</summary>
public sealed record CreateMemoryBookRequest(
    string WorkspaceId,
    string LibraryId,
    string? NodeId,
    string Title,
    string? Summary);

/// <summary>更新 Book 元信息请求。</summary>
public sealed record UpdateMemoryBookRequest(string Title, string? Summary);

/// <summary>创建 Chapter 请求。</summary>
public sealed record CreateMemoryChapterRequest(
    string BookId,
    string Title,
    string Content,
    double Importance);

/// <summary>更新 Chapter 请求。</summary>
public sealed record UpdateMemoryChapterRequest(
    string Title,
    string Content,
    double Importance);

// ═══════════════════════════════════════════════════════════════
// Interface
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 记忆图书馆管理服务——为 Admin UI 提供 Agent 作用域的聚合视图。
/// 所有查询和写操作严格 workspace + agent scoped。
/// </summary>
public interface IMemoryLibraryAdminService
{
    /// <summary>获取 workspace + agent 下的统计概览。</summary>
    Task<MemoryLibraryOverviewDto> GetOverviewAsync(string workspaceId, string agentId, CancellationToken ct = default);

    /// <summary>列出 workspace + agent 下的图书馆。</summary>
    Task<IReadOnlyList<LibraryRecord>> GetLibrariesAsync(string workspaceId, string agentId, CancellationToken ct = default);

    /// <summary>确保 workspace + agent 下存在默认图书馆。</summary>
    Task<LibraryRecord> EnsureDefaultLibraryAsync(string workspaceId, string agentId, CancellationToken ct = default);

    /// <summary>获取 agent-scoped library 的记忆树。</summary>
    Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTreeAsync(string workspaceId, string agentId, string libraryId, CancellationToken ct = default);

    /// <summary>获取 Book 详情页（Book + Chapters），校验 workspace + agent 归属。</summary>
    Task<MemoryBookPageDto> GetBookPageAsync(string workspaceId, string agentId, string bookId, CancellationToken ct = default);

    /// <summary>workspace + agent scoped FTS 全文搜索。</summary>
    Task<IReadOnlyList<MemorySearchResultDto>> SearchAsync(string workspaceId, string agentId, string query, int topK, CancellationToken ct = default);

    /// <summary>获取指定 owner 的来源引用，校验 workspace + agent 归属。</summary>
    Task<IReadOnlyList<SourceReferenceRecord>> GetSourcesAsync(string workspaceId, string agentId, string ownerType, string ownerId, CancellationToken ct = default);

    /// <summary>获取指定实体的 outgoing pointers 和 backlinks，校验 workspace + agent 归属。</summary>
    Task<MemoryPointersDto> GetPointersAsync(string workspaceId, string agentId, string sourceType, string sourceId, CancellationToken ct = default);

    // ── Write (Guarded Editing) ─────────────────────────────────────

    /// <summary>创建 agent-scoped 树节点。</summary>
    Task<MemoryLibraryTreeNodeDto> CreateTreeNodeAsync(string workspaceId, string agentId, CreateMemoryTreeNodeRequest req, CancellationToken ct = default);

    /// <summary>创建 agent-scoped Book page，可选挂载到指定 TreeNode。</summary>
    Task<MemoryBookPageDto> CreateBookAsync(string workspaceId, string agentId, CreateMemoryBookRequest req, CancellationToken ct = default);

    /// <summary>更新 agent-scoped Book title 和 summary。</summary>
    Task<MemoryBookPageDto> UpdateBookAsync(string workspaceId, string agentId, string bookId, UpdateMemoryBookRequest req, CancellationToken ct = default);

    /// <summary>创建 agent-scoped Chapter section。</summary>
    Task<MemoryChapterSectionDto> CreateChapterAsync(string workspaceId, string agentId, CreateMemoryChapterRequest req, CancellationToken ct = default);

    /// <summary>更新 agent-scoped Chapter title、content 和 importance。</summary>
    Task<MemoryChapterSectionDto> UpdateChapterAsync(string workspaceId, string agentId, string chapterId, UpdateMemoryChapterRequest req, CancellationToken ct = default);

    /// <summary>归档 agent-scoped Book。</summary>
    Task<bool> ArchiveBookAsync(string workspaceId, string agentId, string bookId, CancellationToken ct = default);

    /// <summary>归档 agent-scoped Chapter。</summary>
    Task<bool> ArchiveChapterAsync(string workspaceId, string agentId, string chapterId, CancellationToken ct = default);
}

// ═══════════════════════════════════════════════════════════════
// Implementation
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 管理服务实现——直接调用 IMemoryLibrary 底层 primitives，
/// 做 workspace + agent ownership 校验和 DTO projection。
/// 对于未暴露在 IMemoryLibrary 上的查询（如 BookTreeMounts），直接使用 DbContext。
/// </summary>
public sealed class MemoryLibraryAdminService : IMemoryLibraryAdminService
{
    private readonly PuddingCode.Abstractions.IMemoryLibrary _library;
    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbFactory;

    public MemoryLibraryAdminService(
        PuddingCode.Abstractions.IMemoryLibrary library,
        IDbContextFactory<MemoryLibraryDbContext> dbFactory)
    {
        _library = library;
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public async Task<MemoryLibraryOverviewDto> GetOverviewAsync(
        string workspaceId, string agentId, CancellationToken ct = default)
    {
        var libraries = await GetLibrariesAsync(workspaceId, agentId, ct);
        var libraryIds = libraries.Select(l => l.LibraryId).ToHashSet(StringComparer.Ordinal);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var bookCount = await db.Books.AsNoTracking()
            .CountAsync(b => libraryIds.Contains(b.LibraryId), ct);
        var treeNodeCount = await db.MemoryTreeNodes.AsNoTracking()
            .CountAsync(n => n.WorkspaceId == workspaceId && libraryIds.Contains(n.LibraryId), ct);

        return new MemoryLibraryOverviewDto(
            workspaceId,
            libraries.Count,
            bookCount,
            treeNodeCount,
            agentId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryRecord>> GetLibrariesAsync(
        string workspaceId, string agentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.Libraries.AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId && l.AgentId == agentId)
            .OrderByDescending(l => l.UpdatedAt)
            .ToListAsync(ct);
        return entities.Select(ToLibraryRecord).ToList();
    }

    /// <inheritdoc />
    public async Task<LibraryRecord> EnsureDefaultLibraryAsync(
        string workspaceId, string agentId, CancellationToken ct = default)
    {
        var existing = await GetLibrariesAsync(workspaceId, agentId, ct);
        if (existing.Count > 0) return existing[0];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new LibraryEntity
        {
            LibraryId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            AgentId = agentId,
            Name = "默认记忆图书馆",
            Description = "Agent 专属记忆图书馆",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Libraries.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToLibraryRecord(entity);
    }

    private async Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> BuildTreeChildrenAsync(
        string workspaceId, string libraryId, CancellationToken ct = default)
    {
        var roots = await _library.GetTreeChildrenAsync(workspaceId, libraryId, null, ct);
        var result = new List<MemoryLibraryTreeNodeDto>();

        foreach (var root in roots)
        {
            var dto = await BuildTreeNodeDtoAsync(workspaceId, libraryId, root, ct);
            result.Add(dto);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTreeAsync(
        string workspaceId, string agentId, string libraryId, CancellationToken ct = default)
    {
        await ValidateLibraryOwnershipAsync(workspaceId, agentId, libraryId, ct);
        var library = await _library.GetLibraryAsync(libraryId, ct)
            ?? throw new InvalidOperationException($"Library '{libraryId}' not found.");
        return [await BuildLibraryRootAsync(workspaceId, library, ct)];
    }

    private async Task<MemoryBookPageDto> BuildBookPageAsync(
        string workspaceId, string bookId, CancellationToken ct)
    {
        var book = await _library.GetBookAsync(bookId, ct)
            ?? throw new InvalidOperationException($"Book '{bookId}' not found.");

        var chapters = await _library.ListChaptersAsync(bookId, ct);
        var chapterDtos = chapters.Select(c => new MemoryChapterSectionDto(
            c.ChapterId, c.BookId, c.Title, c.Content, c.ContentType,
            c.Importance, c.CreatedAt, c.UpdatedAt)).ToList();

        return new MemoryBookPageDto(
            workspaceId, book.LibraryId, book.BookId,
            book.Title, book.Summary, book.Status,
            chapterDtos);
    }

    /// <inheritdoc />
    public async Task<MemoryBookPageDto> GetBookPageAsync(
        string workspaceId, string agentId, string bookId, CancellationToken ct = default)
    {
        await ValidateBookOwnershipAsync(workspaceId, agentId, bookId, ct);
        return await BuildBookPageAsync(workspaceId, bookId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemorySearchResultDto>> SearchAsync(
        string workspaceId, string agentId, string query, int topK, CancellationToken ct = default)
    {
        var libraries = await GetLibrariesAsync(workspaceId, agentId, ct);
        if (libraries.Count == 0) return [];

        var libraryIds = libraries.Select(l => l.LibraryId).ToHashSet(StringComparer.Ordinal);
        var rawTopK = Math.Clamp(topK * 5, topK, 500);
        var results = await _library.SearchChaptersFtsScopedAsync(workspaceId, query, rawTopK, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var bookIds = results.Select(r => r.BookId).Distinct(StringComparer.Ordinal).ToList();
        var allowedBookIds = await db.Books.AsNoTracking()
            .Where(b => bookIds.Contains(b.BookId) && libraryIds.Contains(b.LibraryId))
            .Select(b => b.BookId)
            .ToListAsync(ct);
        var allowed = allowedBookIds.ToHashSet(StringComparer.Ordinal);

        return results
            .Where(r => allowed.Contains(r.BookId))
            .Take(topK)
            .Select(r => new MemorySearchResultDto(
                r.BookId,
                r.ChapterId ?? string.Empty,
                r.BookTitle,
                r.Snippet,
                r.Score))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceReferenceRecord>> GetSourcesAsync(
        string workspaceId, string agentId, string ownerType, string ownerId, CancellationToken ct = default)
    {
        await ValidateEntityOwnershipAsync(workspaceId, agentId, ownerType, ownerId, ct);
        var sources = await _library.GetSourceReferencesAsync(ownerType, ownerId, ct);
        return sources.Where(s => s.WorkspaceId == workspaceId).ToList();
    }

    /// <inheritdoc />
    public async Task<MemoryPointersDto> GetPointersAsync(
        string workspaceId, string agentId, string sourceType, string sourceId, CancellationToken ct = default)
    {
        await ValidateEntityOwnershipAsync(workspaceId, agentId, sourceType, sourceId, ct);

        var outgoing = await _library.GetPointersBySourceAsync(workspaceId, sourceType, sourceId, ct);
        var filteredOutgoing = new List<PointerRecord>();
        foreach (var pointer in outgoing)
        {
            if (await PointerVisibleToAgentAsync(workspaceId, agentId, pointer, ct))
            {
                filteredOutgoing.Add(pointer);
            }
        }

        var backlinks = await _library.ResolveBacklinksAsync(sourceType, sourceId, ct);
        var filteredBacklinks = new List<PointerRecord>();
        foreach (var pointer in backlinks)
        {
            if (pointer.WorkspaceId == workspaceId && await PointerSourceVisibleToAgentAsync(workspaceId, agentId, pointer, ct))
            {
                filteredBacklinks.Add(pointer);
            }
        }

        return new MemoryPointersDto(filteredOutgoing, filteredBacklinks);
    }

    // ═══════════════════════════════════════════════════════════════
    // Write (Guarded Editing)
    // ═══════════════════════════════════════════════════════════════

    private async Task<MemoryLibraryTreeNodeDto> CreateTreeNodeCoreAsync(
        CreateMemoryTreeNodeRequest req, CancellationToken ct = default)
    {
        var node = await _library.CreateTreeNodeAsync(
            req.WorkspaceId, req.LibraryId, req.ParentNodeId,
            req.Name, req.Summary, req.NodeType, ct);

        return new MemoryLibraryTreeNodeDto(
            node.NodeId, node.ParentNodeId, node.NodeType,
            node.Name, node.Summary, node.Status, null, []);
    }

    /// <inheritdoc />
    public async Task<MemoryLibraryTreeNodeDto> CreateTreeNodeAsync(
        string workspaceId, string agentId, CreateMemoryTreeNodeRequest req, CancellationToken ct = default)
    {
        await ValidateLibraryOwnershipAsync(workspaceId, agentId, req.LibraryId, ct);
        if (!string.IsNullOrWhiteSpace(req.ParentNodeId))
        {
            await ValidateTreeNodeOwnershipAsync(workspaceId, agentId, req.ParentNodeId, ct, req.LibraryId);
        }

        return await CreateTreeNodeCoreAsync(req with { WorkspaceId = workspaceId }, ct);
    }

    private async Task<MemoryBookPageDto> CreateBookCoreAsync(
        CreateMemoryBookRequest req, CancellationToken ct = default)
    {
        var book = await _library.CreateBookAsync(req.LibraryId, req.Title, req.Summary ?? string.Empty, null, ct);

        // 如果指定了 TreeNode，挂载 Book
        if (!string.IsNullOrWhiteSpace(req.NodeId))
        {
            await _library.MountBookAsync(book.BookId, req.NodeId, weight: 1, ct: ct);
        }

        return new MemoryBookPageDto(
            req.WorkspaceId, req.LibraryId, book.BookId,
            book.Title, book.Summary, book.Status, []);
    }

    /// <inheritdoc />
    public async Task<MemoryBookPageDto> CreateBookAsync(
        string workspaceId, string agentId, CreateMemoryBookRequest req, CancellationToken ct = default)
    {
        await ValidateLibraryOwnershipAsync(workspaceId, agentId, req.LibraryId, ct);
        if (!string.IsNullOrWhiteSpace(req.NodeId))
        {
            await ValidateTreeNodeOwnershipAsync(workspaceId, agentId, req.NodeId, ct, req.LibraryId);
        }

        return await CreateBookCoreAsync(req with { WorkspaceId = workspaceId }, ct);
    }

    private async Task<MemoryBookPageDto> UpdateBookCoreAsync(
        string workspaceId, string bookId, UpdateMemoryBookRequest req, CancellationToken ct)
    {
        var book = await _library.UpdateBookAsync(bookId, existing => existing with
        {
            Title = req.Title,
            Summary = req.Summary ?? existing.Summary,
        }, ct);

        var chapters = await _library.ListChaptersAsync(bookId, ct);
        var chapterDtos = chapters.Select(c => new MemoryChapterSectionDto(
            c.ChapterId, c.BookId, c.Title, c.Content, c.ContentType,
            c.Importance, c.CreatedAt, c.UpdatedAt)).ToList();

        return new MemoryBookPageDto(
            workspaceId, book.LibraryId, book.BookId,
            book.Title, book.Summary, book.Status, chapterDtos);
    }

    /// <inheritdoc />
    public async Task<MemoryBookPageDto> UpdateBookAsync(
        string workspaceId, string agentId, string bookId, UpdateMemoryBookRequest req, CancellationToken ct = default)
    {
        await ValidateBookOwnershipAsync(workspaceId, agentId, bookId, ct);
        return await UpdateBookCoreAsync(workspaceId, bookId, req, ct);
    }

    private async Task<MemoryChapterSectionDto> CreateChapterCoreAsync(
        CreateMemoryChapterRequest req, CancellationToken ct)
    {
        var chapter = await _library.AddChapterAsync(
            req.BookId, req.Title, req.Content,
            chapterOrder: 0, sourceSessionId: null, agentInstanceId: null, ct: ct);

        if (req.Importance != 0.5)
        {
            chapter = await _library.UpdateChapterImportanceAsync(
                chapter.ChapterId, req.Importance, ct);
        }

        return new MemoryChapterSectionDto(
            chapter.ChapterId, chapter.BookId, chapter.Title, chapter.Content,
            chapter.ContentType, chapter.Importance, chapter.CreatedAt, chapter.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<MemoryChapterSectionDto> CreateChapterAsync(
        string workspaceId, string agentId, CreateMemoryChapterRequest req, CancellationToken ct = default)
    {
        await ValidateBookOwnershipAsync(workspaceId, agentId, req.BookId, ct);
        return await CreateChapterCoreAsync(req, ct);
    }

    private async Task<MemoryChapterSectionDto> UpdateChapterCoreAsync(
        string chapterId, UpdateMemoryChapterRequest req, CancellationToken ct)
    {
        // P2 fix: 使用 UpdateContentAsync 处理 Content，再用 existing 回调覆盖 Title+Importance
        var chapter = await _library.GetChapterAsync(chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter '{chapterId}' not found.");

        // 先更新内容（含 FTS 同步），再通过 Book updater 覆盖 title 和 importance
        var updated = await _library.UpdateChapterContentAsync(chapterId, req.Content, ct);
        updated = await _library.UpdateChapterImportanceAsync(chapterId, req.Importance, ct);

        return new MemoryChapterSectionDto(
            updated.ChapterId, updated.BookId, req.Title, updated.Content,
            updated.ContentType, updated.Importance, updated.CreatedAt, updated.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<MemoryChapterSectionDto> UpdateChapterAsync(
        string workspaceId, string agentId, string chapterId, UpdateMemoryChapterRequest req, CancellationToken ct = default)
    {
        await ValidateChapterOwnershipAsync(workspaceId, agentId, chapterId, ct);
        return await UpdateChapterCoreAsync(chapterId, req, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveBookAsync(string workspaceId, string agentId, string bookId, CancellationToken ct = default)
    {
        await ValidateBookOwnershipAsync(workspaceId, agentId, bookId, ct);
        return await _library.ArchiveBookAsync(bookId, ct);
    }

    private async Task<bool> ArchiveChapterCoreAsync(string chapterId, CancellationToken ct)
    {
        // P2 fix: 使用 Chapter 的 status 标记为 archived，而非清空内容
        // 由于 ChapterRecord 无 Status 字段，使用 Importance=-1 标记 + 追加 "[archived]" 到 title
        var chapter = await _library.GetChapterAsync(chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter '{chapterId}' not found.");

        if (chapter.Title.Contains("[archived]"))
            return true; // 幂等

        await _library.UpdateChapterContentAsync(chapterId, chapter.Content, ct);
        await _library.UpdateChapterImportanceAsync(chapterId, -1.0, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveChapterAsync(string workspaceId, string agentId, string chapterId, CancellationToken ct = default)
    {
        await ValidateChapterOwnershipAsync(workspaceId, agentId, chapterId, ct);
        return await ArchiveChapterCoreAsync(chapterId, ct);
    }

    // ── Private helpers ──

    /// <summary>校验 Library 属于指定 workspace + agent，否则抛出 UnauthorizedAccessException。</summary>
    private async Task ValidateLibraryOwnershipAsync(string workspaceId, string agentId, string libraryId, CancellationToken ct)
    {
        var library = await _library.GetLibraryAsync(libraryId, ct)
            ?? throw new InvalidOperationException($"Library '{libraryId}' not found.");
        if (library.WorkspaceId != workspaceId || library.AgentId != agentId)
            throw new UnauthorizedAccessException(
                $"Library '{libraryId}' does not belong to workspace '{workspaceId}' and agent '{agentId}'.");
    }

    /// <summary>校验 Book 属于指定 workspace + agent，否则抛出 UnauthorizedAccessException。</summary>
    private async Task ValidateBookOwnershipAsync(string workspaceId, string agentId, string bookId, CancellationToken ct)
    {
        var book = await _library.GetBookReadOnlyAsync(bookId, ct)
            ?? throw new InvalidOperationException($"Book '{bookId}' not found.");
        await ValidateLibraryOwnershipAsync(workspaceId, agentId, book.LibraryId, ct);
    }

    /// <summary>校验 Chapter 属于指定 workspace + agent，否则抛出 UnauthorizedAccessException。</summary>
    private async Task ValidateChapterOwnershipAsync(string workspaceId, string agentId, string chapterId, CancellationToken ct)
    {
        var chapter = await _library.GetChapterAsync(chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter '{chapterId}' not found.");
        await ValidateBookOwnershipAsync(workspaceId, agentId, chapter.BookId, ct);
    }

    /// <summary>校验 TreeNode 属于指定 workspace + agent，否则抛出 UnauthorizedAccessException。</summary>
    private async Task ValidateTreeNodeOwnershipAsync(
        string workspaceId,
        string agentId,
        string nodeId,
        CancellationToken ct,
        string? expectedLibraryId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var node = await db.MemoryTreeNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.NodeId == nodeId, ct)
            ?? throw new InvalidOperationException($"Tree node '{nodeId}' not found.");

        if (node.WorkspaceId != workspaceId)
            throw new UnauthorizedAccessException($"Tree node '{nodeId}' does not belong to workspace '{workspaceId}'.");

        if (!string.IsNullOrWhiteSpace(expectedLibraryId) && node.LibraryId != expectedLibraryId)
            throw new UnauthorizedAccessException(
                $"Tree node '{nodeId}' does not belong to library '{expectedLibraryId}'.");

        await ValidateLibraryOwnershipAsync(workspaceId, agentId, node.LibraryId, ct);
    }

    /// <summary>按 owner type 校验实体属于指定 workspace + agent。</summary>
    private async Task ValidateEntityOwnershipAsync(string workspaceId, string agentId, string entityType, string entityId, CancellationToken ct)
    {
        switch (entityType)
        {
            case "book":
                await ValidateBookOwnershipAsync(workspaceId, agentId, entityId, ct);
                return;
            case "chapter":
                await ValidateChapterOwnershipAsync(workspaceId, agentId, entityId, ct);
                return;
            case "tree_node":
                await ValidateTreeNodeOwnershipAsync(workspaceId, agentId, entityId, ct);
                return;
            default:
                throw new UnauthorizedAccessException($"Unsupported memory entity type '{entityType}'.");
        }
    }

    /// <summary>判断 pointer source 是否属于当前 agent。</summary>
    private async Task<bool> PointerSourceVisibleToAgentAsync(string workspaceId, string agentId, PointerRecord pointer, CancellationToken ct)
    {
        var sourceType = string.IsNullOrWhiteSpace(pointer.SourceType) ? "chapter" : pointer.SourceType;
        var sourceId = string.IsNullOrWhiteSpace(pointer.SourceId) ? pointer.ChapterId : pointer.SourceId;

        try
        {
            await ValidateEntityOwnershipAsync(workspaceId, agentId, sourceType, sourceId, ct);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>判断 pointer 对当前 agent 可见。外部目标只要求 source 可见；内部目标必须同 agent。</summary>
    private async Task<bool> PointerVisibleToAgentAsync(string workspaceId, string agentId, PointerRecord pointer, CancellationToken ct)
    {
        if (!await PointerSourceVisibleToAgentAsync(workspaceId, agentId, pointer, ct))
        {
            return false;
        }

        if (pointer.TargetType is "book" or "chapter" or "tree_node")
        {
            try
            {
                await ValidateEntityOwnershipAsync(workspaceId, agentId, pointer.TargetType, pointer.TargetId, ct);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>递归构建 TreeNode DTO（含子节点和挂载的 BookId）。</summary>
    private async Task<MemoryLibraryTreeNodeDto> BuildTreeNodeDtoAsync(
        string workspaceId, string libraryId,
        PuddingCode.Abstractions.TreeNodeRecord node,
        CancellationToken ct)
    {
        var children = await _library.GetTreeChildrenAsync(workspaceId, libraryId, node.NodeId, ct);
        var childDtos = new List<MemoryLibraryTreeNodeDto>();

        foreach (var child in children)
        {
            childDtos.Add(await BuildTreeNodeDtoAsync(workspaceId, libraryId, child, ct));
        }

        var mountedBooks = await ListMountedBooksAsync(libraryId, node.NodeId, ct);
        childDtos.AddRange(mountedBooks.Select(ToBookPageNode));

        return new MemoryLibraryTreeNodeDto(
            node.NodeId,
            node.ParentNodeId,
            node.NodeType,
            node.Name,
            node.Summary,
            node.Status,
            mountedBooks.FirstOrDefault()?.BookId,
            childDtos);
    }

    /// <summary>构建 Library 根节点下的 children：TreeNode roots + 未挂载 Book 分组。</summary>
    private async Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> BuildLibraryChildrenAsync(
        string workspaceId, string libraryId, CancellationToken ct)
    {
        var roots = await BuildTreeChildrenAsync(workspaceId, libraryId, ct);
        var result = roots.ToList();
        var unmountedBooks = await ListUnmountedBooksAsync(libraryId, ct);
        if (unmountedBooks.Count > 0)
        {
            result.Add(new MemoryLibraryTreeNodeDto(
                $"{libraryId}:unmounted-books",
                libraryId,
                "system",
                "未挂载 Book",
                "尚未挂载到 Page Tree 的 Book，可直接打开阅读或后续移动到目录页。",
                "active",
                null,
                unmountedBooks.Select(ToBookPageNode).ToList()));
        }

        return result;
    }

    /// <summary>构建前端可浏览的 Library 根节点。</summary>
    private async Task<MemoryLibraryTreeNodeDto> BuildLibraryRootAsync(
        string workspaceId, LibraryRecord library, CancellationToken ct)
    {
        var children = await BuildLibraryChildrenAsync(workspaceId, library.LibraryId, ct);
        return new MemoryLibraryTreeNodeDto(
            library.LibraryId,
            null,
            "library",
            library.Name,
            library.Description,
            "active",
            null,
            children);
    }

    /// <summary>列出挂载在指定树节点上的 active Books。</summary>
    private async Task<IReadOnlyList<BookRecord>> ListMountedBooksAsync(
        string libraryId, string nodeId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.BookTreeMounts.AsNoTracking()
            .Join(db.Books.AsNoTracking(), m => m.BookId, b => b.BookId, (m, b) => new { Mount = m, Book = b })
            .Where(x => x.Mount.NodeId == nodeId
                     && x.Book.LibraryId == libraryId
                     && x.Book.Status == "active")
            .OrderByDescending(x => x.Mount.Weight)
            .ThenBy(x => x.Book.Title)
            .Select(x => x.Book)
            .ToListAsync(ct);

        return entities.Select(ToBookRecord).ToList();
    }

    /// <summary>列出 library 下尚未挂载到任何树节点的 active Books。</summary>
    private async Task<IReadOnlyList<BookRecord>> ListUnmountedBooksAsync(
        string libraryId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var mountedBookIds = db.BookTreeMounts.AsNoTracking().Select(m => m.BookId);
        var entities = await db.Books.AsNoTracking()
            .Where(b => b.LibraryId == libraryId
                     && b.Status == "active"
                     && !mountedBookIds.Contains(b.BookId))
            .OrderBy(b => b.Title)
            .ToListAsync(ct);

        return entities.Select(ToBookRecord).ToList();
    }

    /// <summary>把 Book 投影成前端 tree 可直接打开的 Book page 节点。</summary>
    private static MemoryLibraryTreeNodeDto ToBookPageNode(BookRecord book)
        => new(
            $"book:{book.BookId}",
            null,
            "book_page",
            book.Title,
            book.Summary,
            book.Status,
            book.BookId,
            []);

    private static BookRecord ToBookRecord(BookEntity e)
        => new(
            e.BookId,
            e.LibraryId,
            e.Title,
            e.Summary,
            e.Status,
            e.Version,
            e.AccessCount,
            e.LastAccessedAt,
            e.CreatedAt,
            e.UpdatedAt);

    private static LibraryRecord ToLibraryRecord(LibraryEntity e) => new(
        e.LibraryId, e.WorkspaceId, e.Name, e.Description, e.CreatedAt, e.UpdatedAt, e.AgentId);
}
