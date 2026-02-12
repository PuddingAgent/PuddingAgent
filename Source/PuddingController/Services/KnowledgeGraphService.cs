using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// 知识图谱服务（V1 内存实现）。
/// 提供 Workspace 级实体与关系管理，以及简单图谱查询。
/// V2 替换点：基于 PostgreSQL（pgvector / Apache AGE）持久化；接口不变。
/// </summary>
public sealed class KnowledgeGraphService
{
    // key: workspaceId → (entityId → entity)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GraphEntity>> _entities = new();
    // key: workspaceId → (relationId → relation)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GraphRelation>> _relations = new();

    // ── 实体管理 ─────────────────────────────────────────

    /// <summary>添加或更新实体。</summary>
    public GraphEntity UpsertEntity(GraphEntity entity)
    {
        var bucket = _entities.GetOrAdd(entity.WorkspaceId, _ => new());
        bucket[entity.EntityId] = entity;
        return entity;
    }

    /// <summary>移除实体（同时级联删除关联关系）。</summary>
    public bool RemoveEntity(string workspaceId, string entityId)
    {
        if (!_entities.TryGetValue(workspaceId, out var bucket)) return false;
        var removed = bucket.TryRemove(entityId, out _);
        if (removed && _relations.TryGetValue(workspaceId, out var relBucket))
        {
            // 级联删除该实体相关的所有关系
            var toRemove = relBucket.Values
                .Where(r => r.FromEntityId == entityId || r.ToEntityId == entityId)
                .Select(r => r.RelationId)
                .ToList();
            foreach (var rid in toRemove)
                relBucket.TryRemove(rid, out _);
        }
        return removed;
    }

    /// <summary>取单个实体。</summary>
    public GraphEntity? GetEntity(string workspaceId, string entityId)
    {
        if (!_entities.TryGetValue(workspaceId, out var bucket)) return null;
        bucket.TryGetValue(entityId, out var e);
        return e;
    }

    /// <summary>查询 Workspace 内实体，支持按关键词 / 类型过滤。</summary>
    public IReadOnlyList<GraphEntity> QueryEntities(string workspaceId, GraphQueryRequest req)
    {
        if (!_entities.TryGetValue(workspaceId, out var bucket)) return [];
        var query = bucket.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(req.EntityType))
            query = query.Where(e => e.Type.Equals(req.EntityType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(req.Keyword))
        {
            var kw = req.Keyword.ToLowerInvariant();
            query = query.Where(e =>
                e.Label.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                e.Type.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                e.Properties.Values.Any(v => v.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        }
        return query.Take(req.Limit).ToList();
    }

    // ── 关系管理 ─────────────────────────────────────────

    /// <summary>添加或更新关系。</summary>
    public GraphRelation UpsertRelation(GraphRelation relation)
    {
        var bucket = _relations.GetOrAdd(relation.WorkspaceId, _ => new());
        bucket[relation.RelationId] = relation;
        return relation;
    }

    /// <summary>移除关系。</summary>
    public bool RemoveRelation(string workspaceId, string relationId)
    {
        if (!_relations.TryGetValue(workspaceId, out var bucket)) return false;
        return bucket.TryRemove(relationId, out _);
    }

    /// <summary>返回 Workspace 内所有关系，可按实体过滤。</summary>
    public IReadOnlyList<GraphRelation> GetRelations(string workspaceId, string? entityId = null)
    {
        if (!_relations.TryGetValue(workspaceId, out var bucket)) return [];
        if (entityId is null) return bucket.Values.ToList();
        return bucket.Values
            .Where(r => r.FromEntityId == entityId || r.ToEntityId == entityId)
            .ToList();
    }

    // ── 统计 ─────────────────────────────────────────────

    public (int entities, int relations) GetStats(string workspaceId)
    {
        var ec = _entities.TryGetValue(workspaceId, out var eb) ? eb.Count : 0;
        var rc = _relations.TryGetValue(workspaceId, out var rb) ? rb.Count : 0;
        return (ec, rc);
    }
}
