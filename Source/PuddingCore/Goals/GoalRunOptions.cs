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
    public int ContinuationBatchSize { get; set; } = 8;
    public int ContinuationMaxAttempts { get; set; } = 5;

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

        return errors;
    }
}
