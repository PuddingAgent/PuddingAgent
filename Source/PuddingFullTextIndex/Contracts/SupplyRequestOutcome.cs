namespace PuddingFullTextIndex.Contracts;

/// <summary>一次供给提交的结果种类。</summary>
public enum SupplyOutcome
{
    /// <summary>新建了 job 并已启动（调用方可通过 JobId 查状态）。</summary>
    Started = 0,

    /// <summary>合并到已有的 job / 未过期的成功构建，不重复构建（JobId 与已有 job 相同）。</summary>
    Merged = 1,

    /// <summary>scope 已被别的 owner 持有跨进程租约 ⇒ 不构建、不写索引。</summary>
    Busy = 2,

    /// <summary>请求被拒绝（scope 非法），没有任何 job 被创建。</summary>
    Rejected = 3,
}

/// <summary>单个 scope 的提交结果（多 scope 请求逐条可见，不做信息折叠）。</summary>
/// <param name="Value">scope 的原始/规范化值（拒绝时可见被拒的值）。</param>
/// <param name="ScopeKey">规范化 scope 键；未走到规范化时为 null。</param>
/// <param name="Outcome">该 scope 的结果。</param>
/// <param name="JobId">Started/Merged 时的 job 标识；Busy/Rejected 为 null。</param>
/// <param name="Reason">可读说明（合并原因 / 占用者 / 拒绝理由）。</param>
/// <param name="Holder">Busy 时为占用者的 owner/PID/开始时间；Merged 时给出进行中 job 的租约快照。</param>
public sealed record SupplyScopeOutcome(
    string? Value,
    string? ScopeKey,
    SupplyOutcome Outcome,
    string? JobId,
    string? Reason,
    SupplyLeaseHolder? Holder);

/// <summary>
/// 一次 <c>BuildAsync</c> 的整体结果。
/// <para>
/// 聚合口径（<see cref="Scopes"/> 是唯一事实源，顶层字段是其摘要）：
/// 任一 scope 被拒绝 ⇒ <see cref="SupplyOutcome.Rejected"/>；
/// 否则任一 Busy ⇒ <see cref="SupplyOutcome.Busy"/>；
/// 否则任一 Started ⇒ <see cref="SupplyOutcome.Started"/>；否则 <see cref="SupplyOutcome.Merged"/>。
/// </para>
/// <para>
/// <see cref="JobId"/> 只在「多 scope 请求共享同一个 job」时才有意义（单 scope 请求恒有值）；
/// 多 scope 各自新建 job 时为 null —— 逐条 JobId 见 <see cref="Scopes"/>，不猜。
/// </para>
/// </summary>
/// <param name="Outcome">整体结果种类（聚合见类型注释）。</param>
/// <param name="JobId">单 job 结果时的 job 标识；否则 null。</param>
/// <param name="Reason">整体可读说明。</param>
/// <param name="Holder">整体 Busy 时的占用者。</param>
/// <param name="Scopes">逐 scope 的明细（含拒绝项）。</param>
public sealed record SupplyRequestOutcome(
    SupplyOutcome Outcome,
    string? JobId,
    string? Reason,
    SupplyLeaseHolder? Holder,
    IReadOnlyList<SupplyScopeOutcome> Scopes);
