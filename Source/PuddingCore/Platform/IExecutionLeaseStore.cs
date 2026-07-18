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
    /// 释放租约。用于关停或重试前回退。
    /// 业务终态不得在 LeaseStore 单独提交（应通过 IExecutionJournal.CommitTerminalAsync 原子完成）。
    /// </summary>
    Task ReleaseAsync(
        ExecutionLease lease,
        CancellationToken ct);
}
