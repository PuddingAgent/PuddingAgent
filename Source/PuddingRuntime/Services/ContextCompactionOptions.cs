namespace PuddingRuntime.Services;

/// <summary>
/// 上下文压缩配置选项。
/// 控制摘要生成器选择、超时和 token 增量保护行为。
/// </summary>
public sealed class ContextCompactionOptions
{
    /// <summary>摘要生成器：agent（当前 Agent 语义总结）、flash（直接 LLM）或 extractive（模板抽取）。默认 agent。</summary>
    public string SummaryGenerator { get; init; } = "agent";

    /// <summary>当前 Agent 生成压缩摘要的最长等待秒数。默认 120。</summary>
    public int AgentSummaryTimeoutSeconds { get; init; } = 120;

    /// <summary>Flash LLM 调用超时秒数。默认 20。</summary>
    public int FlashTimeoutSeconds { get; init; } = 20;

    /// <summary>语义摘要失败时是否 fallback 到 Extractive。默认 false，避免把降级摘录伪装成正常摘要。</summary>
    public bool FallbackToExtractive { get; init; }

    /// <summary>摘要后 tokens 不降反升时是否跳过压缩写入。默认 true。</summary>
    public bool SkipWhenSummaryIncreasesTokens { get; init; } = true;

    /// <summary>
    /// 等待 Agent 生成工作总结的最大重试次数。
    /// 注入提示词后若 Agent 未响应，最多重试此次数后强制压缩。默认 3。
    /// </summary>
    public int MaxWorkSummaryRetries { get; init; } = 3;

    /// <summary>
    /// 等待 Agent 生成工作总结的最大总时长（秒）。
    /// 超过此时长后不再等待，直接强制压缩。默认 180（3 分钟）。
    /// </summary>
    public int MaxWaitForWorkSummarySeconds { get; init; } = 180;
}
