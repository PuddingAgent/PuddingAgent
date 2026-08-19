using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 聊天消息历史查询 API。
/// 使用游标分页（CreatedAt 毫秒戳），按需加载历史消息。
/// </summary>
[Authorize]
[ApiController]
[Route("api/sessions/{sessionId}/messages")]
public class MessageApiController(PlatformDbContext db, IChatMessageRepository msgRepo, ILogger<MessageApiController> logger) : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── 消息 DTO ────────────────────────────────────────────────

    public record ChatMessageDto(
        long Id,
        string Role,
        string Content,
        List<ThinkingChunkDto>? Thinking,
        TokenUsageDto? Usage,
        long CreatedAt,
        string? MessageId,
        string? TurnId,
        string? CommandId,
        string? SourceType,
        string? SourceId,
        string? SourceName
    );

    public record ThinkingChunkDto(
        string Text,
        long Timestamp
    );

    public record MessageListResponse(
        List<ChatMessageDto> Items,
        bool HasMore,
        long? OldestCreatedAt
    );

    // ── GET: 游标分页查询消息 ──────────────────────────────────

    /// <summary>
    /// GET /api/sessions/{sessionId}/messages?before={cursor}&limit=20
    /// before: 最早已加载消息的 CreatedAt 毫秒戳，首次请求不传。
    /// limit: 每页条数，默认 20，最大 50。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<MessageListResponse>> List(
        string sessionId,
        [FromQuery] long? before = null,
        [FromQuery] int limit = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > MaxPageSize)
            limit = DefaultPageSize;

        var hasMaterializedMessages = await msgRepo.AnyBySessionIdAsync(sessionId, ct);

        if (!hasMaterializedMessages)
        {
            return Ok(await BuildFallbackFromEventLogAsync(sessionId, before, limit, ct));
        }

        IQueryable<ChatMessageEntity> query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId);

        if (before.HasValue)
            query = query.Where(m => m.CreatedAt < before.Value);

        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit + 1) // 多取一条判断 hasMore
            .ToListAsync(ct);

        var hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        // 结果按时间升序返回（前端从上往下渲染）
        var dtos = items
            .OrderBy(m => m.CreatedAt)
            .Select(m => MapToDto(m, logger))
            .ToList();

        var oldestCreatedAt = items.Count > 0
            ? items.Min(m => m.CreatedAt)
            : (long?)null;

        return Ok(new MessageListResponse(dtos, hasMore, oldestCreatedAt));
    }

    // ── GET: 会话 Token 统计（含缓存命中率）──────────────────

    /// <summary>
    /// GET /api/sessions/{sessionId}/token-stats
    /// 返回会话中所有消息的 Token 用量明细及聚合数据（含缓存命中/未命中）。
    /// </summary>
    [HttpGet("token-stats")]
    public async Task<IActionResult> GetTokenStats(
        string sessionId,
        CancellationToken ct = default)
    {
        var messages = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.UsageJson != null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.UsageJson })
            .ToListAsync(ct);

        var usageList = new List<object>();
        long totalPrompt = 0, totalCompletion = 0, totalCacheHit = 0, totalCacheMiss = 0;

        foreach (var m in messages)
        {
            TokenUsageDto? usage = null;
            if (!string.IsNullOrWhiteSpace(m.UsageJson))
            {
                try
                {
                    usage = JsonSerializer.Deserialize<TokenUsageDto>(m.UsageJson, JsonOpts);
                }
                catch (JsonException jex)
                {
                    logger.LogWarning(jex, "[Messages] Failed to deserialize UsageJson for message {MessageId}", m.Id);
                }
            }

            if (usage is null) continue;

            usageList.Add(new
            {
                messageId = m.Id.ToString(),
                usage = new
                {
                    promptTokens = usage.PromptTokens,
                    completionTokens = usage.CompletionTokens,
                    totalTokens = usage.TotalTokens,
                    contextWindowTokens = usage.ContextWindowTokens,
                    promptCacheHitTokens = usage.PromptCacheHitTokens,
                    promptCacheMissTokens = usage.PromptCacheMissTokens,
                }
            });

            totalPrompt += (long)(usage.PromptTokens ?? 0);
            totalCompletion += (long)(usage.CompletionTokens ?? 0);
            totalCacheHit += (long)(usage.PromptCacheHitTokens ?? 0);
            totalCacheMiss += (long)(usage.PromptCacheMissTokens ?? 0);
        }

        var totalCacheTokens = totalCacheHit + totalCacheMiss;
        var cacheHitRate = totalCacheTokens > 0
            ? (double)totalCacheHit / totalCacheTokens
            : 0.0;

        return Ok(new
        {
            sessionId,
            messages = usageList,
            aggregates = new
            {
                totalPromptTokens = totalPrompt,
                totalCompletionTokens = totalCompletion,
                totalCacheHitTokens = totalCacheHit,
                totalCacheMissTokens = totalCacheMiss,
                cacheHitRate = Math.Round(cacheHitRate, 4),
            }
        });
    }

    // ── Mapping ─────────────────────────────────────────────────

    private static ChatMessageDto MapToDto(ChatMessageEntity m, ILogger logger)
    {
        List<ThinkingChunkDto>? thinking = null;
        if (!string.IsNullOrWhiteSpace(m.ThinkingJson))
        {
            // P1-3 T3：ThinkingJson 支持旧数组 / v2 紧凑双格式，统一由 ReasoningCompactCodec 解析。
            // hash 校验失败或结构无效时 fail-open：返回空 thinking，不抛异常、不阻断 UI。
            var decoded = ReasoningCompactCodec.Decode(m.ThinkingJson);
            if (decoded is null)
            {
                logger.LogWarning(
                    "[Messages] Failed to decode ThinkingJson for message {MessageId}; thinking omitted (fail-open).",
                    m.Id);
                thinking = [];
            }
            else if (!decoded.HashValid)
            {
                logger.LogWarning(
                    "[Messages] ThinkingJson hash mismatch for message {MessageId}; thinking omitted (fail-open).",
                    m.Id);
                thinking = [];
            }
            else
            {
                thinking = decoded.Chunks
                    .Select(c => new ThinkingChunkDto(c.Text, c.Timestamp))
                    .ToList();
            }
        }

        TokenUsageDto? usage = null;
        if (!string.IsNullOrWhiteSpace(m.UsageJson))
        {
            try
            {
                usage = JsonSerializer.Deserialize<TokenUsageDto>(m.UsageJson, JsonOpts);
            }
            catch (JsonException) { /* skip malformed UsageJson */ }
        }

        var source = ParseSourceMetadata(m.MetadataJson);

        return new ChatMessageDto(
            m.Id,
            m.Role,
            m.Content,
            thinking,
            usage,
            m.CreatedAt,
            m.MessageId,
            m.TurnId,
            m.CommandId,
            source?.SourceType,
            source?.SourceId,
            source?.SourceName
        );
    }

    private static MessageSourceMetadata? ParseSourceMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MessageSourceMetadata>(
                metadataJson,
                JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record MessageSourceMetadata(
        string? SourceType,
        string? SourceId,
        string? SourceName);

    /// <summary>
    /// ADR-031 旧数据降级：ChatMessages 为空时，从 conversation_events 折叠出 assistant-only 转录。
    /// 用户原文未持久化，不能伪造；前端会将 agent-only 消息渲染为 orphan turn。
    /// </summary>
    private async Task<MessageListResponse> BuildFallbackFromEventLogAsync(
        string sessionId,
        long? before,
        int limit,
        CancellationToken ct)
    {
        var entities = await db.ConversationEvents
            .AsNoTracking()
            .Where(e => e.ConversationId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        if (entities.Count == 0)
            return new MessageListResponse([], false, null);

        var events = entities.Select(MapToConversationEvent).ToList();
        var transcript = ConversationTranscriptFold.Fold(events);

        // turnId → 该 turn 首个事件（用于推导 CreatedAt 毫秒戳）。
        var firstByTurn = events
            .GroupBy(e => e.TurnId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.Sequence).First(),
                StringComparer.Ordinal);

        var fallbackMessages = new List<ChatMessageDto>();
        foreach (var turn in transcript.Turns)
        {
            if (!firstByTurn.TryGetValue(turn.TurnId, out var first))
                continue;

            var content = !string.IsNullOrWhiteSpace(turn.Reply)
                ? turn.Reply
                : turn.AssistantText;
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var createdAt = ToUnixMillis(first.OccurredAt);

            List<ThinkingChunkDto>? thinking = null;
            if (!string.IsNullOrWhiteSpace(turn.ThinkingSummary))
                thinking = [new ThinkingChunkDto(turn.ThinkingSummary, createdAt)];

            fallbackMessages.Add(new ChatMessageDto(
                -Math.Abs(turn.FirstSequence),
                "agent",
                content,
                thinking,
                MapUsage(turn.Usage),
                createdAt,
                null,
                turn.TurnId,
                null,
                null,
                null,
                null));
        }

        var pageCandidates = fallbackMessages.AsEnumerable();
        if (before.HasValue)
            pageCandidates = pageCandidates.Where(m => m.CreatedAt < before.Value);

        var page = pageCandidates
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit + 1)
            .ToList();

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var ordered = page.OrderBy(m => m.CreatedAt).ToList();
        var oldestCreatedAt = ordered.Count > 0
            ? ordered.Min(m => m.CreatedAt)
            : (long?)null;

        return new MessageListResponse(ordered, hasMore, oldestCreatedAt);
    }

    // ── entity → ConversationEvent record 映射（供 ConversationTranscriptFold 使用）──

    private static ConversationEvent MapToConversationEvent(ConversationEventEntity e)
        => new()
        {
            EventId = e.EventId,
            ConversationId = e.ConversationId,
            Sequence = e.Sequence,
            WorkspaceId = e.WorkspaceId,
            TurnId = e.TurnId,
            CommandId = e.CommandId,
            RunId = e.RunId,
            MessageId = e.MessageId,
            Type = e.Type,
            SchemaVersion = e.SchemaVersion,
            OccurredAt = ParseOccurredAt(e.OccurredAt),
            CommittedAt = ParseOccurredAt(e.CommittedAt),
            CorrelationId = e.CorrelationId,
            CausationId = e.CausationId,
            ProducerEventId = e.ProducerEventId,
            AgentId = e.AgentId,
            SourceKind = ParseSourceKind(e.SourceKind),
            Payload = ParsePayload(e.Payload),
        };

    private static DateTimeOffset ParseOccurredAt(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static JsonElement ParsePayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static ConversationEventSourceKind? ParseSourceKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return Enum.TryParse<ConversationEventSourceKind>(raw, ignoreCase: true, out var kind)
            ? kind
            : null;
    }

    private static long ToUnixMillis(DateTimeOffset value)
        => value == DateTimeOffset.MinValue
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : value.ToUnixTimeMilliseconds();

    private static TokenUsageDto? MapUsage(ConversationUsageSummary? summary)
    {
        if (summary is null)
            return null;

        return new TokenUsageDto
        {
            PromptTokens = ToInt(summary.PromptTokens),
            CompletionTokens = ToInt(summary.CompletionTokens),
            TotalTokens = ToInt(summary.TotalTokens),
        };
    }

    private static int? ToInt(long? value)
        => value is null ? null : (int?)Math.Clamp(value.Value, (long)int.MinValue, (long)int.MaxValue);
}
