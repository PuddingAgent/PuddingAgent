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

    /// <summary>Flash LLM 调用超时秒数。默认 60。</summary>
    public int FlashTimeoutSeconds { get; init; } = 60;

    /// <summary>压缩前冲洗（Pre-Compaction Flush）LLM 调用超时秒数，最小钳制 5 秒。默认 30。</summary>
    public int PreCompactFlushTimeoutSeconds { get; init; } = 30;

    /// <summary>语义摘要失败时是否 fallback 到 Extractive。默认 false，避免把降级摘录伪装成正常摘要。</summary>
    public bool FallbackToExtractive { get; init; }

    /// <summary>摘要后 tokens 不降反升时是否跳过压缩写入。默认 true。</summary>
    public bool SkipWhenSummaryIncreasesTokens { get; init; } = true;

    /// <summary>自动压缩触发阈值比例（0 &lt; x ≤ 1），达到此使用率时触发自动压缩。默认 0.65。</summary>
    public double AutoCompactionThreshold { get; init; } = 0.65;

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

    /// <summary>是否在水合会话历史时启用心跳/系统/非对话角色剪枝。默认 true。</summary>
    public bool EnableHistoryPruning { get; init; } = true;

    /// <summary>剪枝后保留的最大消息条数。必须 > 0，非法值回退默认 100。</summary>
    public int HistoryPruningMaxMessages { get; init; } = 100;

    /// <summary>
    /// "最近消息原样保留窗口"的单条消息尺寸上限（字节）。
    /// 保留窗口内单条消息（含 tool_result/tool_output 载荷，如 ToolCallsJson/ToolResultJson/Metadata）超过该值时，
    /// 不再原样保留全文，而是截断为"头部摘要 + 截断标记"（标记注明原始大小，
    /// 并提示完整内容可在会话原始事件流 conversation_events 中查证），
    /// 被截断的完整原文仍会以克隆形式进入摘要侧输入，照常参与摘要处理。
    /// 0 或负数表示禁用该驱逐，保持旧行为，便于回滚。默认 16*1024。
    /// </summary>
    public int MaxVerbatimMessageBytes { get; init; } = 16 * 1024;

    /// <summary>
    /// 冷启动/重水合（进程重启、内存会话过期后从 DB/JSONL 回放）的 token 预算上限。
    /// 重水合请求在 provider 前缀缓存过期后必然全量 miss；把重放体积限制在
    /// 摘要链 + 近期原文 + query 命中晋升的范围内，可把单次 miss 从数十万 token 压到该预算级。
    /// 0 或负数表示不设上限（旧行为：预算 = 整个模型窗口）。默认 49152。
    /// </summary>
    public int MaxHydrationTokenBudget { get; init; } = 49152;

    /// <summary>
    /// 会话活动原文（未压缩消息）的绝对 token 上限；估算超过该值即触发自动压缩，
    /// 与 <see cref="AutoCompactionThreshold"/>（相对窗口比例）构成 OR 条件。
    /// 大窗口模型（256K+）下仅靠 0.65×比例意味着 16 万 token 才压缩，重水合重传代价过高。
    /// 0 或负数表示禁用绝对上限。默认 131072。
    /// </summary>
    public int MaxActiveRawTokenBudget { get; init; } = 131072;
}
