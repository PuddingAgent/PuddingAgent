namespace PuddingCode.Platform;

/// <summary>
/// Lightweight workspace projection for repository queries.
/// </summary>
public sealed class WorkspaceRow
{
    public long Id { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Lightweight chat message row for transcript queries.
/// </summary>
public sealed class ChatMessageRow
{
    public long Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? UsageJson { get; init; }
    public string? ThinkingJson { get; init; }
    public long CreatedAt { get; init; }
    public string? AgentInstanceId { get; init; }
}

/// <summary>
/// Aggregated token usage stats for a provider/model combination.
/// </summary>
public sealed class TokenUsageMonthlyStats
{
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public long TotalPromptTokens { get; init; }
    public long TotalCompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public long TotalCacheHitTokens { get; init; }
    public long TotalCacheMissTokens { get; init; }
    public int RequestCount { get; init; }
    public string Month { get; init; } = string.Empty;
}

/// <summary>
/// Paginated token usage events.
/// </summary>
public sealed class TokenUsageEventRow
{
    public long Id { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public long? CacheHitTokens { get; init; }
    public long? CacheMissTokens { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public long OccurredAt { get; init; }
}

public sealed class TokenUsageEventPage
{
    public IReadOnlyList<TokenUsageEventRow> Events { get; init; } = Array.Empty<TokenUsageEventRow>();
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>
/// Repository for workspace aggregate.
/// </summary>
public interface IWorkspaceRepository
{
    Task<WorkspaceRow?> FindByIdAsync(string workspaceId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string workspaceId, CancellationToken ct = default);
    Task CreateAsync(string workspaceId, string name, string? description, bool isEnabled, CancellationToken ct = default);
    Task UpdateAsync(long id, string name, string? description, bool isEnabled, CancellationToken ct = default);
}

/// <summary>
/// Repository for chat messages (transcripts).
/// </summary>
public interface IChatMessageRepository
{
    Task<IReadOnlyList<ChatMessageRow>> GetMessagesCursorAsync(
        string sessionId, long? beforeId = null, int limit = 50, CancellationToken ct = default);
    Task<bool> AnyBySessionIdAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Repository for token usage aggregation queries.
/// </summary>
public interface ITokenUsageStatsRepository
{
    Task<IReadOnlyList<TokenUsageMonthlyStats>> GetMonthlyStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>
/// Repository for raw token usage events.
/// </summary>
public interface ITokenUsageEventRepository
{
    Task<TokenUsageEventPage> GetFilteredAsync(
        string? workspaceId = null,
        string? sessionId = null,
        string? providerId = null,
        string? modelId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);
}
