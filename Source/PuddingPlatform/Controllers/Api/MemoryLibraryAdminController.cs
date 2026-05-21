using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 记忆图书馆管理 API——为 Admin SPA 提供 workspace scoped 的只读浏览和受控写入。
/// 所有端点严格 workspace scoped，不做跨 workspace 聚合。
/// </summary>
[Authorize]
[ApiController]
[Route("api/admin/memory-library")]
public sealed class MemoryLibraryAdminController : ControllerBase
{
    private readonly IMemoryLibrary _library;
    private readonly IMemoryLibraryAdminService _admin;

    public MemoryLibraryAdminController(IMemoryLibrary library, IMemoryLibraryAdminService admin)
    {
        _library = library;
        _admin = admin;
    }

    // ═══════════════════════════════════════════════════════════════
    // 概览与列表
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取 workspace 下的统计概览。</summary>
    [HttpGet("workspaces/{workspaceId}/overview")]
    public async Task<ActionResult<MemoryLibraryOverviewDto>> GetOverview(
        string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { error = "workspaceId is required." });

        var overview = await _admin.GetOverviewAsync(workspaceId, ct);
        return Ok(overview);
    }

    /// <summary>列出 workspace 下所有图书馆。</summary>
    [HttpGet("workspaces/{workspaceId}/libraries")]
    public async Task<ActionResult<IReadOnlyList<LibraryRecord>>> GetLibraries(
        string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { error = "workspaceId is required." });

        var libraries = await _library.ListLibrariesAsync(workspaceId, ct);
        return Ok(libraries);
    }

    // ═══════════════════════════════════════════════════════════════
    // Memory Tree
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取 library 的记忆树（递归 TreeNode + 挂载 Book）。</summary>
    [HttpGet("libraries/{libraryId}/tree")]
    public async Task<ActionResult<IReadOnlyList<MemoryLibraryTreeNodeDto>>> GetTree(
        [FromQuery] string workspaceId, string libraryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { error = "workspaceId is required." });

        var tree = await _admin.GetTreeAsync(workspaceId, libraryId, ct);
        return Ok(tree);
    }

    // ═══════════════════════════════════════════════════════════════
    // Book Page
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取 Book 详情页（Book + Chapters），校验 workspace 归属。</summary>
    [HttpGet("books/{bookId}")]
    public async Task<ActionResult<MemoryBookPageDto>> GetBook(
        [FromQuery] string workspaceId, string bookId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { error = "workspaceId is required." });

        try
        {
            var page = await _admin.GetBookPageAsync(workspaceId, bookId, ct);
            return Ok(page);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Search
    // ═══════════════════════════════════════════════════════════════

    /// <summary>workspace scoped FTS 全文搜索。</summary>
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<MemorySearchResultDto>>> Search(
        [FromQuery] string workspaceId,
        [FromQuery] string query,
        [FromQuery] int topK = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { error = "workspaceId is required." });
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "query is required." });
        if (topK < 1 || topK > 100)
            return BadRequest(new { error = "topK must be between 1 and 100." });

        var results = await _admin.SearchAsync(workspaceId, query, topK, ct);
        return Ok(results);
    }

    // ═══════════════════════════════════════════════════════════════
    // Write (Guarded Editing) — ADR-030 Phase 3
    // ═══════════════════════════════════════════════════════════════

    /// <summary>创建树节点（directory page）。</summary>
    [HttpPost("tree-nodes")]
    public async Task<ActionResult<MemoryLibraryTreeNodeDto>> CreateTreeNode(
        [FromBody] CreateMemoryTreeNodeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkspaceId))
            return BadRequest(new { error = "workspaceId is required." });
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "name is required." });

        var node = await _admin.CreateTreeNodeAsync(req, ct);
        return CreatedAtAction(nameof(GetTree), new { libraryId = req.LibraryId, workspaceId = req.WorkspaceId }, node);
    }

    /// <summary>创建 Book page，可选挂载到指定 TreeNode。</summary>
    [HttpPost("books")]
    public async Task<ActionResult<MemoryBookPageDto>> CreateBook(
        [FromBody] CreateMemoryBookRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkspaceId))
            return BadRequest(new { error = "workspaceId is required." });
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        var page = await _admin.CreateBookAsync(req, ct);
        return CreatedAtAction(nameof(GetBook), new { bookId = page.BookId, workspaceId = req.WorkspaceId }, page);
    }

    /// <summary>更新 Book title 和 summary。</summary>
    [HttpPut("books/{bookId}")]
    public async Task<ActionResult<MemoryBookPageDto>> UpdateBook(
        string bookId, [FromBody] UpdateMemoryBookRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        var page = await _admin.UpdateBookAsync(bookId, req, ct);
        return Ok(page);
    }

    /// <summary>创建 Chapter section。</summary>
    [HttpPost("chapters")]
    public async Task<ActionResult<MemoryChapterSectionDto>> CreateChapter(
        [FromBody] CreateMemoryChapterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        var chapter = await _admin.CreateChapterAsync(req, ct);
        return CreatedAtAction(nameof(CreateChapter), new { chapterId = chapter.ChapterId }, chapter);
    }

    /// <summary>更新 Chapter title、content 和 importance。</summary>
    [HttpPut("chapters/{chapterId}")]
    public async Task<ActionResult<MemoryChapterSectionDto>> UpdateChapter(
        string chapterId, [FromBody] UpdateMemoryChapterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        var chapter = await _admin.UpdateChapterAsync(chapterId, req, ct);
        return Ok(chapter);
    }

    /// <summary>归档 Book（软删除）。</summary>
    [HttpPost("books/{bookId}/archive")]
    public async Task<ActionResult> ArchiveBook(string bookId, CancellationToken ct)
    {
        var ok = await _admin.ArchiveBookAsync(bookId, ct);
        return ok ? Ok(new { status = "archived" }) : NotFound(new { error = "Book not found." });
    }

    /// <summary>归档 Chapter（软删除，清空内容并标记 importance=-1）。</summary>
    [HttpPost("chapters/{chapterId}/archive")]
    public async Task<ActionResult> ArchiveChapter(string chapterId, CancellationToken ct)
    {
        await _admin.ArchiveChapterAsync(chapterId, ct);
        return Ok(new { status = "archived" });
    }
}
