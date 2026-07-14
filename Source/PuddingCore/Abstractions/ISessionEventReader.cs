using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话事件读取器——只读 SQLite Event Store。
/// <para>
/// ADR-056：事件读取使用严格的单调游标语义，消除旧 GetEventsAsync(fromSequence) 的歧义：
/// - ReadAfterAsync(afterExclusive) 使用 > 语义，读取游标之后的事件
/// - ReadBeforeAsync(beforeExclusive) 使用 &lt; 语义，用于历史向前翻页
/// - GetHeadAsync 返回当前高水位（0 表示无事件）
/// </para>
/// </summary>
public interface ISessionEventReader
{
    /// <summary>
    /// 获取会话的当前高水位（最大已提交 Sequence）。
    /// 无事件时返回 0。
    /// </summary>
    Task<long> GetHeadAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 读取 afterExclusive 之后（不含等于）的事件，最多 to throughInclusive（含等于）。
    /// 返回按 Sequence 升序排列的事件列表。
    /// 如果 limit 不够读完区间，只返回前 limit 条，调用方应循环追赶。
    /// </summary>
    Task<IReadOnlyList<SessionEventEnvelope>> ReadAfterAsync(
        string sessionId,
        long afterExclusive,
        long? throughInclusive = null,
        int limit = 256,
        CancellationToken ct = default);

    /// <summary>
    /// 读取 beforeExclusive 之前（不含等于）的最多 limit 条历史事件，
    /// 按 Sequence 降序返回（最新的在前）。
    /// 用于历史向前翻页。
    /// </summary>
    Task<IReadOnlyList<SessionEventEnvelope>> ReadBeforeAsync(
        string sessionId,
        long beforeExclusive,
        int limit = 50,
        CancellationToken ct = default);
}
