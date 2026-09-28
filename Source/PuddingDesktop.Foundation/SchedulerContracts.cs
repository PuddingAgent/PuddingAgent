namespace PuddingDesktop.Foundation;

/// <summary>The scheduler policy as Core stores it. Revision is the CAS token for updates.</summary>
public sealed record SchedulerPolicy(
    int Revision, bool Enabled, bool Paused, string Mode, int ScanIntervalSeconds, int MinimumIdleSeconds,
    int CandidateLimit, int MaxStartsPerScan, int TrackerStallSeconds, bool EventDrivenEnabled)
{
    public string ModeText => SchedulerText.DescribeMode(Mode);
    public string SwitchText => !Enabled ? "已关闭" : Paused ? "已暂停" : "已启用";
    public bool IsAuthoritative => SchedulerText.IsAuthoritative(Mode);
    public string IntervalText => $"{ScanIntervalSeconds} s";
}

public sealed record SchedulerPrerequisites(
    bool TaskBoundGoalsEnabled, bool GoalRunsEnabled, bool GoalContinuationEnabled)
{
    public bool AuthoritativeReady => TaskBoundGoalsEnabled && GoalRunsEnabled && GoalContinuationEnabled;
    public string DescribeText =>
        $"TaskBoundGoals {SchedulerText.OnOff(TaskBoundGoalsEnabled)} · " +
        $"GoalRuns {SchedulerText.OnOff(GoalRunsEnabled)} · " +
        $"GoalRuns.Continuation {SchedulerText.OnOff(GoalContinuationEnabled)}";
}

/// <summary>One scan's outcome. The card asks to confirm work was executed, not merely dispatched.</summary>
public sealed record SchedulerScanSummary(
    string WorkspaceId, string Mode, string Trigger, string ScanId,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, long DurationMs,
    int IdleAgents, int BusyAgents, int UnknownAgents,
    int Backlog, int RefinementReady, int NeedsRefinement, int Promoted,
    int Candidates, int Eligible, int Deferred, int Denied, int Started, int DecisionsRecorded,
    int Tracked, int Healthy, int Waiting, int Stalled, int Inconsistent, int CleanupRequired, int Repaired,
    IReadOnlyDictionary<string, int> DecisionCodes, IReadOnlyDictionary<string, int> RepairCodes)
{
    public string TriggerText => Trigger switch
    {
        "admin_manual" => "管理员手动扫描",
        "admin_manual_repair" => "管理员手动修复",
        var value when value.Length == 0 => "触发原因未记录",
        var value => value
    };
    public string AgentText => $"空闲 {IdleAgents} · 忙 {BusyAgents} · 未知 {UnknownAgents}";
    public string CandidateText =>
        $"候选 {Candidates} · 可派发 {Eligible} · 延后 {Deferred} · 拒绝 {Denied} · 已启动 {Started}";
    public string TrackerText =>
        $"跟踪 {Tracked} · 健康 {Healthy} · 等待 {Waiting} · 停滞 {Stalled} · 不一致 {Inconsistent}";
    public string RepairText => $"需清理 {CleanupRequired} · 已修复 {Repaired}";
    public string TimeText =>
        $"{StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 用时 {DiagnosticsText.DescribeDuration(DurationMs)}";
    public bool StartedSomething => Started > 0;
    public string CodesText => DecisionCodes.Count == 0 && RepairCodes.Count == 0
        ? "没有决策/修复码"
        : "决策码 " + (DecisionCodes.Count == 0 ? "无" : string.Join("、", DecisionCodes.Select(pair => $"{pair.Key}×{pair.Value}")))
          + " · 修复码 " + (RepairCodes.Count == 0 ? "无" : string.Join("、", RepairCodes.Select(pair => $"{pair.Key}×{pair.Value}")));
}

public sealed record SchedulerStatus(
    string WorkspaceId, string State, SchedulerPolicy Policy, SchedulerPrerequisites Prerequisites,
    SchedulerScanSummary? LastScan, DateTimeOffset? NextScanEstimateUtc, string LastError, DateTimeOffset? LastFailedAtUtc)
{
    public bool IsScanning => string.Equals(State, "scanning", StringComparison.OrdinalIgnoreCase);
    public bool IsFaulted => string.Equals(State, "faulted", StringComparison.OrdinalIgnoreCase);
    public string StateText => SchedulerText.DescribeState(State);
    public string NextScanText => NextScanEstimateUtc is { } next
        ? $"下次扫描约 {next.ToLocalTime():HH:mm:ss}"
        : "没有下次扫描（未启用、已暂停或还没有扫描记录）";
    public string LastErrorText => LastError.Length == 0
        ? "没有错误"
        : LastError + (LastFailedAtUtc is { } failed ? $"（{failed.ToLocalTime():yyyy-MM-dd HH:mm:ss}）" : "");
}

/// <summary>The policy fields this page edits; Core keeps other tuning values out of this card.</summary>
public sealed record SchedulerPolicyEdit(
    int ExpectedRevision, bool Enabled, bool Paused, string Mode,
    int ScanIntervalSeconds, int CandidateLimit, int MaxStartsPerScan, bool EventDrivenEnabled);

public interface ISchedulerSettings
{
    Task<SchedulerStatus> GetStatusAsync(string workspaceId, CancellationToken cancellationToken = default);
    /// <summary>CAS on the read revision; a stale one surfaces as SettingsConflictException.</summary>
    Task<SchedulerStatus> SavePolicyAsync(string workspaceId, SchedulerPolicyEdit edit, CancellationToken cancellationToken = default);
    Task<SchedulerStatus> SetPausedAsync(string workspaceId, bool paused, int expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Manual scan runs even while paused (Core's controller does the same).</summary>
    Task<SchedulerScanSummary> RunScanAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task<SchedulerScanSummary> RunRepairAsync(string workspaceId, CancellationToken cancellationToken = default);
}

public static class SchedulerText
{
    /// <summary>Every mode Core accepts. The card names two of them; Core has five.</summary>
    public static IReadOnlyList<string> Modes { get; } =
        ["disabled", "shadow", "authoritative", "authoritative-single", "authoritative-bounded"];

    public const int MinimumScanIntervalSeconds = 1;
    public const int MaximumScanIntervalSeconds = 3600;
    public const int MinimumCandidateLimit = 1;
    public const int MaximumCandidateLimit = 500;
    public const int MinimumStartsPerScan = 1;
    public const int MaximumStartsPerScan = 32;

    public const string RevisionNotice =
        "保存是 CAS：提交里带读到的 revision，期间被别人改过就报冲突并阻止覆盖（Core 的 scheduler_policy_conflict）。";

    public const string ModeNotice =
        "Core 实际接受五个模式：disabled / shadow / authoritative / authoritative-single / authoritative-bounded" +
        "（卡片只提到 shadow 与 authoritative）。";

    public const string AuthoritativeNotice =
        "authoritative 系需要三个前置开关同时打开（TaskBoundGoals / GoalRuns / GoalRuns.Continuation），" +
        "否则 Core 会拒绝并列出观察到的取值。";

    public const string MaxStartsNotice =
        "authoritative-single 会把单轮启动数强制为 1（灰度试运行）；其余模式用配置值（Core 夹在 1–32）。";

    public const string ManualScanNotice =
        "手动扫描在暂停状态下**也允许执行**（与 Core 的控制器一致）；修复只处理跟踪器不一致，不会启动新任务。";

    public const string ExecuteNotice =
        "判断是否真的执行了：看「已启动」与跟踪器计数，而不是只看「候选/可派发」——候选只是判定结果。";

    public static string OnOff(bool value) => value ? "开" : "关";

    public static bool IsAuthoritative(string? mode) => mode is "authoritative" or "authoritative-single" or "authoritative-bounded";

    public static string DescribeMode(string? mode) => mode switch
    {
        null or "" => "模式未知",
        "disabled" => "disabled（完全关闭后台调度）",
        "shadow" => "shadow（只判定与记录，不真正启动）",
        "authoritative" => "authoritative（按判定真正启动）",
        "authoritative-single" => "authoritative-single（灰度：单轮最多启动 1 个）",
        "authoritative-bounded" => "authoritative-bounded（有上限地启动）",
        var value => value
    };

    public static string DescribeState(string? state) => state switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "scanning", StringComparison.OrdinalIgnoreCase) => "扫描中",
        var value when string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase) => "未启用",
        var value when string.Equals(value, "paused", StringComparison.OrdinalIgnoreCase) => "已暂停",
        var value when string.Equals(value, "faulted", StringComparison.OrdinalIgnoreCase) => "故障",
        var value when string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase) => "shadow（判定中）",
        var value => value
    };

    /// <summary>Mirrors the constraints Core validates for exactly these fields.</summary>
    public static IReadOnlyList<string> Validate(SchedulerPolicyEdit edit, SchedulerPrerequisites prerequisites)
    {
        var errors = new List<string>();
        if (!Modes.Contains(edit.Mode, StringComparer.Ordinal))
            errors.Add($"模式必须是 {string.Join(" / ", Modes)} 之一。");
        if (edit.ScanIntervalSeconds is < MinimumScanIntervalSeconds or > MaximumScanIntervalSeconds)
            errors.Add($"扫描间隔必须在 {MinimumScanIntervalSeconds}–{MaximumScanIntervalSeconds} 秒之间（Core 允许 1 秒–1 小时）。");
        if (edit.CandidateLimit is < MinimumCandidateLimit or > MaximumCandidateLimit)
            errors.Add($"候选上限必须在 {MinimumCandidateLimit}–{MaximumCandidateLimit} 之间。");
        if (edit.MaxStartsPerScan is < MinimumStartsPerScan or > MaximumStartsPerScan)
            errors.Add($"单轮启动上限必须在 {MinimumStartsPerScan}–{MaximumStartsPerScan} 之间。");
        // 与 Core 同一条前置规则：开启 authoritative 系但前置不全时先拦下（Core 会拒绝并列出观察值）。
        if (edit.Enabled && IsAuthoritative(edit.Mode) && !prerequisites.AuthoritativeReady)
            errors.Add("authoritative 系需要三个前置开关同时打开；当前 " + prerequisites.DescribeText + "。");
        return errors;
    }

    /// <summary>
    /// The revision is passed through untouched: clamping it would turn a stale token into a valid one and
    /// silently defeat the CAS that protects against overwriting someone else's policy change.
    /// </summary>
    public static SchedulerPolicyEdit Normalize(SchedulerPolicyEdit edit) =>
        edit with { Mode = (edit.Mode ?? "").Trim().ToLowerInvariant() };

    /// <summary>What Core will actually use, so the page can state it instead of implying the stored value.</summary>
    public static int EffectiveMaxStarts(SchedulerPolicy policy) =>
        string.Equals(policy.Mode, "authoritative-single", StringComparison.Ordinal)
            ? 1
            : Math.Clamp(policy.MaxStartsPerScan, MinimumStartsPerScan, MaximumStartsPerScan);
}
