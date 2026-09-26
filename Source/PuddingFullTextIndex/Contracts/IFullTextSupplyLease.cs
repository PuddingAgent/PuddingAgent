namespace PuddingFullTextIndex.Contracts;

/// <summary>租约持有者身份（owner 标识 + 进程号 + 机器名）。</summary>
/// <param name="OwnerId">owner 标识，默认 <c>机器名#进程号#角色</c>（见 <see cref="ForCurrentProcess"/>）。</param>
/// <param name="ProcessId">进程号。</param>
/// <param name="MachineName">机器名。</param>
public sealed record SupplyLeaseOwner(string OwnerId, int ProcessId, string MachineName)
{
    /// <summary>
    /// 当前进程在指定角色下的默认身份：<c>机器名#进程号#角色</c>。
    /// <para>
    /// <b>没有无参重载</b>（删除它是本修复的一部分）：角色是「谁在写」的一部分身份，
    /// 必须由调用点在编译期显式表态，不得靠默认值蒙混 —— 否则同进程内两条路径又会拿到同一个
    /// <c>OwnerId</c>，被租约判成「自己人」而重入，互斥形同虚设。
    /// </para>
    /// </summary>
    /// <param name="role">调用方所属角色（供给 / 维护）。</param>
    public static SupplyLeaseOwner ForCurrentProcess(SupplyLeaseRole role) =>
        new($"{Environment.MachineName}#{Environment.ProcessId}#{role}", Environment.ProcessId, Environment.MachineName);
}

/// <summary>已取得的租约（值对象）。</summary>
/// <param name="ScopeKey">规范化 scope 键。</param>
/// <param name="Owner">持有者身份。</param>
/// <param name="JobId">持有时承担的 job。</param>
/// <param name="StartedAtUtc">取得（或接管）时刻。</param>
/// <param name="HeartbeatUtc">最近续期时刻。</param>
/// <param name="TakeoverReason">接管原因（新取得时为 null）。</param>
/// <param name="PreviousOwnerId">被接管的上一位持有者（无则为 null）。</param>
public sealed record SupplyLease(
    string ScopeKey,
    SupplyLeaseOwner Owner,
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatUtc,
    string? TakeoverReason,
    string? PreviousOwnerId);

/// <summary>一次取租约的结果。</summary>
/// <param name="Acquired">是否取得。</param>
/// <param name="Lease">取得时的租约；未取得为 null。</param>
/// <param name="Holder">未取得时的当前持有者（owner/PID/开始时间/心跳）；用于 <c>Busy</c> 可读性。</param>
/// <param name="Message">可读说明（取得 / 被占用 / 接管原因）。</param>
public sealed record SupplyLeaseAcquireResult(
    bool Acquired,
    SupplyLease? Lease,
    SupplyLeaseHolder? Holder,
    string Message);

/// <summary>
/// 跨进程 scope 写者租约。
/// <para>
/// 语义：同一 scope 同一时刻只允许一个 owner 持租约并写索引；拿不到租约必须<b>直接放弃本次构建</b>
/// （不得等待、不得写索引）。真实实现是文件租约（见 <c>FileSupplyLease</c>），
/// 过期判定阈值可注入，允许接管过期租约并<b>记录接管原因</b>。
/// </para>
/// </summary>
public interface IFullTextSupplyLease
{
    /// <summary>尝试取得 scope 租约。<paramref name="jobId"/> 记入租约文件，便于「谁在跑哪个 job」可查。</summary>
    Task<SupplyLeaseAcquireResult> TryAcquireAsync(
        string scopeKey,
        SupplyLeaseOwner owner,
        string? jobId = null,
        CancellationToken ct = default);

    /// <summary>续期（刷新心跳）。只有在租约确实属于 <paramref name="ownerId"/> 时才返回 true。</summary>
    Task<bool> RenewAsync(string scopeKey, string ownerId, CancellationToken ct = default);

    /// <summary>释放（删除）属于自己的租约。非自己的租约返回 false（不越权删除）。</summary>
    Task<bool> ReleaseAsync(string scopeKey, string ownerId, CancellationToken ct = default);

    /// <summary>查看当前租约持有者；无租约或无法读取时返回 null。</summary>
    Task<SupplyLeaseHolder?> DescribeHolderAsync(string scopeKey, CancellationToken ct = default);
}
