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
    /// 取消且无 pending 输出时允许 accepted Turn / leased Run 直接终态，供恢复前取消使用；
    /// 不产生虚假的 turn.started，不允许未启动的执行直接成功。
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

    /// <summary>
    /// A01-slice-4c：父 Turn park —— 本 Turn 仍有 running 子代理时，把 Turn/Run/Command 收敛为非终态
    /// <c>waiting_child</c>，并 flush 本 Turn 的非终态 pending 输出。同一事务内：
    ///   1. 校验 Run（runId + workerId + fencingToken + status = running）；不校验 lease_until，
    ///      因为 park 的语义就是停止续租、改由 waiting_child 状态承担判活（回收扫描不再命中该行）。
    ///   2. 写入 pending 非终态输出事件。
    ///   3. Turn running → waiting_child（CAS）。
    ///   4. Run running → waiting_child（释放租约，不写 completedAt / terminalSequence）。
    ///   5. Command running｜cancel_requested → waiting_child（释放租约）。
    ///   6. 把待提交终态持久化进命令 metadata_json，使收口不依赖进程内状态。
    /// <para>本 API 不写任何 terminal 事件、不写业务 completed；失败（CAS 未命中）返回 null，调用方必须回退常规终态提交。</para>
    /// </summary>
    Task<ExecutionParkResult?> ParkForChildrenAsync(
        ExecutionLease lease,
        TurnTerminal deferredTerminal,
        IReadOnlyList<NewConversationEvent> pendingEvents,
        CancellationToken ct);

    /// <summary>
    /// A01-slice-4c：唤醒收口 —— 父 Turn 已无 running 子代理时，把 park 的父 Turn 收敛为终态。
    /// 以 <c>WHERE status = 'waiting_child'</c> 的 CAS 抢占唯一收口权：并发或重复触发只允许一次成功，
    /// 其余调用返回 null（绝不写第二个终态事件）。终态事件与 park 时持久化的待提交终态逐字节一致。
    /// 无 park 行、缺待提交终态或 CAS 失败时返回 null，调用方必须把它当作「未收口」。
    /// </summary>
    Task<ExecutionParkFinalizeResult?> TryFinalizeWaitingTurnAsync(
        string parentTurnId,
        CancellationToken ct);
}

/// <summary>
/// Run 启动结果。
/// </summary>
public sealed record RunStartResult(
    long StartedSequence,
    long TurnStartedSequence,
    int EventCount);

/// <summary>
/// A01-slice-4c：父 Turn park 结果。LastSequence 为本轮 pending 输出的末序列（无输出时为 0）。
/// 该结果不表示任何终态事实成立。
/// </summary>
public sealed record ExecutionParkResult(
    long LastSequence,
    int EventCount);

/// <summary>
/// A01-slice-4c：park 的父 Turn 收口结果。TerminalKind/TerminalSequence 为本次唯一写入的终态事实。
/// </summary>
public sealed record ExecutionParkFinalizeResult(
    string TurnId,
    string RunId,
    long TerminalSequence,
    string TerminalKind);
