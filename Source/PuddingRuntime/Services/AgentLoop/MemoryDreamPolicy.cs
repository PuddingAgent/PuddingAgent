namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Autodream 的触发判定（纯函数，便于单测）。
/// <para>
/// 为什么把这套判定单独抽出来：dream 会消耗后台预算，而触发条件涉及
/// 「停止原因 + 冷却窗口 + 每会话配额」三者的组合。把它埋在 Hook 里会变成
/// 一段没人敢改的 if 嵌套，也没法在不构造整个 Loop 的情况下验证。
/// </para>
/// </summary>
public sealed record MemoryDreamPolicy
{
    /// <summary>同一会话两次 dream 之间的最小间隔。</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>单会话最多触发次数（超过即视为异常，不再触发）。</summary>
    public int MaxPerSession { get; init; } = 3;

    /// <summary>未产生任何结论就失败的结束方式不值得整理记忆。</summary>
    public bool SkipOnFailure { get; init; } = true;

    public static MemoryDreamPolicy Default { get; } = new();

    /// <summary>
    /// 判定本次 Loop 结束后是否应触发 dream。
    /// <para>
    /// 刻意**不在**下面这些情况触发：被取消（用户打断，上下文不完整）、
    /// 预算耗尽（子代理还在收尾）、失败（没有可沉淀的结论）。
    /// </para>
    /// </summary>
    public bool ShouldDream(
        AgentLoopStopReason stopReason,
        DateTimeOffset? lastDreamAt,
        DateTimeOffset now,
        int dreamsThisSession)
    {
        if (dreamsThisSession >= MaxPerSession)
            return false;

        if (lastDreamAt is { } last && now - last < Cooldown)
            return false;

        return stopReason switch
        {
            AgentLoopStopReason.Done => true,
            AgentLoopStopReason.MaxRoundsReached => true,
            AgentLoopStopReason.MaxElapsedReached => false,
            AgentLoopStopReason.Cancelled => false,
            AgentLoopStopReason.Waiting => false,
            AgentLoopStopReason.BudgetExhausted => false,
            AgentLoopStopReason.Failed => !SkipOnFailure,
            _ => false,
        };
    }
}
