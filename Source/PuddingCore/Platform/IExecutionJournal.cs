namespace PuddingCode.Platform;

/// <summary>
/// 执行日志 — 统一的 fenced 事件写入边界。
/// 替换旧版 IExecutionEventCommitter。
/// 所有写入必须携带同一 ExecutionLease 实例进行 fencing 校验。
/// </summary>
public interface IExecutionJournal
{
    /// <summary>
    /// 原子启动 Run。同一事务：
    ///   1. 验证 lease（runId + workerId + fencingToken + 未过期）
    ///   2. Run leased → running
    ///   3. Command leased → running
    ///   4. Turn accepted → running
    ///   5. 写 turn.started 事件
    ///   6. 推进 Conversation Head
    ///   7. commit
    /// 此后 Cancel 和 Steering Handler 能稳定判断 Command/Turn 状态。
    /// </summary>
    Task<AppendResult> StartRunAsync(
        ExecutionLease lease,
        string snapshotId,
        NewConversationEvent startedEvent,
        CancellationToken ct);

    /// <summary>
    /// 追加运行输出事件（delta、tool_call 等非终态事件）。
    /// 同事务验证：runId 匹配、workerId 匹配、fencingToken 匹配、Run 状态为 Running、lease 未过期。
    /// 拒绝 terminal 类型事件。
    /// </summary>
    Task<AppendResult> AppendOutputAsync(
        ExecutionLease lease,
        IReadOnlyList<NewConversationEvent> events,
        CancellationToken ct);

    /// <summary>
    /// 原子提交终态。同一事务内：
    ///   1. 验证 lease（runId + workerId + fencingToken + 未过期）
    ///   2. 验证 Turn 尚未终态
    ///   3. 写入所有待 flush 的 pending 事件（非 terminal）
    ///   4. 写入 terminal 事件（唯一，由 TurnTerminal.Kind 决定事件类型和状态）
    ///   5. 更新 Conversation Head
    ///   6. 更新 Turn（status + terminalSequence + terminalKind）
    ///   7. 更新 ExecutionRun（status + terminalSequence + completedAt）
    ///   8. 更新 Command（status + terminalSequence + completedAt）
    ///   9. 验证所有 UPDATE affected rows == 1
    /// 每个 Turn 只能调用一次；Turn 终态后任何追加或再次提交均拒绝。
    /// </summary>
    Task<AppendResult> CommitTerminalAsync(
        ExecutionLease lease,
        TurnTerminal terminal,
        IReadOnlyList<NewConversationEvent> pendingEvents,
        CancellationToken ct);

    /// <summary>
    /// Worker 的最后一道 fail-closed 边界。
    /// Coordinator 在启动前或运行中意外逃逸时，将 leased/running 状态原子收敛为失败终态。
    /// 如果 fence 已丢失或 Turn 已终态，返回 null，不覆盖新的 Worker 或既有终态。
    /// </summary>
    Task<AppendResult?> TryCommitInfrastructureFailureAsync(
        ExecutionLease lease,
        TurnTerminal terminal,
        IReadOnlyList<NewConversationEvent> pendingEvents,
        CancellationToken ct);
}

/// <summary>
/// Run 启动结果。
/// </summary>
public sealed record RunStartResult(
    long StartedSequence,
    long TurnStartedSequence,
    int EventCount);
