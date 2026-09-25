namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 文件租约的<b>落盘模式</b>（跨进程可读；其他进程 / 运维工具 / 后续切片按此结构解析）。
/// <para>
/// 位置：<c>&lt;FullTextIndexOptions.IndexRootDirectory&gt;/.supply-leases/&lt;sha256(scopeKey)&gt;.json</c>。
/// </para>
/// </summary>
/// <param name="ScopeKey">规范化 scope 键（本文件即为此键的租约）。</param>
/// <param name="OwnerId">持有者 owner 标识。</param>
/// <param name="ProcessId">持有者进程号。</param>
/// <param name="MachineName">持有者机器名。</param>
/// <param name="JobId">持有者正在执行的 job。</param>
/// <param name="StartedAtUtc">取得（或接管）时刻。</param>
/// <param name="HeartbeatUtc">最后心跳时刻；过期判定 = <c>now - HeartbeatUtc &gt;= 有效期</c>。</param>
/// <param name="PreviousOwnerId">被接管的上一位持有者（首次取得为 null）。</param>
/// <param name="TakeoverReason">接管原因（首次取得为 null）。</param>
public sealed record SupplyLeaseDocument(
    string ScopeKey,
    string OwnerId,
    int ProcessId,
    string MachineName,
    string? JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatUtc,
    string? PreviousOwnerId,
    string? TakeoverReason);
