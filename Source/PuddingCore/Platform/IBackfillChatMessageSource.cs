namespace PuddingCode.Platform;

/// <summary>
/// Lightweight chat message row for backfill scanning.
/// Minimal projection needed by SessionChunkBackfillService.
/// </summary>
public sealed class BackfillChatMessageRow
{
    public long Id { get; init; }
    public string MessageId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// Abstraction for keyset-paginated chat message scanning used by
/// SessionChunkBackfillService. Decouples PuddingRuntime from
/// PlatformDbContext.
/// </summary>
public interface IBackfillChatMessageSource
{
    /// <summary>
    /// Keyset-paginated scan: returns messages with Id &gt; <paramref name="afterId"/>,
    /// ordered by Id ascending, limited to <paramref name="limit"/> rows.
    /// </summary>
    Task<IReadOnlyList<BackfillChatMessageRow>> GetBatchAfterIdAsync(
        long afterId,
        int limit,
        CancellationToken ct = default);
}
