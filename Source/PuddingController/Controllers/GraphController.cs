using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// Workspace 知识图谱 API。
/// 职责：实体/关系 CRUD 与简单图谱查询。
/// 路径：/api/graph/{workspaceId}/...
/// </summary>
[ApiController]
[Route("api/graph/{workspaceId}")]
public sealed class GraphController : ControllerBase
{
    private readonly KnowledgeGraphService _graph;
    private readonly InMemoryWorkspaceCatalog _catalog;

    public GraphController(KnowledgeGraphService graph, InMemoryWorkspaceCatalog catalog)
    {
        _graph = graph;
        _catalog = catalog;
    }

    // ── 实体 ─────────────────────────────────────────────

    /// <summary>查询实体，支持关键词与类型过滤。</summary>
    [HttpPost("entities/query")]
    public ActionResult<IReadOnlyList<GraphEntity>> QueryEntities(
        string workspaceId, [FromBody] GraphQueryRequest req)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return Ok(_graph.QueryEntities(workspaceId, req));
    }

    /// <summary>取单个实体。</summary>
    [HttpGet("entities/{entityId}")]
    public ActionResult<GraphEntity> GetEntity(string workspaceId, string entityId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        var e = _graph.GetEntity(workspaceId, entityId);
        return e is null ? NotFound() : Ok(e);
    }

    /// <summary>添加或更新实体。</summary>
    [HttpPut("entities")]
    public ActionResult<GraphEntity> UpsertEntity(string workspaceId, [FromBody] GraphEntity entity)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        if (entity.WorkspaceId != workspaceId)
            return BadRequest("WorkspaceId in body must match URL.");
        return Ok(_graph.UpsertEntity(entity));
    }

    /// <summary>删除实体（级联删除关联关系）。</summary>
    [HttpDelete("entities/{entityId}")]
    public ActionResult DeleteEntity(string workspaceId, string entityId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return _graph.RemoveEntity(workspaceId, entityId) ? NoContent() : NotFound();
    }

    // ── 关系 ─────────────────────────────────────────────

    /// <summary>返回 Workspace 内关系，可按 entityId 过滤。</summary>
    [HttpGet("relations")]
    public ActionResult<IReadOnlyList<GraphRelation>> GetRelations(
        string workspaceId, [FromQuery] string? entityId = null)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return Ok(_graph.GetRelations(workspaceId, entityId));
    }

    /// <summary>添加或更新关系。</summary>
    [HttpPut("relations")]
    public ActionResult<GraphRelation> UpsertRelation(string workspaceId, [FromBody] GraphRelation relation)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        if (relation.WorkspaceId != workspaceId)
            return BadRequest("WorkspaceId in body must match URL.");
        return Ok(_graph.UpsertRelation(relation));
    }

    /// <summary>删除关系。</summary>
    [HttpDelete("relations/{relationId}")]
    public ActionResult DeleteRelation(string workspaceId, string relationId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return _graph.RemoveRelation(workspaceId, relationId) ? NoContent() : NotFound();
    }

    // ── 统计 ─────────────────────────────────────────────

    /// <summary>返回 Workspace 图谱统计（实体数 + 关系数）。</summary>
    [HttpGet("stats")]
    public ActionResult GetStats(string workspaceId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        var (entities, relations) = _graph.GetStats(workspaceId);
        return Ok(new { workspaceId, entities, relations });
    }

    private bool WorkspaceExists(string workspaceId) =>
        _catalog.GetWorkspace(workspaceId) is not null;
}
