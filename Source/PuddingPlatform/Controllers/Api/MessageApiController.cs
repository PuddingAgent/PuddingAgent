using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
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
public class MessageApiController(PlatformDbContext db) : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private static readonly string[] TranscriptFallbackEventTypes = ["delta", "thinking", "usage", "done"];
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
        long CreatedAt
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

        var hasMaterializedMessages = await db.ChatMessages
            .AsNoTracking()
            .AnyAsync(m => m.SessionId == sessionId, ct);

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
            .Select(MapToDto)
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
                    usage = JsonSerializer.Deserialize<TokenUsageDto>(m.UsageJson);
                }
                catch { /* ignore */ }
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

    private static ChatMessageDto MapToDto(ChatMessageEntity m)
    {
        List<ThinkingChunkDto>? thinking = null;
        if (!string.IsNullOrWhiteSpace(m.ThinkingJson))
        {
            try
            {
                thinking = JsonSerializer.Deserialize<List<ThinkingChunkDto>>(m.ThinkingJson, JsonOpts);
            }
            catch { /* ignore malformed thinking JSON */ }
        }

        TokenUsageDto? usage = null;
        if (!string.IsNullOrWhiteSpace(m.UsageJson))
        {
            try
            {
                usage = JsonSerializer.Deserialize<TokenUsageDto>(m.UsageJson, JsonOpts);
            }
            catch { /* ignore */ }
        }

        return new ChatMessageDto(
            m.Id,
            m.Role,
            m.Content,
            thinking,
            usage,
            m.CreatedAt
        );
    }

    /// <summary>
    /// ADR-031 旧数据降级：ChatMessages 为空时，从 session_event_log 合成 assistant-only 转录。
    /// 用户原文未持久化，不能伪造；前端会将 agent-only 消息渲染为 orphan turn。
    /// </summary>
    private async Task<MessageListResponse> BuildFallbackFromEventLogAsync(
        string sessionId,
        long? before,
        int limit,
        CancellationToken ct)
    {
        var events = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId && TranscriptFallbackEventTypes.Contains(e.EventType))
            .OrderBy(e => e.SequenceNum)
            .Select(e => new
            {
                e.SequenceNum,
                e.EventType,
                e.Data,
                e.RecordedAt,
            })
            .ToListAsync(ct);

        if (events.Count == 0)
            return new MessageListResponse([], false, null);

        var fallbackMessages = new List<ChatMessageDto>();
        var replyBuilder = new StringBuilder();
        var thinking = new List<ThinkingChunkDto>();
        string? usageJson = null;
        long? firstCreatedAt = null;
        long lastSequence = 0;
        long lastCreatedAt = 0;

        foreach (var ev in events)
        {
            var createdAt = ParseRecordedAtMillis(ev.RecordedAt);
            firstCreatedAt ??= createdAt;
            lastSequence = ev.SequenceNum;
            lastCreatedAt = createdAt;

            if (ev.EventType == "delta")
            {
                var delta = TryReadStringProperty(ev.Data, "delta");
                if (!string.IsNullOrEmpty(delta))
                    replyBuilder.Append(delta);
                continue;
            }

            if (ev.EventType == "thinking")
            {
                var delta = TryReadStringProperty(ev.Data, "delta");
                if (!string.IsNullOrEmpty(delta))
                    thinking.Add(new ThinkingChunkDto(delta, createdAt));
                continue;
            }

            if (ev.EventType == "usage")
            {
                usageJson = TryReadUsageJson(ev.Data) ?? usageJson;
                continue;
            }

            if (ev.EventType == "done")
            {
                var reply = TryReadStringProperty(ev.Data, "reply");
                var content = !string.IsNullOrWhiteSpace(reply)
                    ? reply
                    : replyBuilder.ToString();
                var doneUsageJson = TryReadUsageJson(ev.Data) ?? usageJson;

                if (!string.IsNullOrWhiteSpace(content))
                {
                    fallbackMessages.Add(new ChatMessageDto(
                        -Math.Abs(ev.SequenceNum),
                        "agent",
                        content,
                        thinking.Count > 0 ? [.. thinking] : null,
                        DeserializeUsage(doneUsageJson),
                        firstCreatedAt ?? createdAt));
                }

                replyBuilder.Clear();
                thinking.Clear();
                usageJson = null;
                firstCreatedAt = null;
            }
        }

        if (replyBuilder.Length > 0)
        {
            fallbackMessages.Add(new ChatMessageDto(
                -Math.Abs(lastSequence == 0 ? 1 : lastSequence),
                "agent",
                replyBuilder.ToString(),
                thinking.Count > 0 ? thinking : null,
                DeserializeUsage(usageJson),
                firstCreatedAt ?? lastCreatedAt));
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

    private static long ParseRecordedAtMillis(string recordedAt)
    {
        return DateTimeOffset.TryParse(recordedAt, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static TokenUsageDto? DeserializeUsage(string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TokenUsageDto>(usageJson, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadUsageJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                return usage.GetRawText();

            return LooksLikeUsagePayload(root)
                ? root.GetRawText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeUsagePayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        return root.TryGetProperty("promptTokens", out _)
            || root.TryGetProperty("PromptTokens", out _)
            || root.TryGetProperty("completionTokens", out _)
            || root.TryGetProperty("CompletionTokens", out _)
            || root.TryGetProperty("totalTokens", out _)
            || root.TryGetProperty("TotalTokens", out _);
    }
}
