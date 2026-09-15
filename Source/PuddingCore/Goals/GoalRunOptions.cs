namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 Goal 运行时配置。配置节：GoalRuns。
/// 与旧 GoalMode（JSON 注入队列原型）完全独立；默认关闭 —— 不改变任何现有行为。
/// 配置优先来自 <DataRoot>/config/system.json 对应节（配置文件优先约定）。
/// </summary>
public sealed class GoalRunOptions
{
    public const string SectionName = "GoalRuns";

    /// <summary>总开关。默认 false；关闭时 /goal 命令返回明确的 goal_disabled 提示。</summary>
    public bool Enabled { get; set; }

    /// <summary>--rounds 省略时的默认预算。不可超过 GoalLimits.MaxIterationsHardLimit。</summary>
    public int DefaultMaxIterations { get; set; } = GoalLimits.DefaultMaxIterations;

    /// <summary>
    /// G2 durable continuation 开关。默认 false；只有 Enabled 与本开关同时为 true
    /// 才会创建/领取 goal_outbox，不改变已有 G1 Goal 控制面行为。
    /// </summary>
    public bool ContinuationEnabled { get; set; }

    public TimeSpan ContinuationScanInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ContinuationLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ConversationBusyRetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// G92-1：受控检查（build/test/postcondition）的执行工作目录（通常是仓库根）。
    /// 为空时受控检查一律 fail-closed（evidence_missing），不得在任意目录里构建/跑测试。
    /// </summary>
    public string? CheckWorkingDirectory { get; set; }

    /// <summary>G92-1：单次受控检查的 deadline 秒数（默认 600）。</summary>
    public int CheckTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// G92-1：有界规划使用的受检目标（相对仓库根的项目文件，如
    /// Source/PuddingPlatformTests/PuddingPlatformTests.csproj）。为空时合同派生不发生
    /// （保持空合同 → 有界修复）；不接受目录通配、绝对路径或任意命令。
    /// </summary>
    public string[] CheckProjects { get; set; } = [];
    public int ContinuationBatchSize { get; set; } = 8;
    public int ContinuationMaxAttempts { get; set; } = 5;

    /// <summary>
    /// ADR-092：Goal 未显式配置 resume_policy 时的默认重启策略。只允许
    /// <see cref="GoalResumePolicies.Paused"/>（默认，等价历史 disarm 行为）或
    /// <see cref="GoalResumePolicies.AutoResumeOnRestart"/>。
    /// </summary>
    public string DefaultResumePolicy { get; set; } = GoalResumePolicies.Paused;

    /// <summary>ADR-092：单次 boot 自动恢复 Goal 数量上限（防恢复风暴），超出部分按 paused 处理。</summary>
    public int MaxAutoResumesPerBoot { get; set; } = 8;

    /// <summary>
    /// P0-2（ADR-092 §7）：无进展/同阻塞熔断阈值 —— 同一进度指纹未变化、或同一阻塞码连续、
    /// 或基础设施类失败连续达到该次数后，结算不再返回 repair（先尝试一次 Replan 改选另一 ready
    /// WorkUnit；仍无进展则转 needs_user / typed wait）。默认 3，对齐 codex-rs ext/goal
    /// accounting 的 consecutive failure 阈值；合法边界 1..16。
    /// </summary>
    public int NoProgressBreakerThreshold { get; set; } = 3;

    /// <summary>启动校验：局部配置不得扩大系统硬边界。</summary>
    public static IReadOnlyList<string> Validate(GoalRunOptions options)
    {
        var errors = new List<string>();
        if (options.DefaultMaxIterations is < GoalLimits.MinIterations
            or > GoalLimits.MaxIterationsHardLimit)
        {
            errors.Add(
                $"GoalRuns:DefaultMaxIterations must be between {GoalLimits.MinIterations} and " +
                $"{GoalLimits.MaxIterationsHardLimit}; got {options.DefaultMaxIterations}.");
        }

        if (options.ContinuationScanInterval < TimeSpan.FromMilliseconds(100)
            || options.ContinuationScanInterval > TimeSpan.FromMinutes(5))
            errors.Add("GoalRuns:ContinuationScanInterval must be between 100ms and 5m.");
        if (options.ContinuationLeaseDuration < TimeSpan.FromSeconds(5)
            || options.ContinuationLeaseDuration > TimeSpan.FromMinutes(30))
            errors.Add("GoalRuns:ContinuationLeaseDuration must be between 5s and 30m.");
        if (options.ConversationBusyRetryDelay < TimeSpan.FromSeconds(1)
            || options.ConversationBusyRetryDelay > TimeSpan.FromMinutes(30))
            errors.Add("GoalRuns:ConversationBusyRetryDelay must be between 1s and 30m.");
        if (options.ContinuationBatchSize is < 1 or > 64)
            errors.Add("GoalRuns:ContinuationBatchSize must be between 1 and 64.");
        if (options.ContinuationMaxAttempts is < 1 or > 20)
            errors.Add("GoalRuns:ContinuationMaxAttempts must be between 1 and 20.");
        if (options.CheckTimeoutSeconds is < 30 or > 3600)
            errors.Add("GoalRuns:CheckTimeoutSeconds must be between 30 and 3600.");
        if (options.CheckProjects is { Length: > 16 })
            errors.Add("GoalRuns:CheckProjects must contain at most 16 entries.");
        if (options.DefaultResumePolicy is not (GoalResumePolicies.Paused
            or GoalResumePolicies.AutoResumeOnRestart))
        {
            errors.Add(
                $"GoalRuns:DefaultResumePolicy must be '{GoalResumePolicies.Paused}' or " +
                $"'{GoalResumePolicies.AutoResumeOnRestart}'; got '{options.DefaultResumePolicy}'.");
        }
        if (options.MaxAutoResumesPerBoot is < 0 or > 64)
        {
            errors.Add(
                $"GoalRuns:MaxAutoResumesPerBoot must be between 0 and 64; got {options.MaxAutoResumesPerBoot}.");
        }
        if (options.NoProgressBreakerThreshold is < 1 or > 16)
        {
            errors.Add(
                $"GoalRuns:NoProgressBreakerThreshold must be between 1 and 16; got {options.NoProgressBreakerThreshold}.");
        }

        return errors;
    }
}

/// <summary>
/// ADR-092：goal_runs.resume_policy 列的合法取值。未知/空值由消费方 fail-safe 回落默认策略。
/// </summary>
public static class GoalResumePolicies
{
    /// <summary>重启后 disarm 为 paused（历史默认行为，ADR-074 §12）。</summary>
    public const string Paused = "paused";

    /// <summary>重启后保持 Active，换发 activation fence（epoch++ / bootId 更新）并落 goal.resumed 事件。</summary>
    public const string AutoResumeOnRestart = "auto_resume_on_restart";
}
