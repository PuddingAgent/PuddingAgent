using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// 会话历史查询服务实现：Repository → Service 层封装，
/// 对上层（Tool/Agent）隐藏 DbContext 细节。
/// </summary>
public sealed class ChatHistoryService : IChatHistoryService
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public ChatHistoryService(IDbContextFactory<PlatformDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ChatHistoryPage> GetMessagesAsync(string sessionId, long? before = null, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var beforeTs = before ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        limit = Math.Clamp(limit, 1, 50);

        var messages = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.CreatedAt < beforeTs)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new ChatHistoryEntry
            {
                SessionId = m.SessionId,
                Role = m.Role,
                Content = m.Content.Length > 500 ? m.Content.Substring(0, 500) + "…" : m.Content,
                CreatedAt = m.CreatedAt,
            })
            .ToListAsync(ct);

        var oldest = messages.Count > 0 ? messages.Min(m => m.CreatedAt) : (long?)null;
        var hasMore = oldest is not null
            && await db.ChatMessages.AnyAsync(m => m.SessionId == sessionId && m.CreatedAt < oldest, ct);

        return new ChatHistoryPage
        {
            Messages = messages,
            HasMore = hasMore,
            NextCursor = oldest,
        };
    }

    public async Task<ChatHistoryPage> GetRecentMessagesAsync(long? before = null, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var beforeTs = before ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        limit = Math.Clamp(limit, 1, 50);

        var messages = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.CreatedAt < beforeTs)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new ChatHistoryEntry
            {
                SessionId = m.SessionId,
                Role = m.Role,
                Content = m.Content.Length > 200 ? m.Content.Substring(0, 200) + "…" : m.Content,
                CreatedAt = m.CreatedAt,
            })
            .ToListAsync(ct);

        var oldest = messages.Count > 0 ? messages.Min(m => m.CreatedAt) : (long?)null;

        return new ChatHistoryPage
        {
            Messages = messages,
            HasMore = oldest is not null,
            NextCursor = oldest,
        };
    }
}
