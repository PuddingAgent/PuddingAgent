namespace PuddingComputer.Benchmark;

/// <summary>一次尝试之后该做什么。</summary>
public enum AttemptDecision
{
    /// <summary>oracle 通过，可以声明成功。</summary>
    Succeeded,

    /// <summary>未通过但可以再试。</summary>
    Retry,

    /// <summary>不能再试：要么次数用尽，要么风险不允许在不确定下重试。</summary>
    Blocked,
}

/// <summary>
/// 把「oracle 判定 + 风险等级 + 已用次数」折算成下一步动作。
/// <para>
/// 这是整个 benchmark 组件唯一有分支逻辑的地方，也是唯一值得单测取红的地方。
/// 它编码了 Codex 两轮规划里最硬的两条约束：
/// </para>
/// <list type="number">
/// <item><b>只有 <see cref="OracleVerdict.Pass"/> 允许声明成功。</b>
///   <see cref="OracleVerdict.Unknown"/> 无论重试多少次都不会变成成功。</item>
/// <item><b>非幂等/破坏性动作在 <see cref="OracleVerdict.Unknown"/> 下不得盲重试。</b>
///   "上一步可能已经生效"正是这类动作最危险的失败形态，再试一次可能造成第二个副作用。</item>
/// </list>
/// </summary>
public static class BenchmarkPolicy
{
    /// <summary>
    /// 决定下一步。
    /// </summary>
    /// <param name="task">任务定义。</param>
    /// <param name="evaluation">本次 oracle 的评估结果。</param>
    /// <param name="attemptsUsed">**已经用完**的尝试次数（含本次）。</param>
    /// <returns>下一步动作。</returns>
    public static AttemptDecision Decide(
        BenchmarkTask task,
        OracleEvaluation evaluation,
        int attemptsUsed)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(evaluation);

        // 判定顺序是有意的：先看成功，再看次数，最后看风险。
        //
        // 先看成功：即使次数已用尽，只要本次通过就该报成功 ——
        //   否则"最后一次才成功"会被错误地报成 blocked。
        if (evaluation.Verdict == OracleVerdict.Pass)
            return AttemptDecision.Succeeded;

        if (attemptsUsed >= task.MaxAttempts)
            return AttemptDecision.Blocked;

        // 走到这里说明本次没通过且还有余量。
        // 不确定 + 高风险 = 不许再试：重试可能产生第二个副作用。
        if (evaluation.Verdict == OracleVerdict.Unknown && task.Risk.ForbidsRetryOnUnknown())
            return AttemptDecision.Blocked;

        return AttemptDecision.Retry;
    }
}
