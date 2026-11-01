namespace PuddingRuntime.Services.AgentLoop;

/// <summary>本轮结束后是否该自动续跑（而不是停下来等人拍一下）。</summary>
public enum ContinuationDecision
{
    /// <summary>继续下一轮：目标未完成且有预算。</summary>
    Continue = 0,
    /// <summary>目标已完成，正常收尾。</summary>
    Completed = 1,
    /// <summary>预算用尽（轮次 / 时间 / token）。</summary>
    BudgetExhausted = 2,
    /// <summary>连续多轮没有实质进展，停下并记录原因。</summary>
    NoProgress = 3,
    /// <summary>被用户取消 / 等待外部输入。</summary>
    Halted = 4,
}

/// <summary>续跑判定所需的观测量（全部由调用方注入，本类不读任何外部状态）。</summary>
public sealed record ContinuationObservation
{
    public required AgentLoopStopReason StopReason { get; init; }

    /// <summary>已经用掉的轮次与上限。</summary>
    public int RoundsUsed { get; init; }
    public int MaxRounds { get; init; } = 50;

    /// <summary>目标里还有多少未完成事项。0 表示目标已收口。</summary>
    public int OpenItems { get; init; }

    /// <summary>上一轮是否产生了实质动作（工具调用 / 文件变更 / 状态推进）。</summary>
    public bool LastRoundMadeProgress { get; init; }

    /// <summary>连续多少轮没有实质进展（含本轮）。</summary>
    public int ConsecutiveIdleRounds { get; init; }

    /// <summary>用户是否在本轮之后送来了新消息（有则优先处理用户，而不是自己续跑）。</summary>
    public bool UserMessagePending { get; init; }
}

/// <summary>
/// 自动续跑策略（纯函数）。
/// <para>
/// 背景（2026-11-01 实测）：3000 条真实 user turn 里 46.9% 含「继续」。
/// 这不是用户习惯，而是**架构症状** —— 任务没做完时 Loop 会停下，等人拍一下才动。
/// 于是用户被迫当人肉心跳。正确的做法是把"是否继续"变成 Loop 自己的判定。
/// </para>
/// <para>
/// 三条必须同时成立的续跑条件：**有未完成事项 + 还有预算 + 上一轮确实推进了**。
/// 缺最后一条就会变成"空转续跑"——那比停下来更糟，因为它烧钱且掩盖卡住的事实。
/// </para>
/// </summary>
public sealed record ContinuationPolicy
{
    /// <summary>连续多少轮无进展就强制停（默认 2：给一次重试机会，但不给无限次）。</summary>
    public int MaxConsecutiveIdleRounds { get; init; } = 2;

    public static ContinuationPolicy Default { get; } = new();

    public ContinuationDecision Decide(ContinuationObservation o)
    {
        ArgumentNullException.ThrowIfNull(o);

        if (o.UserMessagePending)
            return ContinuationDecision.Halted;

        switch (o.StopReason)
        {
            case AgentLoopStopReason.Cancelled:
            case AgentLoopStopReason.Waiting:
                return ContinuationDecision.Halted;

            case AgentLoopStopReason.Failed:
                // 失败不自动重试：重试逻辑属于上层，避免"失败即重跑"掩盖真故障。
                return ContinuationDecision.Halted;

            case AgentLoopStopReason.BudgetExhausted:
            case AgentLoopStopReason.MaxElapsedReached:
                return ContinuationDecision.BudgetExhausted;
        }

        if (o.OpenItems <= 0)
            return ContinuationDecision.Completed;

        if (o.RoundsUsed >= o.MaxRounds)
            return ContinuationDecision.BudgetExhausted;

        if (o.ConsecutiveIdleRounds >= MaxConsecutiveIdleRounds)
            return ContinuationDecision.NoProgress;

        if (!o.LastRoundMadeProgress)
            return ContinuationDecision.NoProgress;

        return ContinuationDecision.Continue;
    }

    /// <summary>是否需要在下一轮之前向模型注入"你还有未完成事项"的提醒。</summary>
    public bool NeedsReminder(ContinuationObservation o) =>
        o.OpenItems > 0 && Decide(o) == ContinuationDecision.Continue;
}
