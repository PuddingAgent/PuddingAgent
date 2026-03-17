using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// Workspace 知识库 API。
/// 职责：文档索引、文档移除、文本检索。
/// 路径：/api/knowledge/{workspaceId}/...
/// </summary>
[ApiController]
[Route("api/knowledge/{workspaceId}")]
public sealed class KnowledgeController : ControllerBase
{
    private readonly KnowledgeBaseService _kb;
    private readonly InMemoryWorkspaceCatalog _catalog;

    public KnowledgeController(KnowledgeBaseService kb, InMemoryWorkspaceCatalog catalog)
    {
        _kb = kb;
        _catalog = catalog;
    }

    // ── 文档管理 ─────────────────────────────────────────

    /// <summary>列举 Workspace 已索引文档。</summary>
    [HttpGet("documents")]
    public ActionResult<IReadOnlyList<KnowledgeDocument>> List(string workspaceId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return Ok(_kb.ListDocuments(workspaceId));
    }

    /// <summary>取单条文档。</summary>
    [HttpGet("documents/{documentId}")]
    public ActionResult<KnowledgeDocument> Get(string workspaceId, string documentId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        var doc = _kb.GetDocument(workspaceId, documentId);
        return doc is null ? NotFound() : Ok(doc);
    }

    /// <summary>索引一条文档（若 documentId 已存在则覆盖）。</summary>
    [HttpPost("documents")]
    public ActionResult<KnowledgeDocument> Index(string workspaceId, [FromBody] KnowledgeDocument doc)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        if (doc.WorkspaceId != workspaceId)
            return BadRequest("WorkspaceId in body must match URL.");
        var indexed = _kb.IndexDocument(doc);
        return Ok(indexed);
    }

    /// <summary>移除一条文档。</summary>
    [HttpDelete("documents/{documentId}")]
    public ActionResult Delete(string workspaceId, string documentId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return _kb.RemoveDocument(workspaceId, documentId) ? NoContent() : NotFound();
    }

    // ── 检索 ─────────────────────────────────────────────

    /// <summary>在 Workspace 知识库中搜索文档。</summary>
    [HttpPost("search")]
    public ActionResult<IReadOnlyList<KnowledgeSearchResult>> Search(
        string workspaceId,
        [FromBody] KnowledgeSearchRequest req)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        var results = _kb.Search(workspaceId, req.Query, req.TopK);
        return Ok(results);
    }

    private bool WorkspaceExists(string workspaceId) =>
        _catalog.GetWorkspace(workspaceId) is not null;
}

/// <summary>知识检索请求。</summary>
public sealed record KnowledgeSearchRequest
{
    public required string Query { get; init; }
    public int TopK { get; init; } = 5;
}
