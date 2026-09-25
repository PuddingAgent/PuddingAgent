namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 跨进程租约持有者快照。<c>Busy</c> 结果必须带上它（owner / PID / 开始时间），
/// 否则「谁在写这个 scope」对调用方不可见。
/// </summary>
/// <param name="OwnerId">持有者标识（默认 <c>机器名#进程号</c>）。</param>
/// <param name="ProcessId">持有者进程号。</param>
/// <param name="MachineName">持有者机器名（同一台机器上的多进程竞争时仍可区分）。</param>
/// <param name="StartedAtUtc">持有者取得租约的时刻（UTC）。</param>
/// <param name="HeartbeatUtc">最后一次心跳/续期时刻（UTC）——过期判定就看它与租约有效期的差。</param>
/// <param name="JobId">持有者正在执行的 job（未知时为 null）。</param>
/// <param name="IsExpired">按租约有效期判定是否已过期（过期 ⇒ 可接管）。</param>
/// <param name="TakeoverReason">该持有者取得租约时的接管原因（新取得、无接管时为 null）。</param>
public sealed record SupplyLeaseHolder(
    string OwnerId,
    int ProcessId,
    string MachineName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatUtc,
    string? JobId,
    bool IsExpired,
    string? TakeoverReason);
