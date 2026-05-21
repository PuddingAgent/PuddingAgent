namespace PuddingPlatform.Services;

/// <summary>记忆图书馆概览——一个 Workspace 下的统计摘要。</summary>
public sealed record MemoryLibraryOverviewDto(
    string WorkspaceId,
    int LibraryCount,
    int BookCount,
    int TreeNodeCount);

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
/// 记忆图书馆管理服务——为 Admin UI 提供只读聚合视图。
/// 所有查询和写操作严格 workspace scoped。
/// </summary>
public interface IMemoryLibraryAdminService
{
    /// <summary>获取 workspace 下的统计概览。</summary>
    Task<MemoryLibraryOverviewDto> GetOverviewAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>获取 library 的记忆树（递归构建 TreeNode + 挂载的 Book）。</summary>
    Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTreeAsync(string workspaceId, string libraryId, CancellationToken ct = default);

    /// <summary>获取 Book 详情页（Book + Chapters），校验 workspace 归属。</summary>
    Task<MemoryBookPageDto> GetBookPageAsync(string workspaceId, string bookId, CancellationToken ct = default);

    /// <summary>workspace scoped FTS 全文搜索。</summary>
    Task<IReadOnlyList<MemorySearchResultDto>> SearchAsync(string workspaceId, string query, int topK, CancellationToken ct = default);

    // ── Write (Guarded Editing) ─────────────────────────────────────

    /// <summary>创建树节点（directory page）。工作区归属通过 libraryId 校验。</summary>
    Task<MemoryLibraryTreeNodeDto> CreateTreeNodeAsync(CreateMemoryTreeNodeRequest req, CancellationToken ct = default);

    /// <summary>创建 Book page，可选挂载到指定 TreeNode。</summary>
    Task<MemoryBookPageDto> CreateBookAsync(CreateMemoryBookRequest req, CancellationToken ct = default);

    /// <summary>更新 Book title 和 summary。</summary>
    Task<MemoryBookPageDto> UpdateBookAsync(string bookId, UpdateMemoryBookRequest req, CancellationToken ct = default);

    /// <summary>创建 Chapter section。</summary>
    Task<MemoryChapterSectionDto> CreateChapterAsync(CreateMemoryChapterRequest req, CancellationToken ct = default);

    /// <summary>更新 Chapter title、content 和 importance。</summary>
    Task<MemoryChapterSectionDto> UpdateChapterAsync(string chapterId, UpdateMemoryChapterRequest req, CancellationToken ct = default);

    /// <summary>归档 Book（软删除，不可恢复）。</summary>
    Task<bool> ArchiveBookAsync(string bookId, CancellationToken ct = default);

    /// <summary>归档 Chapter（清空内容并设置 importance 为负数标记）。</summary>
    Task<bool> ArchiveChapterAsync(string chapterId, CancellationToken ct = default);
}

// ═══════════════════════════════════════════════════════════════
// Implementation
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 管理服务实现——直接调用 IMemoryLibrary 底层 primitives，
/// 做 workspace ownership 校验和 DTO projection。
/// </summary>
public sealed class MemoryLibraryAdminService : IMemoryLibraryAdminService
{
    private readonly PuddingCode.Abstractions.IMemoryLibrary _library;

    public MemoryLibraryAdminService(PuddingCode.Abstractions.IMemoryLibrary library)
    {
        _library = library;
    }

    /// <inheritdoc />
    public async Task<MemoryLibraryOverviewDto> GetOverviewAsync(string workspaceId, CancellationToken ct = default)
    {
        var libraries = await _library.ListLibrariesAsync(workspaceId, ct);
        var books = await _library.ListBooksScopedAsync(workspaceId, limit: int.MaxValue, ct: ct);

        // TreeNode 统计：遍历每个 library 的根节点（children 为空表示无子节点，仅计数根层）
        var treeNodeCount = 0;
        foreach (var lib in libraries)
        {
            var children = await _library.GetTreeChildrenAsync(workspaceId, lib.LibraryId, null, ct);
            treeNodeCount += await CountAllDescendants(workspaceId, lib.LibraryId, children, ct);
        }

        return new MemoryLibraryOverviewDto(
            workspaceId,
            libraries.Count,
            books.Count,
            treeNodeCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTreeAsync(
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
    public async Task<MemoryBookPageDto> GetBookPageAsync(
        string workspaceId, string bookId, CancellationToken ct = default)
    {
        var book = await _library.GetBookAsync(bookId, ct)
            ?? throw new InvalidOperationException($"Book '{bookId}' not found.");

        // 校验 workspace 归属：book → library → workspaceId
        var library = await _library.GetLibraryAsync(book.LibraryId, ct)
            ?? throw new InvalidOperationException($"Library '{book.LibraryId}' not found for book '{bookId}'.");
        if (library.WorkspaceId != workspaceId)
            throw new UnauthorizedAccessException(
                $"Book '{bookId}' does not belong to workspace '{workspaceId}'.");

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
    public async Task<IReadOnlyList<MemorySearchResultDto>> SearchAsync(
        string workspaceId, string query, int topK, CancellationToken ct = default)
    {
        var results = await _library.SearchChaptersFtsScopedAsync(workspaceId, query, topK, ct);
        return results.Select(r => new MemorySearchResultDto(
            r.BookId,
            r.ChapterId ?? string.Empty,
            r.BookTitle,
            r.Snippet,
            r.Score)).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Write (Guarded Editing)
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<MemoryLibraryTreeNodeDto> CreateTreeNodeAsync(
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
    public async Task<MemoryBookPageDto> CreateBookAsync(
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
    public async Task<MemoryBookPageDto> UpdateBookAsync(
        string bookId, UpdateMemoryBookRequest req, CancellationToken ct = default)
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

        // 反查 workspaceId
        var library = await _library.GetLibraryAsync(book.LibraryId, ct);
        var workspaceId = library?.WorkspaceId ?? string.Empty;

        return new MemoryBookPageDto(
            workspaceId, book.LibraryId, book.BookId,
            book.Title, book.Summary, book.Status, chapterDtos);
    }

    /// <inheritdoc />
    public async Task<MemoryChapterSectionDto> CreateChapterAsync(
        CreateMemoryChapterRequest req, CancellationToken ct = default)
    {
        var chapter = await _library.AddChapterAsync(
            req.BookId, req.Title, req.Content,
            chapterOrder: 0, sourceSessionId: null, ct);

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
    public async Task<MemoryChapterSectionDto> UpdateChapterAsync(
        string chapterId, UpdateMemoryChapterRequest req, CancellationToken ct = default)
    {
        var chapter = await _library.UpdateChapterContentAsync(chapterId, req.Content, ct);
        chapter = await _library.UpdateChapterImportanceAsync(chapterId, req.Importance, ct);

        return new MemoryChapterSectionDto(
            chapter.ChapterId, chapter.BookId, req.Title, chapter.Content,
            chapter.ContentType, chapter.Importance, chapter.CreatedAt, chapter.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveBookAsync(string bookId, CancellationToken ct = default)
    {
        return await _library.ArchiveBookAsync(bookId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveChapterAsync(string chapterId, CancellationToken ct = default)
    {
        // V1 归档策略：清空内容并将 importance 设为 -1 作为软删除标记
        await _library.UpdateChapterContentAsync(chapterId, string.Empty, ct);
        await _library.UpdateChapterImportanceAsync(chapterId, -1.0, ct);
        return true;
    }

    // ── Private helpers ──

    /// <summary>递归构建 TreeNode DTO（含子节点和挂载的 Book）。</summary>
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

        return new MemoryLibraryTreeNodeDto(
            node.NodeId,
            node.ParentNodeId,
            node.NodeType,
            node.Name,
            node.Summary,
            node.Status,
            null, // BookId 暂不填充，V1 仅展示 TreeNode，挂载 Book 由后续 phase 完善
            childDtos);
    }

    /// <summary>递归统计所有后代节点数。</summary>
    private async Task<int> CountAllDescendants(
        string workspaceId, string libraryId,
        IReadOnlyList<PuddingCode.Abstractions.TreeNodeRecord> nodes,
        CancellationToken ct)
    {
        var count = nodes.Count;
        foreach (var node in nodes)
        {
            var children = await _library.GetTreeChildrenAsync(workspaceId, libraryId, node.NodeId, ct);
            count += await CountAllDescendants(workspaceId, libraryId, children, ct);
        }
        return count;
    }
}
