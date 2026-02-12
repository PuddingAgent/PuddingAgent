using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 记忆图书馆管理 API——为 Admin SPA 提供 Agent 作用域的只读浏览和受控写入。
/// 所有端点严格 workspace + agent scoped，不做跨 Agent 聚合。
/// </summary>
[Authorize]
[ApiController]
[Route("api/admin/memory-library")]
public sealed class MemoryLibraryAdminController : ControllerBase
{
    private readonly IMemoryLibraryAdminService _admin;

    public MemoryLibraryAdminController(IMemoryLibraryAdminService admin)
    {
        _admin = admin;
    }

    // ═══════════════════════════════════════════════════════════════
    // 概览与列表
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取 workspace + agent 下的统计概览。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/overview")]
    public async Task<ActionResult<MemoryLibraryOverviewDto>> GetAgentOverview(
        string workspaceId, string agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });

        var overview = await _admin.GetOverviewAsync(workspaceId, agentId, ct);
        return Ok(overview);
    }

    /// <summary>列出 workspace + agent 下的图书馆。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/libraries")]
    public async Task<ActionResult<IReadOnlyList<LibraryRecord>>> GetAgentLibraries(
        string workspaceId, string agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });

        var libraries = await _admin.GetLibrariesAsync(workspaceId, agentId, ct);
        return Ok(libraries);
    }

    /// <summary>确保 workspace + agent 下存在默认图书馆。</summary>
    [HttpPost("workspaces/{workspaceId}/agents/{agentId}/libraries/default")]
    public async Task<ActionResult<LibraryRecord>> EnsureAgentDefaultLibrary(
        string workspaceId, string agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });

        var library = await _admin.EnsureDefaultLibraryAsync(workspaceId, agentId, ct);
        return Ok(library);
    }

    // ═══════════════════════════════════════════════════════════════
    // Memory Tree
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取 agent-scoped library 的记忆树。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/tree")]
    public async Task<ActionResult<IReadOnlyList<MemoryLibraryTreeNodeDto>>> GetAgentTree(
        string workspaceId, string agentId, string libraryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });

        try
        {
            var tree = await _admin.GetTreeAsync(workspaceId, agentId, libraryId, ct);
            return Ok(tree);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Book Page
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取 agent-scoped Book 详情页。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/books/{bookId}")]
    public async Task<ActionResult<MemoryBookPageDto>> GetAgentBook(
        string workspaceId, string agentId, string bookId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });

        try
        {
            var page = await _admin.GetBookPageAsync(workspaceId, agentId, bookId, ct);
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

    /// <summary>workspace + agent scoped FTS 全文搜索。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/search")]
    public async Task<ActionResult<IReadOnlyList<MemorySearchResultDto>>> AgentSearch(
        string workspaceId,
        string agentId,
        [FromQuery] string query,
        [FromQuery] int topK = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "query is required." });
        if (topK < 1 || topK > 100)
            return BadRequest(new { error = "topK must be between 1 and 100." });

        var results = await _admin.SearchAsync(workspaceId, agentId, query, topK, ct);
        return Ok(results);
    }

    // ═══════════════════════════════════════════════════════════════
    // Write (Guarded Editing) — ADR-030 Phase 3
    // ═══════════════════════════════════════════════════════════════

    /// <summary>创建 agent-scoped 树节点。</summary>
    [HttpPost("workspaces/{workspaceId}/agents/{agentId}/tree-nodes")]
    public async Task<ActionResult<MemoryLibraryTreeNodeDto>> CreateAgentTreeNode(
        string workspaceId, string agentId, [FromBody] CreateMemoryTreeNodeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "name is required." });

        try
        {
            var node = await _admin.CreateTreeNodeAsync(workspaceId, agentId, req, ct);
            return Ok(node);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    /// <summary>创建 agent-scoped Book page，可选挂载到指定 TreeNode。</summary>
    [HttpPost("workspaces/{workspaceId}/agents/{agentId}/books")]
    public async Task<ActionResult<MemoryBookPageDto>> CreateAgentBook(
        string workspaceId, string agentId, [FromBody] CreateMemoryBookRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        try
        {
            var page = await _admin.CreateBookAsync(workspaceId, agentId, req, ct);
            return Ok(page);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    /// <summary>更新 agent-scoped Book title 和 summary。</summary>
    [HttpPut("workspaces/{workspaceId}/agents/{agentId}/books/{bookId}")]
    public async Task<ActionResult<MemoryBookPageDto>> UpdateAgentBook(
        string workspaceId, string agentId, string bookId,
        [FromBody] UpdateMemoryBookRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        try
        {
            var page = await _admin.UpdateBookAsync(workspaceId, agentId, bookId, req, ct);
            return Ok(page);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    /// <summary>创建 agent-scoped Chapter section。</summary>
    [HttpPost("workspaces/{workspaceId}/agents/{agentId}/chapters")]
    public async Task<ActionResult<MemoryChapterSectionDto>> CreateAgentChapter(
        string workspaceId, string agentId, [FromBody] CreateMemoryChapterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        try
        {
            var chapter = await _admin.CreateChapterAsync(workspaceId, agentId, req, ct);
            return Ok(chapter);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    /// <summary>更新 agent-scoped Chapter title、content 和 importance。</summary>
    [HttpPut("workspaces/{workspaceId}/agents/{agentId}/chapters/{chapterId}")]
    public async Task<ActionResult<MemoryChapterSectionDto>> UpdateAgentChapter(
        string workspaceId, string agentId, string chapterId,
        [FromBody] UpdateMemoryChapterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        try
        {
            var chapter = await _admin.UpdateChapterAsync(workspaceId, agentId, chapterId, req, ct);
            return Ok(chapter);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    /// <summary>归档 agent-scoped Book。</summary>
    [HttpPost("workspaces/{workspaceId}/agents/{agentId}/books/{bookId}/archive")]
    public async Task<ActionResult> ArchiveAgentBook(
        string workspaceId, string agentId, string bookId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });

        try
        {
            var ok = await _admin.ArchiveBookAsync(workspaceId, agentId, bookId, ct);
            return ok ? Ok(new { status = "archived" }) : NotFound(new { error = "Book not found." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    /// <summary>归档 agent-scoped Chapter。</summary>
    [HttpPost("workspaces/{workspaceId}/agents/{agentId}/chapters/{chapterId}/archive")]
    public async Task<ActionResult> ArchiveAgentChapter(
        string workspaceId, string agentId, string chapterId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "workspaceId and agentId are required." });

        try
        {
            await _admin.ArchiveChapterAsync(workspaceId, agentId, chapterId, ct);
            return Ok(new { status = "archived" });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Sources & Pointers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取 agent-scoped 来源引用列表。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/sources")]
    public async Task<ActionResult<IReadOnlyList<SourceReferenceRecord>>> GetAgentSources(
        string workspaceId, string agentId,
        [FromQuery] string ownerType, [FromQuery] string ownerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerType) || string.IsNullOrWhiteSpace(ownerId))
            return BadRequest(new { error = "ownerType and ownerId are required." });

        try
        {
            var sources = await _admin.GetSourcesAsync(workspaceId, agentId, ownerType, ownerId, ct);
            return Ok(sources);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }

    /// <summary>获取 agent-scoped 指针（outgoing + backlinks）。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/pointers")]
    public async Task<ActionResult<MemoryPointersDto>> GetAgentPointers(
        string workspaceId,
        string agentId,
        [FromQuery] string sourceType,
        [FromQuery] string sourceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceId))
            return BadRequest(new { error = "sourceType and sourceId are required." });

        try
        {
            var pointers = await _admin.GetPointersAsync(workspaceId, agentId, sourceType, sourceId, ct);
            return Ok(pointers);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
    }
}
