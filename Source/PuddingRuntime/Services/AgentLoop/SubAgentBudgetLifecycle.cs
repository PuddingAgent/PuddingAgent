namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Tracks the system-owned budget window for one sub-agent run.
/// The normal budget is followed by a bounded cleanup grace window so the child can
/// save recoverable state and return a staged report instead of being cut off abruptly.
/// </summary>
internal sealed class SubAgentBudgetLifecycle
{
    private readonly bool _resumed;
    private bool _startNoticeSent;
    private bool _remaining80NoticeSent;
    private bool _remaining50NoticeSent;
    private int? _graceStartedAtRound;
    private string? _graceCause;

    public SubAgentBudgetLifecycle(
        int primaryMaxRounds,
        int graceRounds,
        TimeSpan hardMaxElapsed,
        int graceTimeoutSeconds,
        int maxToolCallsTotal,
        bool resumed)
    {
        PrimaryMaxRounds = Math.Max(1, primaryMaxRounds);
        GraceRounds = Math.Clamp(graceRounds, 10, 50);
        HardMaxElapsed = hardMaxElapsed > TimeSpan.Zero
            ? hardMaxElapsed
            : TimeSpan.FromSeconds(1);

        var requestedGrace = TimeSpan.FromSeconds(Math.Max(0, graceTimeoutSeconds));
        var maximumGrace = HardMaxElapsed > TimeSpan.FromSeconds(1)
            ? HardMaxElapsed - TimeSpan.FromSeconds(1)
            : TimeSpan.Zero;
        // A parent deadline can shorten a 24-hour system budget to only a few minutes.
        // Keep cleanup useful without letting the configured 30-minute reserve consume the
        // child's entire normal work window in that case.
        var maximumProportionalGrace = TimeSpan.FromTicks(HardMaxElapsed.Ticks / 4);
        var effectiveMaximumGrace = maximumGrace < maximumProportionalGrace
            ? maximumGrace
            : maximumProportionalGrace;
        GraceElapsed = requestedGrace < effectiveMaximumGrace
            ? requestedGrace
            : effectiveMaximumGrace;
        PrimaryMaxElapsed = HardMaxElapsed - GraceElapsed;
        MaxToolCallsTotal = Math.Max(1, maxToolCallsTotal);
        _resumed = resumed;
    }

    public int PrimaryMaxRounds { get; }
    public int GraceRounds { get; }
    public int MaxToolCallsTotal { get; }
    public TimeSpan PrimaryMaxElapsed { get; }
    public TimeSpan GraceElapsed { get; }
    public TimeSpan HardMaxElapsed { get; }
    public bool IsInGrace => _graceStartedAtRound.HasValue;

    public SubAgentBudgetDecision EvaluateBeforeRound(int roundIndex, TimeSpan elapsed)
    {
        var notices = new List<SubAgentBudgetNotice>(2);
        if (!_startNoticeSent)
        {
            _startNoticeSent = true;
            notices.Add(new SubAgentBudgetNotice(
                "start",
                BuildStartNotice()));
        }

        if (!IsInGrace)
        {
            var remainingRounds = Math.Max(0, PrimaryMaxRounds - roundIndex);
            var remainingRatio = (double)remainingRounds / PrimaryMaxRounds;
            if (!_remaining80NoticeSent && roundIndex > 0 && remainingRatio < 0.80)
            {
                _remaining80NoticeSent = true;
                notices.Add(new SubAgentBudgetNotice(
                    "remaining_80",
                    BuildRemainingNotice(remainingRounds, 80)));
            }

            if (!_remaining50NoticeSent && roundIndex > 0 && remainingRatio < 0.50)
            {
                _remaining50NoticeSent = true;
                notices.Add(new SubAgentBudgetNotice(
                    "remaining_50",
                    BuildRemainingNotice(remainingRounds, 50)));
            }

            if (roundIndex >= PrimaryMaxRounds || elapsed >= PrimaryMaxElapsed)
            {
                _graceStartedAtRound = roundIndex;
                _graceCause = roundIndex >= PrimaryMaxRounds ? "rounds" : "time";
                notices.Add(new SubAgentBudgetNotice(
                    "grace_started",
                    BuildGraceNotice()));
            }
        }

        var graceRoundsUsed = _graceStartedAtRound is { } startedAt
            ? Math.Max(0, roundIndex - startedAt)
            : 0;
        var graceExhausted = IsInGrace && graceRoundsUsed >= GraceRounds;
        var hardTimeExhausted = elapsed >= HardMaxElapsed;
        return new SubAgentBudgetDecision(
            notices,
            ShouldStop: graceExhausted || hardTimeExhausted,
            RemainingGraceRounds: IsInGrace
                ? Math.Max(0, GraceRounds - graceRoundsUsed)
                : GraceRounds,
            GraceCause: _graceCause);
    }

    private string BuildStartNotice()
    {
        var runKind = _resumed
            ? "这是一次透明续跑：保留原子代理会话和上下文，但本次运行的轮次、工具调用和时间计数器已重置。"
            : "这是本次子代理运行的系统预算。";
        return
            "[SYSTEM: SUB-AGENT BUDGET]\n" +
            $"{runKind}\n" +
            $"正常预算：{PrimaryMaxRounds} 轮、{FormatDuration(PrimaryMaxElapsed)}；" +
            $"工具调用硬上限：{MaxToolCallsTotal} 次。\n" +
            $"达到正常轮次或时间预算后，系统将额外提供 {GraceRounds} 个收尾轮次（最多 {FormatDuration(GraceElapsed)}，且不超过本次硬截止时间）。\n" +
            "请在预算内持续保存可恢复现场，并以 SUMMARY、CHANGES、EVIDENCE、RISKS、BLOCKERS 结构交付结果。";
    }

    private string BuildRemainingNotice(int remainingRounds, int threshold)
        =>
            "[SYSTEM: SUB-AGENT BUDGET NOTICE]\n" +
            $"本次正常轮次预算剩余 {remainingRounds}/{PrimaryMaxRounds}，已进入剩余 {threshold}% 阈值。" +
            "请检查进度、及时保存现场，并确保能够在预算结束前形成阶段性报告。";

    private string BuildGraceNotice()
        =>
            "[SYSTEM: SUB-AGENT CLEANUP GRACE]\n" +
            $"子代理已经超出了{(_graceCause == "time" ? "会话时间" : "轮数")}预算限制。" +
            $"系统将在 {GraceRounds} 个收尾轮次后终止本次运行（同时受剩余 {FormatDuration(GraceElapsed)} 和硬截止时间约束）。\n" +
            "请立即停止扩展任务，保存可恢复现场，并产生阶段性任务报告。报告必须包含：" +
            "SUMMARY、CHANGES、EVIDENCE、RISKS、BLOCKERS，以及下一次续跑应从哪里继续。";

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            return "0 秒";
        if (value.TotalHours >= 1)
            return $"{value.TotalHours:0.##} 小时";
        if (value.TotalMinutes >= 1)
            return $"{value.TotalMinutes:0.##} 分钟";
        return $"{value.TotalSeconds:0.##} 秒";
    }
}

internal sealed record SubAgentBudgetNotice(string Kind, string Message);

internal sealed record SubAgentBudgetDecision(
    IReadOnlyList<SubAgentBudgetNotice> Notices,
    bool ShouldStop,
    int RemainingGraceRounds,
    string? GraceCause);
