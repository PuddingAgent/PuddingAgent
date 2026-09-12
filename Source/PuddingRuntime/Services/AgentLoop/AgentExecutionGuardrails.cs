namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Agent Loop 执行预算与护栏配置。MaxRounds/MaxToolCallsTotal/MaxElapsed 是
/// 「系统 profile 默认值」：请求未显式携带预算时回退生效；显式请求值忠实采纳
/// （超限拒绝由上游校验层完成），本层不以其为上界钳制。其余为行为护栏，
/// 防止死循环、资源耗尽与无进展僵局。
/// 注册为 Singleton 后可通过标准 Options 模式覆盖默认值。
/// </summary>
public sealed record AgentExecutionGuardrails
{
    /// <summary>配置节名（系统级覆盖入口，在 Runtime DependencyInjection 中绑定）。</summary>
    public const string SectionName = "AgentLoop:Guardrails";

    /// <summary>
    /// 系统默认迭代轮次（每轮 = 一次 LLM 调用），请求未显式携带 MaxRounds 时回退生效。
    /// 默认派生自契约唯一权威常量 SubAgentExecutionOptions.LargeTaskMaxRounds；
    /// 显式请求值（8/32/600/1200/10000…）忠实采纳，不作为上界钳制。
    /// </summary>
    public int MaxRounds { get; init; } =
        PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxRounds;

    /// <summary>
    /// 系统默认累计工具调用次数预算。请求未携带显式预算（0/负数）时回退生效；
    /// manifest 或子代理系统预算有值时以请求值为准。默认派生自契约唯一权威
    /// 常量 SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal；显式请求值
    /// 忠实采纳，不作为上界钳制。
    /// </summary>
    public int MaxToolCallsTotal { get; init; } =
        PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal;

    /// <summary>
    /// 最大允许总耗时的系统默认值（不可续期的最终保险丝；正常的卡死检测
    /// 由 Run 级滑动无进展看门狗负责）。默认派生自契约常量
    /// SubAgentExecutionOptions.LargeTaskMaxTimeoutSeconds。
    /// </summary>
    public TimeSpan MaxElapsed { get; init; } =
        TimeSpan.FromSeconds(PuddingCode.Runtime.SubAgentExecutionOptions.LargeTaskMaxTimeoutSeconds);

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
