using PuddingCode.Runtime;

namespace PuddingPlatform.Services.AgentChat;

internal enum ExecutionWatchdogDecisionKind
{
    Continue,
    HardTimeout,
    Stalled,
}

internal sealed record ExecutionWatchdogDecision(
    ExecutionWatchdogDecisionKind Kind,
    DateTimeOffset? DeadlineUtc,
    TimeSpan IdleFor,
    string? LastStage)
{
    public static ExecutionWatchdogDecision Continue { get; } =
        new(ExecutionWatchdogDecisionKind.Continue, null, TimeSpan.Zero, null);
}

/// <summary>
/// 固定安全上限与滑动无进展窗口的纯策略。Worker lease 不属于任务进度，
/// 不能通过续租延长这里的任一窗口。
/// </summary>
internal static class ExecutionWatchdogPolicy
{
    public static ExecutionWatchdogDecision Evaluate(
        DateTimeOffset nowUtc,
        DateTimeOffset? hardDeadlineUtc,
        TimeSpan noProgressTimeout,
        ExecutionProgressSnapshot? progress)
    {
        if (hardDeadlineUtc is { } hardDeadline && nowUtc >= hardDeadline)
        {
            return new ExecutionWatchdogDecision(
                ExecutionWatchdogDecisionKind.HardTimeout,
                hardDeadline,
                TimeSpan.Zero,
                progress?.LastStage);
        }

        if (progress is null || noProgressTimeout <= TimeSpan.Zero)
            return ExecutionWatchdogDecision.Continue;

        var idleFor = nowUtc - progress.LastMeaningfulProgressAtUtc;
        if (idleFor < noProgressTimeout)
            return ExecutionWatchdogDecision.Continue;

        return new ExecutionWatchdogDecision(
            ExecutionWatchdogDecisionKind.Stalled,
            progress.LastMeaningfulProgressAtUtc.Add(noProgressTimeout),
            idleFor,
            progress.LastStage);
    }
}
