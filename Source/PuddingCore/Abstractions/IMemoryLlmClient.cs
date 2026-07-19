using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 记忆专用 LLM 客户端。
/// 用于记忆分类/评分/摘要，独立于主聊天模型以降低成本。
/// </summary>
public interface IMemoryLlmClient
{
    /// <summary>
    /// 分类一条消息：是闲聊还是值得记忆。
    /// 返回分类结果与评分。
    /// </summary>
    Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default);

    /// <summary>
    /// 摘要一组旧记忆，生成合并后的新记忆文本。
    /// </summary>
    Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default);

    /// <summary>
    /// 解析用户消息的意图，提取用于记忆检索的查询参数。
    /// </summary>
    Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default);

    /// <summary>
    /// 通用对话接口，支持 Tool/Function calling（用于记忆深度探索）。
    /// tools 为 null 或空时表示纯对话模式。
    /// </summary>
    Task<string> ChatAsync(string systemPrompt, string userMessage, IReadOnlyList<object>? tools = null, CancellationToken ct = default);

    /// <summary>
    /// 通用对话接口（带可选记忆模型覆盖配置）。
    /// 默认实现忽略 memoryLlmConfig；具体实现不得静默回退到主聊天模型。
    /// </summary>
    Task<string> ChatWithConfigAsync(
        string systemPrompt,
        string userMessage,
        MemoryLlmConfig? memoryLlmConfig,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
        => ChatAsync(systemPrompt, userMessage, tools, ct);

    /// <summary>
    /// 通用对话接口（带可选记忆模型覆盖配置），同时返回 LLM token 用量。
    /// 默认实现忽略 usage（返回 null），由支持 token 追踪的实现覆盖。
    /// </summary>
    async Task<(string Text, TokenUsageDto? Usage)> ChatWithUsageAsync(
        string systemPrompt,
        string userMessage,
        MemoryLlmConfig? memoryLlmConfig,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
    {
        var text = await ChatWithConfigAsync(systemPrompt, userMessage, memoryLlmConfig, tools, ct);
        return (text, null);
    }

    /// <summary>
    /// 通用对话接口（带目标记忆所有权边界）。
    /// 调用者身份仍是框架潜意识；targetScope 决定本次维护属于哪个 workspace/agent/session。
    /// </summary>
    Task<string> ChatWithScopedConfigAsync(
        string systemPrompt,
        string userMessage,
        MemoryLlmConfig? memoryLlmConfig,
        SubconsciousMemoryScope targetScope,
        IReadOnlyList<object>? tools = null,
        CancellationToken ct = default)
        => ChatWithConfigAsync(systemPrompt, userMessage, memoryLlmConfig, tools, ct);
}

/// <summary>记忆分类结果。</summary>
public sealed record MemoryClassification(
    bool IsWorthRemembering,
    double ImportanceScore,
    double Confidence,
    string? SuggestedTag,
    string? Summary);

/// <summary>记忆检索意图结果。</summary>
public sealed record MemoryQueryIntent
{
    /// <summary>意图类型：task_progress / preference / past_conversation / factual / general。</summary>
    public string IntentType { get; init; } = "general";

    /// <summary>提取的关键实体（如项目名、人名、日期）。</summary>
    public IReadOnlyList<string> Entities { get; init; } = Array.Empty<string>();

    /// <summary>时间范围：recent(7天) / recent_month(30天) / months_ago(60-180天) / any。</summary>
    public string TimeRange { get; init; } = "any";

    /// <summary>语义搜索查询（用于 FTS5 fallback）。</summary>
    public string SearchQuery { get; init; } = string.Empty;

    /// <summary>建议的 Tag 前缀（用于 TagTree 检索）。</summary>
    public string? TagPrefix { get; init; }
}
