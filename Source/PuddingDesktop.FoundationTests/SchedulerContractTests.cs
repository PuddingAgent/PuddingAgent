using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-16 scheduler slice: the policy form mirrors Core's own constraints (including all five modes and the
/// authoritative prerequisites), and the status card distinguishes "decided" from "started".
/// </summary>
public sealed class SchedulerContractTests
{
    private static SchedulerPrerequisites Ready { get; } = new(true, true, true);
    private static SchedulerPrerequisites NotReady { get; } = new(true, false, false);

    private static SchedulerPolicyEdit Edit(string mode = "shadow", bool enabled = true) =>
        new(ExpectedRevision: 7, Enabled: enabled, Paused: false, Mode: mode,
            ScanIntervalSeconds: 60, CandidateLimit: 50, MaxStartsPerScan: 3, EventDrivenEnabled: true);

    private static SchedulerScanSummary Scan(int started = 2) => new(
        "default", "authoritative", "admin_manual", "scan-1",
        DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, 1000,
        IdleAgents: 3, BusyAgents: 1, UnknownAgents: 0,
        Backlog: 9, RefinementReady: 4, NeedsRefinement: 5, Promoted: 2,
        Candidates: 6, Eligible: 4, Deferred: 1, Denied: 1, Started: started, DecisionsRecorded: 6,
        Tracked: 5, Healthy: 4, Waiting: 1, Stalled: 0, Inconsistent: 0, CleanupRequired: 0, Repaired: 0,
        DecisionCodes: new Dictionary<string, int> { ["candidate_ok"] = 4, ["denied_capability"] = 1 },
        RepairCodes: new Dictionary<string, int>());

    [Fact]
    public void ModeVocabularyIsCoresFiveNotTheCardsTwo()
    {
        // 卡片只提到 shadow/authoritative，Core 实际接受五个。
        Assert.Equal(["disabled", "shadow", "authoritative", "authoritative-single", "authoritative-bounded"],
            SchedulerText.Modes);
        Assert.Contains("五个模式", SchedulerText.ModeNotice, StringComparison.Ordinal);
        Assert.True(SchedulerText.IsAuthoritative("authoritative-single"));
        Assert.True(SchedulerText.IsAuthoritative("authoritative-bounded"));
        Assert.False(SchedulerText.IsAuthoritative("shadow"));
        Assert.False(SchedulerText.IsAuthoritative("disabled"));
        Assert.False(SchedulerText.IsAuthoritative(null));

        Assert.Contains("shadow（只判定与记录", SchedulerText.DescribeMode("shadow"), StringComparison.Ordinal);
        Assert.Contains("单轮最多启动 1 个", SchedulerText.DescribeMode("authoritative-single"), StringComparison.Ordinal);
        Assert.Equal("模式未知", SchedulerText.DescribeMode(null));
    }

    [Fact]
    public void ValidationMirrorsCoresOwnConstraints()
    {
        Assert.Empty(SchedulerText.Validate(Edit(), Ready));

        Assert.Contains("模式必须是", SchedulerText.Validate(Edit("turbo"), Ready).Single(), StringComparison.Ordinal);
        Assert.Contains("扫描间隔", SchedulerText.Validate(Edit() with { ScanIntervalSeconds = 0 }, Ready).Single(), StringComparison.Ordinal);
        Assert.Contains("扫描间隔", SchedulerText.Validate(Edit() with { ScanIntervalSeconds = 3601 }, Ready).Single(), StringComparison.Ordinal);
        Assert.Contains("候选上限", SchedulerText.Validate(Edit() with { CandidateLimit = 501 }, Ready).Single(), StringComparison.Ordinal);
        Assert.Contains("单轮启动上限", SchedulerText.Validate(Edit() with { MaxStartsPerScan = 33 }, Ready).Single(), StringComparison.Ordinal);
        // 边界值是允许的（与 Core 的闭区间一致）。
        Assert.Empty(SchedulerText.Validate(Edit() with { ScanIntervalSeconds = 1, CandidateLimit = 1, MaxStartsPerScan = 1 }, Ready));
        Assert.Empty(SchedulerText.Validate(Edit() with { ScanIntervalSeconds = 3600, CandidateLimit = 500, MaxStartsPerScan = 32 }, Ready));

        // 开启 authoritative 系但前置不全：先拦下，并列出观察到的取值。
        var prerequisite = SchedulerText.Validate(Edit("authoritative"), NotReady).Single();
        Assert.Contains("三个前置开关", prerequisite, StringComparison.Ordinal);
        Assert.Contains("GoalRuns 关", prerequisite, StringComparison.Ordinal);
        // shadow 模式不受前置限制；关掉总开关后也不拦。
        Assert.Empty(SchedulerText.Validate(Edit("shadow"), NotReady));
        Assert.Empty(SchedulerText.Validate(Edit("authoritative", enabled: false), NotReady));
    }

    [Fact]
    public void NormalizationLowercasesModeAndPassesTheRevisionThrough()
    {
        var normalized = SchedulerText.Normalize(Edit("  SHADOW  "));
        Assert.Equal("shadow", normalized.Mode);
        // revision 必须原样传递：夹紧会把过期令牌变成有效令牌，从而悄悄破坏 CAS。
        Assert.Equal(-3, SchedulerText.Normalize(Edit() with { ExpectedRevision = -3 }).ExpectedRevision);
        Assert.Equal(7, normalized.ExpectedRevision);
        // 大小写不同即视为不同取值，规范化后必须命中白名单。
        Assert.Empty(SchedulerText.Validate(normalized, Ready));
    }

    [Fact]
    public void EffectiveStartsReflectsTheModeOverride()
    {
        var policy = new SchedulerPolicy(
            Revision: 1, Enabled: true, Paused: false, Mode: "authoritative-single", ScanIntervalSeconds: 60,
            MinimumIdleSeconds: 30, CandidateLimit: 50, MaxStartsPerScan: 8, TrackerStallSeconds: 300,
            EventDrivenEnabled: true);
        // authoritative-single 强制为 1，即使配置里写着 8。
        Assert.Equal(1, SchedulerText.EffectiveMaxStarts(policy));
        Assert.Contains("灰度", SchedulerText.MaxStartsNotice, StringComparison.Ordinal);
        Assert.Equal(8, SchedulerText.EffectiveMaxStarts(policy with { Mode = "authoritative" }));
        // 越界值被夹到 Core 的区间内。
        Assert.Equal(32, SchedulerText.EffectiveMaxStarts(policy with { Mode = "shadow", MaxStartsPerScan = 99 }));
        Assert.Equal(1, SchedulerText.EffectiveMaxStarts(policy with { Mode = "shadow", MaxStartsPerScan = 0 }));
    }

    [Fact]
    public void StatusAndScanTextSeparateDecisionFromExecution()
    {
        var status = new SchedulerStatus("default", "scanning",
            new SchedulerPolicy(4, true, false, "shadow", 60, 30, 50, 3, 300, true),
            Ready, Scan(), DateTimeOffset.UtcNow.AddSeconds(60), "", null);
        Assert.True(status.IsScanning);
        Assert.False(status.IsFaulted);
        Assert.Equal("扫描中", status.StateText);
        Assert.Contains("下次扫描约", status.NextScanText, StringComparison.Ordinal);
        Assert.Equal("没有错误", status.LastErrorText);
        Assert.Equal("shadow（只判定与记录，不真正启动）", status.Policy.ModeText);
        Assert.Equal("已启用", status.Policy.SwitchText);

        var scan = status.LastScan!;
        // 卡片要求确认「已执行而非仅派发」：候选与已启动必须分开呈现。
        Assert.Contains("候选 6", scan.CandidateText, StringComparison.Ordinal);
        Assert.Contains("已启动 2", scan.CandidateText, StringComparison.Ordinal);
        Assert.True(scan.StartedSomething);
        Assert.Contains("空闲 3", scan.AgentText, StringComparison.Ordinal);
        Assert.Contains("跟踪 5", scan.TrackerText, StringComparison.Ordinal);
        Assert.Contains("candidate_ok×4", scan.CodesText, StringComparison.Ordinal);
        Assert.Contains("修复码 无", scan.CodesText, StringComparison.Ordinal);
        Assert.Contains("管理员手动扫描", scan.TriggerText, StringComparison.Ordinal);

        // 没有启动任何任务时不能被读成「已执行」。
        var idle = Scan(started: 0);
        Assert.False(idle.StartedSomething);
        Assert.Contains("已启动 0", idle.CandidateText, StringComparison.Ordinal);

        // 故障状态与暂停开关独立表达。
        var faulted = status with { State = "faulted", LastError = "boom", LastFailedAtUtc = DateTimeOffset.UtcNow };
        Assert.True(faulted.IsFaulted);
        Assert.Equal("故障", faulted.StateText);
        Assert.Contains("boom", faulted.LastErrorText, StringComparison.Ordinal);

        var noScan = status with { LastScan = null, NextScanEstimateUtc = null };
        Assert.Contains("没有下次扫描", noScan.NextScanText, StringComparison.Ordinal);
        Assert.Equal("状态未知", SchedulerText.DescribeState(null));
        Assert.Equal("开", SchedulerText.OnOff(true));
        Assert.Equal("关", SchedulerText.OnOff(false));
    }

    [Fact]
    public void PrerequisitesAndNoticesAreExplicit()
    {
        Assert.True(Ready.AuthoritativeReady);
        Assert.False(NotReady.AuthoritativeReady);
        Assert.Contains("TaskBoundGoals 开", Ready.DescribeText, StringComparison.Ordinal);
        Assert.Contains("GoalRuns 关", NotReady.DescribeText, StringComparison.Ordinal);
        Assert.Contains("scheduler_policy_conflict", SchedulerText.RevisionNotice, StringComparison.Ordinal);
        Assert.Contains("三个前置开关", SchedulerText.AuthoritativeNotice, StringComparison.Ordinal);
        Assert.Contains("列出观察到的取值", SchedulerText.AuthoritativeNotice, StringComparison.Ordinal);
        Assert.Contains("也允许执行", SchedulerText.ManualScanNotice, StringComparison.Ordinal);
        Assert.Contains("候选只是判定结果", SchedulerText.ExecuteNotice, StringComparison.Ordinal);
    }
}
