using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public record TokenUsageDto(
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        int? ContextWindowTokens = null,
        int? PromptCacheHitTokens = null,
        int? PromptCacheMissTokens = null
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

            totalPrompt += usage.PromptTokens;
            totalCompletion += usage.CompletionTokens;
            totalCacheHit += usage.PromptCacheHitTokens ?? 0;
            totalCacheMiss += usage.PromptCacheMissTokens ?? 0;
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
                thinking = JsonSerializer.Deserialize<List<ThinkingChunkDto>>(m.ThinkingJson);
            }
            catch { /* ignore malformed thinking JSON */ }
        }

        TokenUsageDto? usage = null;
        if (!string.IsNullOrWhiteSpace(m.UsageJson))
        {
            try
            {
                usage = JsonSerializer.Deserialize<TokenUsageDto>(m.UsageJson);
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
}
