namespace PuddingRuntime.Services.GoalMode;

/// <summary>
/// Goal 模式配置。配置节：GoalMode。默认关闭——不改变任何 Agent 的现有行为。
/// </summary>
public sealed class GoalModeOptions
{
    public const string SectionName = "GoalMode";

    /// <summary>总开关。默认 false（北极星原则：不改变默认语义）。</summary>
    public bool Enabled { get; set; }

    /// <summary>单个目标最大注入次数；达到后自动跳过该目标（熔断，防无限循环）。</summary>
    public int MaxInjectionsPerGoal { get; set; } = 3;

    /// <summary>注入信封在队列中的最大积压目标数（超出部分追加时拒绝，防失控膨胀）。</summary>
    public int MaxQueueLength { get; set; } = 20;
}