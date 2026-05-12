namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Agent Loop 执行护栏配置——约束多轮执行的各类上界，防止死循环、资源耗尽与无进展僵局。
/// 注册为 Singleton 后可通过标准 Options 模式覆盖默认值。
/// </summary>
public sealed record AgentExecutionGuardrails
{
    /// <summary>最大迭代轮次（每轮 = 一次 LLM 调用）。默认 200。</summary>
    public int MaxRounds { get; init; } = 200;

    /// <summary>整体执行的最大允许总耗时。超出后强制停止。默认 20 分钟。</summary>
    public TimeSpan MaxElapsed { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>整次执行内工具调用总次数上限。默认 100。</summary>
    public int MaxToolCallsTotal { get; init; } = 100;

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
}
