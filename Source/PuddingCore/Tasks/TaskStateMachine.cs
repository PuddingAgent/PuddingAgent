namespace PuddingCode.Tasks;

/// <summary>WorkspaceTask 纯状态机：无 IO、无依赖、纯函数。</summary>
/// <remarks>
/// 权威来源：ADR-072 §5.2 状态机 + 合同冻结 v1 §2。Failed → Ready 只能通过显式 Reopen
/// 命令触发，不作为普通转换（<see cref="CanTransition"/> 对 Failed→Ready 返回 false）。
/// </remarks>
public static class TaskStateMachine
{
    /// <summary>普通转换表（不含 Reopen 特例）。</summary>
    private static readonly IReadOnlyDictionary<WorkspaceTaskStatus, IReadOnlySet<WorkspaceTaskStatus>> Transitions =
        BuildTransitions();

    /// <summary>判断 from → to 是否为普通合法转换（不含 Reopen 特例）。</summary>
    public static bool CanTransition(WorkspaceTaskStatus from, WorkspaceTaskStatus to)
        => Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>返回某状态的所有合法目标状态（不含 Reopen）。</summary>
    public static IReadOnlySet<WorkspaceTaskStatus> GetAllowedTransitions(WorkspaceTaskStatus from)
        => Transitions[from];

    /// <summary>五列投影。Cancelled/Archived 不占五列，抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public static BoardColumn ProjectBoardColumn(WorkspaceTaskStatus status) => status switch
    {
        WorkspaceTaskStatus.Backlog => BoardColumn.Backlog,
        WorkspaceTaskStatus.Ready or
        WorkspaceTaskStatus.Deferred or
        WorkspaceTaskStatus.Reserved or
        WorkspaceTaskStatus.Assigned or
        WorkspaceTaskStatus.NeedsReview => BoardColumn.Todo,
        WorkspaceTaskStatus.InProgress or WorkspaceTaskStatus.Blocked => BoardColumn.InProgress,
        WorkspaceTaskStatus.Completed => BoardColumn.Done,
        WorkspaceTaskStatus.Failed => BoardColumn.Failed,
        WorkspaceTaskStatus.Cancelled or WorkspaceTaskStatus.Archived =>
            throw new ArgumentOutOfRangeException(nameof(status), status, "已取消/已归档默认进入历史筛选，不占五列，无对应看板列。"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知任务状态。")
    };

    /// <summary>终态判断：Completed/Failed/Cancelled/Archived。</summary>
    public static bool IsTerminal(WorkspaceTaskStatus status) => status is
        WorkspaceTaskStatus.Completed or
        WorkspaceTaskStatus.Failed or
        WorkspaceTaskStatus.Cancelled or
        WorkspaceTaskStatus.Archived;

    /// <summary>闭合判断：Completed/Failed（闭合，可 Archived）。</summary>
    public static bool IsClosed(WorkspaceTaskStatus status) => status is
        WorkspaceTaskStatus.Completed or WorkspaceTaskStatus.Failed;

    /// <summary>
    /// ADR-092 §6.2（G92-1 刀 B / P4）：结算完成权的显式声明——只有这些状态允许由 Goal 结算
    /// 事务在同一 Serializable 事务内原子写成 Completed。Assigned/InProgress 是「绑定 + 持有
    /// active assignment」的执行态：它们能走到结算的 Complete 分支，本身就意味着调用方已通过
    /// 执行门禁（canonical Turn 正常结束）与验收门禁（全部必需条件同版本 passed）并持有 active
    /// binding。
    /// <para>
    /// 已是 Completed 的重放由调用方在写入前直接跳过（幂等，不重写 CompletedAtUtc、不再 +1 版本、
    /// 不再落第二条事件），因此不列入本谓词。本谓词只声明结算路径的合法起点，<b>不修改</b>通用迁移表
    /// <see cref="Transitions"/> 与 <see cref="TryInterpretDisposition"/>：agent 的 task_update 路径依旧
    /// 不允许 Assigned→Completed（G92-1 刀 B / P5 另有守卫），完成权只属于结算事务。
    /// </para>
    /// </summary>
    public static bool CanSettleCompleted(WorkspaceTaskStatus current) => current is
        WorkspaceTaskStatus.Assigned
        or WorkspaceTaskStatus.InProgress;

    /// <summary>Command → 目标状态（含 Reopen/Resume/Requeue 特例），非法返回 false。</summary>
    public static bool TryApplyCommand(WorkspaceTaskStatus current, TaskCommand command, out WorkspaceTaskStatus next)
    {
        switch (command)
        {
            case TaskCommand.Create:
                next = WorkspaceTaskStatus.Backlog;
                return true;

            case TaskCommand.Update:
                if (IsTerminal(current))
                {
                    next = default;
                    return false;
                }

                next = current;
                return true;

            case TaskCommand.Assign:
                return TryNext(current, WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Reserved, out next);

            case TaskCommand.RunNow:
                return TryNext(
                    current,
                    new[] { WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Deferred },
                    WorkspaceTaskStatus.Reserved,
                    out next);

            case TaskCommand.Cancel:
                // 与 BuildTransitions 表保持同源：凡表中含 Cancelled 出边的状态，都必须能经 Cancel 命令到达。
                return TryNext(
                    current,
                    new[]
                    {
                        WorkspaceTaskStatus.Backlog,
                        WorkspaceTaskStatus.Ready,
                        WorkspaceTaskStatus.Deferred,
                        WorkspaceTaskStatus.Reserved,
                        WorkspaceTaskStatus.Assigned,
                        WorkspaceTaskStatus.InProgress,
                        WorkspaceTaskStatus.Blocked,
                        WorkspaceTaskStatus.NeedsReview
                    },
                    WorkspaceTaskStatus.Cancelled,
                    out next);

            case TaskCommand.Archive:
                return TryNext(
                    current,
                    new[]
                    {
                        WorkspaceTaskStatus.Completed,
                        WorkspaceTaskStatus.Cancelled,
                        WorkspaceTaskStatus.Failed
                    },
                    WorkspaceTaskStatus.Archived,
                    out next);

            case TaskCommand.Reopen:
                return TryNext(current, WorkspaceTaskStatus.Failed, WorkspaceTaskStatus.Ready, out next);

            case TaskCommand.MarkFailed:
                return TryNext(
                    current,
                    new[]
                    {
                        WorkspaceTaskStatus.Assigned,
                        WorkspaceTaskStatus.InProgress,
                        WorkspaceTaskStatus.Blocked
                    },
                    WorkspaceTaskStatus.Failed,
                    out next);

            case TaskCommand.Resume:
                return TryNext(
                    current,
                    new[] { WorkspaceTaskStatus.Blocked, WorkspaceTaskStatus.NeedsReview },
                    WorkspaceTaskStatus.Ready,
                    out next);

            case TaskCommand.Requeue:
                return TryNext(
                    current,
                    new[] { WorkspaceTaskStatus.Deferred, WorkspaceTaskStatus.Ready },
                    WorkspaceTaskStatus.Ready,
                    out next);

            default:
                next = default;
                return false;
        }
    }

    /// <summary>disposition → 目标状态（task_update 解释），非法返回 false。</summary>
    public static bool TryInterpretDisposition(WorkspaceTaskStatus current, TaskDisposition disposition, out WorkspaceTaskStatus next)
    {
        switch (disposition)
        {
            case TaskDisposition.Accept:
                return TryNext(current, WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.InProgress, out next);

            case TaskDisposition.Progress:
                return TryNext(current, WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.InProgress, out next);

            case TaskDisposition.Todo:
                return TryNext(
                    current,
                    new[]
                    {
                        WorkspaceTaskStatus.InProgress,
                        WorkspaceTaskStatus.Blocked,
                        WorkspaceTaskStatus.NeedsReview
                    },
                    WorkspaceTaskStatus.Ready,
                    out next);

            case TaskDisposition.Blocked:
                return TryNext(
                    current,
                    new[] { WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.InProgress },
                    WorkspaceTaskStatus.Blocked,
                    out next);

            case TaskDisposition.NeedsApproval:
                return TryNext(
                    current,
                    new[] { WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.InProgress },
                    WorkspaceTaskStatus.Blocked,
                    out next);

            case TaskDisposition.Rejected:
                return TryNext(current, WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Ready, out next);

            case TaskDisposition.Completed:
                return TryNext(current, WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Completed, out next);

            default:
                next = default;
                return false;
        }
    }

    private static IReadOnlyDictionary<WorkspaceTaskStatus, IReadOnlySet<WorkspaceTaskStatus>> BuildTransitions()
    {
        return new Dictionary<WorkspaceTaskStatus, IReadOnlySet<WorkspaceTaskStatus>>
        {
            // 看板卡 2a92b3ed：Backlog/Deferred/Reserved/NeedsReview 原先只有 {Ready} 单元素出边，
            // 已交付或已作废的卡没有任何终态通道，只能长期滞留（V6-T4 实例：143 张卡堆积在 Backlog）。
            // 这里只补「关闭」这一条出边，不放宽到 Ready 之外的非终态迁移，保持其它语义不变。
            [WorkspaceTaskStatus.Backlog] = Set(WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Cancelled),
            // 卡 cce95d6b（2026-09-20）：交付可能发生在 claim 通道之外（本 Agent 无 Active Task
            // Context，无法 claim/complete），此类卡停在 Ready 却没有任何终态出边。
            // 这里只补「验收即关闭」两条出边；Assigned/InProgress 的完成仍只能走 disposition
            // 与完成结算（TaskCommandService 的「完成事实唯一路径防护」未放宽）。
            [WorkspaceTaskStatus.Ready] = Set(
                WorkspaceTaskStatus.Deferred,
                WorkspaceTaskStatus.Reserved,
                WorkspaceTaskStatus.NeedsReview,
                WorkspaceTaskStatus.Completed,
                WorkspaceTaskStatus.Cancelled),
            [WorkspaceTaskStatus.Deferred] = Set(WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Cancelled),
            [WorkspaceTaskStatus.Reserved] = Set(
                WorkspaceTaskStatus.Ready,
                WorkspaceTaskStatus.Assigned,
                WorkspaceTaskStatus.Cancelled),
            [WorkspaceTaskStatus.Assigned] = Set(
                WorkspaceTaskStatus.InProgress,
                WorkspaceTaskStatus.Blocked,
                WorkspaceTaskStatus.Completed,
                WorkspaceTaskStatus.Failed,
                WorkspaceTaskStatus.Ready,
                WorkspaceTaskStatus.NeedsReview,
                WorkspaceTaskStatus.Cancelled),
            [WorkspaceTaskStatus.NeedsReview] = Set(
                WorkspaceTaskStatus.Ready,
                WorkspaceTaskStatus.Completed,
                WorkspaceTaskStatus.Cancelled),
            [WorkspaceTaskStatus.InProgress] = Set(
                WorkspaceTaskStatus.Blocked,
                WorkspaceTaskStatus.Ready,
                WorkspaceTaskStatus.Failed,
                WorkspaceTaskStatus.Completed,
                WorkspaceTaskStatus.NeedsReview,
                WorkspaceTaskStatus.Cancelled),
            [WorkspaceTaskStatus.Blocked] = Set(
                WorkspaceTaskStatus.Ready,
                WorkspaceTaskStatus.Failed,
                WorkspaceTaskStatus.Cancelled),
            [WorkspaceTaskStatus.Completed] = Set(WorkspaceTaskStatus.Archived),
            [WorkspaceTaskStatus.Failed] = Set(WorkspaceTaskStatus.Archived),
            [WorkspaceTaskStatus.Cancelled] = Set(WorkspaceTaskStatus.Archived),
            [WorkspaceTaskStatus.Archived] = Set()
        };
    }

    private static IReadOnlySet<WorkspaceTaskStatus> Set(params WorkspaceTaskStatus[] items)
        => new HashSet<WorkspaceTaskStatus>(items);

    private static bool TryNext(
        WorkspaceTaskStatus current,
        WorkspaceTaskStatus required,
        WorkspaceTaskStatus target,
        out WorkspaceTaskStatus next)
        => TryNext(current, new[] { required }, target, out next);

    private static bool TryNext(
        WorkspaceTaskStatus current,
        IReadOnlyCollection<WorkspaceTaskStatus> allowed,
        WorkspaceTaskStatus target,
        out WorkspaceTaskStatus next)
    {
        if (allowed.Contains(current))
        {
            next = target;
            return true;
        }

        next = default;
        return false;
    }
}
