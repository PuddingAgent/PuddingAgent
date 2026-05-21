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
}
