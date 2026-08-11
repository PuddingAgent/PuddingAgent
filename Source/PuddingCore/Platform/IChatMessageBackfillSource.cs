namespace PuddingCode.Platform;

/// <summary>
/// Interface for keyset-paginated chat message scanning.
/// Decouples PuddingRuntime from PlatformDbContext.
/// Implemented by BackfillChatMessageSource in PuddingPlatform.
/// </summary>
public interface IChatMessageBackfillSource
{
    /// <summary>
    /// Keyset-paginated scan: returns messages with Id > afterId,
    /// ordered by Id ascending, limited to 'limit' rows.
    /// </summary>
    Task<IReadOnlyList<BackfillChatMessageRow>> GetBatchAfterIdAsync(
        long afterId, int limit, CancellationToken ct);
}
