namespace PuddingCode.SubAgents;

/// <summary>子代理向父代理汇报的时点类型。</summary>
public enum SubAgentProgressKind
{
    /// <summary>周期性进度汇报。</summary>
    Progress,
    /// <summary>运行成功结束。</summary>
    Completed,
    /// <summary>运行失败结束。</summary>
    Failed,
    /// <summary>运行被取消。</summary>
    Cancelled
}

/// <summary>父代理侧「已投递到哪一步」的游标，由调用方持久化。</summary>
/// <param name="DeliveredCount">已投递条数。</param>
/// <param name="LastDeliveredRound">上一条进度汇报时的轮次；尚未投递过进度时为 null。</param>
/// <param name="LastDeliveredToolCalls">上一条进度汇报时的工具调用数。</param>
/// <param name="TerminalDelivered">终态汇报是否已投递。</param>
public sealed record SubAgentProgressReportState(
    int DeliveredCount,
    int? LastDeliveredRound,
    int? LastDeliveredToolCalls,
    bool TerminalDelivered)
{
    /// <summary>尚未投递任何汇报。</summary>
    public static readonly SubAgentProgressReportState Empty = new(0, null, null, false);
}

/// <summary>待判定的快照：子代理当前跑到哪、以什么时点结束。</summary>
public sealed record SubAgentProgressSnapshot(int Round, int ToolCalls, SubAgentProgressKind Kind);

/// <summary>单次汇报的判定结论。</summary>
public enum SubAgentProgressDecision
{
    /// <summary>应当投递。</summary>
    Deliver,
    /// <summary>与上一条完全相同，投递只会重复占用父上下文。</summary>
    SuppressDuplicate,
    /// <summary>快照轮次早于已投递轮次（乱序写入），不是新进展。</summary>
    SuppressStale,
    /// <summary>距上一条不足最小轮次间隔。</summary>
    SuppressTooFrequent,
    /// <summary>终态已送达，之后的快照一律不再投递。</summary>
    SuppressAfterTerminal
}

/// <summary>判定结论 + 可读原因（原因进运行日志，便于事后解释「为什么这条没被投递」）。</summary>
public sealed record SubAgentProgressReportPlan(SubAgentProgressDecision Decision, string Reason)
{
    /// <summary>是否应当投递。</summary>
    public bool ShouldDeliver => Decision == SubAgentProgressDecision.Deliver;
}

/// <summary>
/// 子代理 → 父代理汇报的节流规则，抽成纯函数以便逐分支断言。
/// <para>
/// 背景：子代理每转一圈都向父代理写一句「我在做第 N 步」，会把父上下文塞满——
/// 父代理真正的成本项是它自己的上下文窗口，而不是子代理内部的工具输出。
/// 因此这里只决定「这一刻该不该说」，与传输方式无关，可独立测试后再接宿主。
/// </para>
/// </summary>
public static class SubAgentProgressReportPolicy
{
    /// <summary>默认最小轮次间隔：每 3 轮最多一条进度汇报。</summary>
    public const int DefaultMinRoundInterval = 3;

    /// <summary>
    /// 判定一条快照是否应当投递给父代理。
    /// </summary>
    /// <param name="state">已投递游标。</param>
    /// <param name="snapshot">当前快照。</param>
    /// <param name="minRoundInterval">最小轮次间隔；小于 1 时按 1 处理（避免配置成 0 时每轮都发）。</param>
    public static SubAgentProgressReportPlan Plan(
        SubAgentProgressReportState state,
        SubAgentProgressSnapshot snapshot,
        int minRoundInterval = DefaultMinRoundInterval)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        var interval = minRoundInterval < 1 ? 1 : minRoundInterval;

        // 终态只发一次：父代理已经拿到结论，后续快照再投递只是噪声。
        if (state.TerminalDelivered)
            return new SubAgentProgressReportPlan(
                SubAgentProgressDecision.SuppressAfterTerminal,
                "terminal report already delivered");

        // 终态必须送达：哪怕与上一条进度同一轮，也不允许被节流吞掉，
        // 否则父代理会永远停在「运行中」。
        if (snapshot.Kind != SubAgentProgressKind.Progress)
            return new SubAgentProgressReportPlan(
                SubAgentProgressDecision.Deliver,
                "terminal status must reach the parent");

        // 首条进度没有参考点，必须送达，否则父代理看不到「已经开工」。
        if (state.LastDeliveredRound is not { } lastRound)
            return new SubAgentProgressReportPlan(
                SubAgentProgressDecision.Deliver,
                "first progress report");

        // 轮次倒退说明快照乱序到达，投递只会把父代理的状态往回拨。
        if (snapshot.Round < lastRound)
            return new SubAgentProgressReportPlan(
                SubAgentProgressDecision.SuppressStale,
                $"snapshot round {snapshot.Round} is behind last delivered round {lastRound}");

        // 同一轮的相同快照（重试、重复写入）没有新信息。
        if (snapshot.Round == lastRound && snapshot.ToolCalls == state.LastDeliveredToolCalls)
            return new SubAgentProgressReportPlan(
                SubAgentProgressDecision.SuppressDuplicate,
                "identical to the last delivered report");

        var elapsed = snapshot.Round - lastRound;
        if (elapsed < interval)
            return new SubAgentProgressReportPlan(
                SubAgentProgressDecision.SuppressTooFrequent,
                $"only {elapsed} round(s) since the last report; minimum is {interval}");

        return new SubAgentProgressReportPlan(
            SubAgentProgressDecision.Deliver,
            $"{elapsed} round(s) since the last report");
    }

    /// <summary>
    /// 投递成功后推进游标。与 <see cref="Plan"/> 分开：判定是纯函数，推进只在真的送达后发生，
    /// 这样「判定成功但传输失败」不会把游标推错位。
    /// </summary>
    public static SubAgentProgressReportState Record(
        SubAgentProgressReportState state,
        SubAgentProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Kind == SubAgentProgressKind.Progress
            ? state with
            {
                DeliveredCount = state.DeliveredCount + 1,
                LastDeliveredRound = snapshot.Round,
                LastDeliveredToolCalls = snapshot.ToolCalls
            }
            : state with { DeliveredCount = state.DeliveredCount + 1, TerminalDelivered = true };
    }
}
