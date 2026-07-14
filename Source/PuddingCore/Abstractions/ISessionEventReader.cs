using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话事件读取器——只读 SQLite Event Store。
/// 提供水位查询、向前翻页、向后追尾三种语义。
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
