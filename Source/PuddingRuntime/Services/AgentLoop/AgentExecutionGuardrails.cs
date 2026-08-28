namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Agent Loop 执行护栏配置——约束多轮执行的各类上界，防止死循环、资源耗尽与无进展僵局。
/// 注册为 Singleton 后可通过标准 Options 模式覆盖默认值。
/// </summary>
public sealed record AgentExecutionGuardrails
{
    /// <summary>配置节名（系统级覆盖入口，在 Runtime DependencyInjection 中绑定）。</summary>
    public const string SectionName = "AgentLoop:Guardrails";

    /// <summary>最大迭代轮次（每轮 = 一次 LLM 调用）。默认支持大型子任务的 600 轮。</summary>
    public int MaxRounds { get; init; } =
        PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxRounds;

    /// <summary>
    /// 累计工具调用次数的系统级默认预算。请求未携带显式预算（agent manifest
    /// 未配置等）时生效；manifest 或子代理系统预算有值时以请求值为准。默认 400。
    /// </summary>
    public int MaxToolCallsTotal { get; init; } = 400;

    /// <summary>
    /// 整体执行的最大允许总耗时。它是不可续期的最终保险丝；
    /// 正常的卡死检测由 Run 级滑动无进展看门狗负责。
    /// </summary>
    public TimeSpan MaxElapsed { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// 相同工具 + 相同参数哈希在连续轮次中最多允许重复的次数。
    /// 超出后注入引导消息，强迫 LLM 换策略或声明 FAILED。默认 3。
    /// </summary>
    public int MaxSameToolRepeat { get; init; } = 3;

    /// <summary>
    /// 连续无进展轮次上限——连续若干个 CONTINUE 轮次均无工具调用（LLM 只输出文本）时触发。
    /// 超出后注入系统引导消息，提示 LLM 调用工具、收口或声明 FAILED。默认 3。
    /// </summary>
    public int MaxNoProgressRounds { get; init; } = 3;

    /// <summary>
    /// 连续只调用 <c>search_tools</c>、但始终不执行任何已发现业务工具的上限。
    /// 查询文本变化也计入同一 discovery-only 族；达到上限后以
    /// <c>tool_discovery_stalled</c> 终止，避免通过换词绕过精确参数重复护栏。
    /// </summary>
    public int MaxConsecutiveToolDiscoveryCalls { get; init; } = 8;
}
