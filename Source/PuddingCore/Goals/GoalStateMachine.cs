namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 §5: GoalRun 纯状态机 —— 转换矩阵、终态判定与预算不变量，无任何 I/O。
/// 持久层与命令服务必须先经过本类裁决，再执行 CAS 写入。
/// </summary>
public static class GoalStateMachine
{
    /// <summary>completed / cancelled / failed / budget_exhausted 是仅有的终态。</summary>
    public static bool IsTerminal(GoalPhase phase)
        => phase is GoalPhase.Completed
            or GoalPhase.Cancelled
            or GoalPhase.Failed
            or GoalPhase.BudgetExhausted;

    /// <summary>
    /// ADR-074 §5.1 合法转换矩阵：
    /// active → paused / blocked / completed / cancelled / failed / budget_exhausted；
    /// paused / blocked → active（显式 resume）或 cancelled；
    /// 四个终态（completed/cancelled/failed/budget_exhausted）无任何出边 ——
    /// budget_exhausted 也不例外，V1 不允许 resume 重置轮数，
    /// 对已终态 Goal 的 cancel 只产生"已处于终态"回执，不是状态转换。
    /// </summary>
    public static bool CanTransition(GoalPhase from, GoalPhase to)
    {
        if (from == to || IsTerminal(from))
            return false;

        return from switch
        {
            GoalPhase.Active => to is GoalPhase.Paused
                or GoalPhase.Blocked
                or GoalPhase.Completed
                or GoalPhase.Cancelled
                or GoalPhase.Failed
                or GoalPhase.BudgetExhausted,
            GoalPhase.Paused => to is GoalPhase.Active or GoalPhase.Cancelled,
            GoalPhase.Blocked => to is GoalPhase.Active or GoalPhase.Cancelled,
            _ => false,
        };
    }

    /// <summary>
    /// resume 只允许 paused / blocked；budget_exhausted 不可恢复 ——
    /// 需要新预算必须显式创建新 Goal 并保留旧 Goal 终态关联。
    /// </summary>
    public static bool CanResume(GoalPhase phase)
        => phase is GoalPhase.Paused or GoalPhase.Blocked;

    /// <summary>edit 保留 Goal identity 与已消费预算，仅 paused / blocked / active 可编辑。</summary>
    public static bool CanEdit(GoalPhase phase)
        => !IsTerminal(phase);

    /// <summary>计数不变量：预算 ∈ [1,256]，started/settled 非负且不越界。</summary>
    public static bool AreCountersValid(int maxIterations, int iterationsStarted, int iterationsSettled)
        => GoalLimits.IsValidIterationBudget(maxIterations)
           && iterationsStarted >= 0
           && iterationsSettled >= 0
           && iterationsStarted <= maxIterations
           && iterationsSettled <= iterationsStarted;

    /// <summary>
    /// 新 Iteration 受理前置条件：Goal 必须 active 且尚有剩余额度。
    /// 受理后即使取消、失败或崩溃也不回退计数（ADR-074 §3）。
    /// </summary>
    public static bool CanAcceptNewIteration(GoalPhase phase, int maxIterations, int iterationsStarted)
        => phase == GoalPhase.Active && iterationsStarted < maxIterations;

    /// <summary>额度耗尽的确定性终态判定。</summary>
    public static bool IsBudgetExhausted(int maxIterations, int iterationsStarted)
        => iterationsStarted >= maxIterations;

    /// <summary>列出某阶段允许到达的全部目标阶段（供属性测试与 UI 提示复用）。</summary>
    public static IReadOnlyList<GoalPhase> AllowedTransitions(GoalPhase from)
        => Enum.GetValues<GoalPhase>()
            .Where(to => to != from && CanTransition(from, to))
            .ToArray();
}
