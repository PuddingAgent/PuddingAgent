namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 全文索引供给协调器 —— 组件内<b>唯一</b>的供给入口。
/// <para>
/// 所有供给入口（宿主 bootstrap / 显式工具 / 离线 CLI）都只向本接口提交任务；协调器集中负责：
/// scope 规范化、同任务幂等合并、进程内单写者、Windows 跨进程文件租约、job 状态机、Plan 干跑估算。
/// </para>
/// <para>
/// ⚠️ A1 边界：本切片<b>不接宿主</b>、<b>不调真实 Lucene</b>（测试全用替身）、
/// <b>不做</b> staging / 预算硬限 / 成功后原子切换（属 A2）。
/// </para>
/// </summary>
public interface IFullTextIndexSupplyCoordinator
{
    /// <summary>
    /// 干跑估算：规范化 scope、按索引口径清点语料、预测索引体积并给出预算判定。
    /// <b>零写入</b>：不建索引目录、不写租约、不写任何文件、不启动构建。
    /// </summary>
    Task<SupplyPlanResult> PlanAsync(SupplyScopeRequest request, CancellationToken ct = default);

    /// <summary>
    /// 提交一次供给。返回 <see cref="SupplyOutcome"/>：Started（新建并已启动）/ Merged（幂等合并，不重复构建）
    /// / Busy（跨进程租约被占用，不构建、不写索引）/ Rejected（scope 非法，无 job 产生）。
    /// <para>
    /// ⚠️ 本方法的 <paramref name="ct"/> 只管<b>提交阶段</b>（等待 scope 闸门、取租约）。
    /// job 一旦启动，其执行由 <see cref="CancelAsync"/> 控制，不受调用方 ct 影响
    /// （否则调用方的一次超时会中途腰斩一次全量构建）。
    /// </para>
    /// </summary>
    Task<SupplyRequestOutcome> BuildAsync(SupplyScopeRequest request, CancellationToken ct = default);

    /// <summary>查询单个 job 状态；未知（或已被历史淘汰）返回 null。</summary>
    Task<SupplyJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>列出保留中的全部 job 状态（按开始时间排序；条数受 MaxRetainedJobs 限制）。</summary>
    Task<IReadOnlyList<SupplyJobStatus>> ListStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// 取消 <c>Queued|Running</c> 的 job：终态 <see cref="SupplyJobState.Cancelled"/>，
    /// 且传给 builder 的取消令牌会被真正触发。
    /// 对终态 job 返回 false（非法流转被拒绝并记录消息，不做静默 no-op）。
    /// </summary>
    Task<bool> CancelAsync(string jobId, CancellationToken ct = default);
}
