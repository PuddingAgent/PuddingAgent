namespace PuddingCode.Abstractions;

/// <summary>
/// 会话历史查询服务抽象——提供分页消息查询能力。
/// Tool/Agent 通过此接口访问原始会话记录，不直接依赖 DbContext。
/// </summary>
public interface IChatHistoryService
{
    /// <summary>分页获取指定会话的消息（游标分页，按时间倒序）。</summary>
    Task<ChatHistoryPage> GetMessagesAsync(string sessionId, long? before = null, int limit = 20, CancellationToken ct = default);

    /// <summary>获取最近消息（跨会话，按时间倒序）。</summary>
    Task<ChatHistoryPage> GetRecentMessagesAsync(long? before = null, int limit = 20, CancellationToken ct = default);
}

/// <summary>分页结果。</summary>
public sealed record ChatHistoryPage
{
    public IReadOnlyList<ChatHistoryEntry> Messages { get; init; } = Array.Empty<ChatHistoryEntry>();
    public bool HasMore { get; init; }
    public long? NextCursor { get; init; }
}

/// <summary>单条消息条目。</summary>
public sealed record ChatHistoryEntry
{
    public string SessionId { get; init; } = "";
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
    public long CreatedAt { get; init; }
}
