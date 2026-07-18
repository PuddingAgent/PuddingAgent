using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// EF Core implementation of IChatMessageRepository and ICompactionChatMessageStore.
/// </summary>
public sealed class ChatMessageRepository : IChatMessageRepository, ICompactionChatMessageStore
{
    private readonly PlatformDbContext _db;

    public ChatMessageRepository(PlatformDbContext db) => _db = db;

    public async Task<IReadOnlyList<ChatMessageRow>> GetMessagesCursorAsync(
        string sessionId, long? beforeId = null, int limit = 50, CancellationToken ct = default)
    {
        var query = _db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId);

        if (beforeId.HasValue)
            query = query.Where(m => m.Id < beforeId.Value);

        var messages = await query
            .OrderByDescending(m => m.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        return messages.Select(Map).ToList();
    }

    public async Task<bool> AnyBySessionIdAsync(string sessionId, CancellationToken ct = default)
        => await _db.ChatMessages.AnyAsync(m => m.SessionId == sessionId, ct);

    /// <summary>
    /// ADR-058: 按稳定业务 ID 加载用户消息。
    /// ChatExecutionWorker 通过此方法获取用户消息正文，不再从命令载荷反序列化。
    /// </summary>
    public async Task<ChatMessageRow?> GetByMessageIdAsync(string messageId, CancellationToken ct = default)
    {
        var entity = await _db.ChatMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MessageId == messageId, ct);
        return entity is null ? null : Map(entity);
    }

    private static ChatMessageRow Map(ChatMessageEntity e) => new()
    {
        Id = e.Id,
        SessionId = e.SessionId,
        Role = e.Role,
        Content = e.Content,
        UsageJson = e.UsageJson,
        ThinkingJson = e.ThinkingJson,
        CreatedAt = e.CreatedAt,
        AgentInstanceId = e.AgentInstanceId,
    };

    public async Task<IReadOnlyList<ChatMessageRow>> GetAllForSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var messages = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && !string.IsNullOrWhiteSpace(m.Content))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
        return messages.Select(Map).ToList();
    }

    public async Task<int> GetCountForSessionAsync(string sessionId, CancellationToken ct = default)
        => await _db.ChatMessages.CountAsync(m => m.SessionId == sessionId, ct);
}
