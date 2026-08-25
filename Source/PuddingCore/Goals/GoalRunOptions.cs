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

        return errors;
    }
}
