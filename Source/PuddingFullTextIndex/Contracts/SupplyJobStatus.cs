namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 供给 job 的状态（状态机：<c>Queued → Running → Succeeded|Failed|Cancelled</c>；终态不可再流转）。
/// </summary>
public enum SupplyJobState
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>job 阶段（字符串枚举；终态 job 的 Phase 停在最后一次进展，成功者为 <see cref="Completed"/>）。</summary>
public static class SupplyJobPhases
{
    /// <summary>已受理、等待调度。</summary>
    public const string Queued = "Queued";

    /// <summary>正在清点语料（只读遍历，未写索引）。</summary>
    public const string Discovering = "Discovering";

    /// <summary>正在构建索引（本阶段才会写索引）。</summary>
    public const string Building = "Building";

    /// <summary>收尾（结果回填 / 终态判定）。</summary>
    public const string Finalizing = "Finalizing";

    /// <summary>成功终态。</summary>
    public const string Completed = "Completed";
}

/// <summary>
/// 一个供给 job 的可观测状态快照（不可变；协调器每次返回新实例）。
/// </summary>
/// <param name="JobId">job 标识（同 scope 的幂等合并共享同一个 JobId）。</param>
/// <param name="ScopeKey">规范化 scope 键。</param>
/// <param name="RootPath">规范化 scope 路径。</param>
/// <param name="State">状态机当前状态。</param>
/// <param name="Phase">阶段（见 <see cref="SupplyJobPhases"/>）。</param>
/// <param name="DiscoveredFileCount">清点到的可索引文件数（Discovering 之前为 0）。</param>
/// <param name="DiscoveredBytes">清点到的语料字节数（截断/超限文件已剔除，与索引口径一致）。</param>
/// <param name="StartedAt">job 进入状态机的时刻（UTC）。</param>
/// <param name="FinishedAt">终态时刻（UTC）；非终态为 null。</param>
/// <param name="Message">最近一次可读说明（失败原因 / 取消原因 / 接管原因 / 完成度量）。</param>
/// <param name="LeaseHolder">本 job 持有的跨进程租约（含接管原因）；未取得租约时为 null。</param>
public sealed record SupplyJobStatus(
    string JobId,
    string ScopeKey,
    string RootPath,
    SupplyJobState State,
    string Phase,
    int DiscoveredFileCount,
    long DiscoveredBytes,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Message,
    SupplyLeaseHolder? LeaseHolder);
