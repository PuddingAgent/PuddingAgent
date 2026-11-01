namespace PuddingRuntime.Services.AgentLoop;

/// <summary>一次执行的可比对轨迹：轮次序列 + 结束原因 + 工具调用序列。</summary>
public sealed record GoalRunTrace(
    IReadOnlyList<int> Rounds,
    AgentLoopStopReason StopReason,
    IReadOnlyList<string> ToolSignatures)
{
    public static GoalRunTrace Empty { get; } = new([], AgentLoopStopReason.Done, []);
}

/// <summary>
/// 开关两态的等价性比对。
/// <para>
/// 这是「续跑开关关闭后必须与现状逐字等价」这条验收标准的**可执行形式**。
/// 没有它，"等价"只能靠人肉看日志 —— 而人肉看日志在几百轮里一定会漏。
/// </para>
/// <para>
/// 只比较**行为可观测量**（轮次、结束原因、工具调用序列），不比较时间戳与耗时：
/// 那两项天然不同，纳进来只会制造假差异。
/// </para>
/// </summary>
public static class GoalRunEquivalence
{
    /// <summary>返回差异描述；完全等价时为空数组。</summary>
    public static IReadOnlyList<string> Compare(GoalRunTrace baseline, GoalRunTrace candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var diffs = new List<string>();

        if (baseline.StopReason != candidate.StopReason)
            diffs.Add($"stopReason: {baseline.StopReason} → {candidate.StopReason}");

        if (!baseline.Rounds.SequenceEqual(candidate.Rounds))
            diffs.Add($"rounds: [{string.Join(',', baseline.Rounds)}] → [{string.Join(',', candidate.Rounds)}]");

        if (!baseline.ToolSignatures.SequenceEqual(candidate.ToolSignatures))
            diffs.Add($"tools: {baseline.ToolSignatures.Count} → {candidate.ToolSignatures.Count} 条");

        return diffs;
    }

    /// <summary>等价判定（差异为空）。</summary>
    public static bool AreEquivalent(GoalRunTrace baseline, GoalRunTrace candidate) =>
        Compare(baseline, candidate).Count == 0;
}
