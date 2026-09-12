namespace PuddingCode.Platform;

/// <summary>
/// ADR-059: 执行租约存储 — Worker 命令领取、租约续期、租约释放。
/// 所有操作基于 ExecutionRun 模型（不再内联到 Command 表）。
/// </summary>
public interface IExecutionLeaseStore
{
    /// <summary>
    /// 尝试获取下一个待执行命令的租约。原子 CAS 操作：
    /// BEGIN IMMEDIATE → 查找 Pending Command → 确认 Conversation 无 Active Run
    /// → 分配数据库 fencing sequence → 创建 ExecutionRun → UPDATE Command → COMMIT。
    /// </summary>
    Task<ExecutionLease?> TryAcquireAsync(
        string workerId,
        TimeSpan duration,
        CancellationToken ct);

    /// <summary>
    /// 续约。必须传入完整 lease 信息（runId + workerId + fencingToken）。
    /// WHERE run_id = @runId AND worker_id = @workerId AND fencing_token = @fencingToken
    ///   AND status IN ('leased','running','cancel_requested') AND lease_until >= @now。
    /// 任意条件不满足返回 false。
    /// </summary>
    Task<bool> RenewAsync(
        ExecutionLease lease,
        TimeSpan duration,
        CancellationToken ct);

    /// <summary>
    /// 释放租约。调用方必须显式声明本次释放的语义（<paramref name="outcome"/>），
    /// LeaseStore 不得再自行假定终态（历史实现无条件写 lease_lost，把优雅释放也贴成租约丢失）。
    /// <list type="bullet">
    /// <item><see cref="RunStatus.LeaseLost"/>：真实丢失 / 中止回退。调用方没有提交任何 Turn 终态，
    /// run 记为 <c>lease_lost</c>，command 回到 <c>pending</c>，Turn 回到 <c>accepted</c>（可重试）。</item>
    /// <item><see cref="RunStatus.Succeeded"/> / <see cref="RunStatus.Failed"/> / <see cref="RunStatus.Cancelled"/>：
    /// 优雅释放。调用方已知该 Turn 的真实终态，run/command 记为对应终态并清空租约；
    /// <b>不得</b>把 command 退回 <c>pending</c>，<b>不得</b>把 Turn 退回 <c>accepted</c>。</item>
    /// </list>
    /// <see cref="RunStatus.Leased"/> / <see cref="RunStatus.Running"/> 不是释放语义，调用即抛 <see cref="ArgumentOutOfRangeException"/>。
    /// 业务终态应优先通过 IExecutionJournal.CommitTerminalAsync 原子提交；此处终态分支只对仍活跃的
    /// run/command 生效（已终态的行为幂等空操作）。
    /// </summary>
    Task ReleaseAsync(
        ExecutionLease lease,
        RunStatus outcome,
        CancellationToken ct);
}
