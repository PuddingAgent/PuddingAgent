using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services.Tools.Handlers;

/// <summary>
/// 记忆工具 Handler：知识图谱关联操作（add_relation / list_relations / get_related）。
/// </summary>
public sealed class GraphHandler
{
    private readonly IMemoryLibrary _lib;
    private readonly ILogger<GraphHandler> _logger;

    public GraphHandler(IMemoryLibrary lib, ILogger<GraphHandler> logger)
    {
        _lib = lib;
        _logger = logger;
    }

    // ── add_relation ──

    public async Task<string> AddRelationAsync(JsonElement root, CancellationToken ct)
    {
        var sourceId = root.GetOptionalString("source_chapter_id");
        var targetId = root.GetOptionalString("target_chapter_id");
        var relType = root.GetOptionalString("relation_type");
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(relType))
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = "source_chapter_id, target_chapter_id, and relation_type are required"
            });

        var desc = root.GetOptionalString("description");
        var weight = root.TryGetProperty("weight", out var wp) && wp.TryGetDouble(out var w) ? w : 1.0;
        var rel = await _lib.CreateChapterRelationAsync(sourceId, targetId, relType, desc, weight, ct);
        _logger.LogInformation("[GraphHandler] Added relation={Type} from={Source} to={Target}", relType, sourceId, targetId);
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            action = "add_relation",
            relationId = rel.RelationId,
            sourceChapterId = rel.SourceChapterId,
            targetChapterId = rel.TargetChapterId,
            relationType = rel.RelationType,
            weight = rel.Weight
        });
    }

    // ── list_relations ──

    public async Task<string> ListRelationsAsync(JsonElement root, CancellationToken ct)
    {
        var chapterId = root.GetOptionalString("chapter_id");
        if (string.IsNullOrEmpty(chapterId))
            return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

        var relType = root.GetOptionalString("relation_type");
        var relations = await _lib.GetChapterRelationsAsync(chapterId, relType, ct);
        var list = relations.Select(r => new
        {
            r.RelationId, r.SourceChapterId, r.TargetChapterId,
            r.RelationType, r.Description, r.Weight, r.CreatedAt
        });
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            action = "list_relations",
            chapterId,
            count = relations.Count,
            relations = list
        });
    }

    // ── get_related ──

    public async Task<string> GetRelatedAsync(JsonElement root, CancellationToken ct)
    {
        var chapterId = root.GetOptionalString("chapter_id");
        if (string.IsNullOrEmpty(chapterId))
            return JsonSerializer.Serialize(new { status = "error", message = "chapter_id is required" });

        var depth = root.GetInt32("depth", 1);
        var minWeight = root.TryGetProperty("min_weight", out var mwp) && mwp.TryGetDouble(out var mw) ? mw : 0.0;
        var related = await _lib.GetRelatedChaptersAsync(chapterId, depth, minWeight, ct);
        var list = related.Select(r => new
        {
            r.RelationId, r.SourceChapterId, r.TargetChapterId,
            r.RelationType, r.Description, r.Weight
        });
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            action = "get_related",
            chapterId,
            depth,
            count = related.Count,
            relations = list
        });
    }
}
