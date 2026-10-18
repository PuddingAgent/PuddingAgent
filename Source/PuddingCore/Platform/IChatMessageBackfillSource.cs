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

    /// <summary>当前权威库的最大消息 Id（用于冻结本次回填高水位 H）。</summary>
    Task<long> GetMaxIdAsync(CancellationToken ct);

    /// <summary>
    /// 数据集身份（数据库路径/副本标识）。checkpoint 绑定它：恢复旧库/换库时不得沿用旧断点。
    /// </summary>
    Task<string> GetDatasetIdentityAsync(CancellationToken ct);
}
