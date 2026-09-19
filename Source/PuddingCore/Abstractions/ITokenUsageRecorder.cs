using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;


namespace PuddingCode.Abstractions;

/// <summary>
/// Records token usage events for billing and analytics.
/// </summary>
public interface ITokenUsageRecorder
{
    /// <summary>
    /// Best-effort usage recording for non-authoritative telemetry callers.
    /// Implementations may log and suppress persistence failures.
    /// </summary>
    Task RecordAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null,
        string? parentSessionId = null);

    /// <summary>
    /// Required usage-fact recording. Persistence failures must propagate so
    /// the owning workflow cannot report success while silently losing billing facts.
    /// </summary>
    Task RecordRequiredAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null,
        string? parentSessionId = null);

    /// <summary>
    /// Required usage recording with canonical Agent-loop attribution. The default body keeps
    /// older recorders source-compatible while implementations that own the attribution ledger
    /// can persist round/tool/sub-agent facts.
    /// </summary>
    Task RecordAttributedRequiredAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        TokenUsageAttribution attribution,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null)
        => RecordRequiredAsync(
            usage,
            sourceType,
            sourceId,
            workspaceId,
            sessionId,
            providerId,
            modelId,
            prefixSnapshot,
            occurredAtUtc,
            attribution.ParentSessionId);
}

/// <summary>
/// Canonical per-LLM-call attribution supplied by the Agent Loop. Identity comes from
/// RuntimeExecutionIdentity; implementations must not infer sub-agent ownership from SessionId.
/// </summary>
public sealed record TokenUsageAttribution
{
    public string? ParentSessionId { get; init; }
    public string? SubAgentId { get; init; }
    public int? TurnRound { get; init; }
    public int? ToolCallCount { get; init; }
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>
    /// S01-B：Agent 发起的一次逻辑模型调用的身份，在请求准备阶段生成并随请求传递。
    /// 记录器不得自行推断该身份，也不允许用相邻请求的调试快照补齐。
    /// </summary>
    public string? InvocationId { get; init; }

    /// <summary>
    /// S01-B：实际向 Provider 发出的某一次网络请求的身份；重试产生新 ID。
    /// </summary>
    public string? AttemptId { get; init; }

    /// <summary>
    /// S01-B：请求准备阶段冻结的上下文层归因。为空表示调用方未提供请求级事实，
    /// 记录器只能回退到“写入时刻的 session 最新快照”并显式标注 provenance。
    /// </summary>
    public RequestContextAttribution? Context { get; init; }
}

/// <summary>请求级上下文归因的来源标记。</summary>
public static class RequestContextAttributionSources
{
    /// <summary>请求准备阶段冻结；唯一可作为请求级事实的来源。</summary>
    public const string RequestPrepareFrozen = "request_prepare_frozen";

    /// <summary>记录时回读 session 最新快照（legacy 兼容），不得作为请求级事实。</summary>
    public const string SessionLatestFallback = "session_latest_fallback";

    /// <summary>归因缺失。</summary>
    public const string Unknown = "unknown";
}

/// <summary>一个上下文层的冻结副本。</summary>
public sealed record RequestContextLayer
{
    public string LayerName { get; init; } = string.Empty;
    public int TokenCount { get; init; }
    public string? ContentPreview { get; init; }
    public string? FullContent { get; init; }
}

/// <summary>
/// S01-B 请求级上下文归因：构造后不可变，随 invocation/attempt 传递到 usage 回执。
/// 记录器只消费该对象，不再回读 session 最新调试缓存；旧请求迟到落账时仍保留自己的 shape。
/// </summary>
public sealed record RequestContextAttribution
{
    public string Source { get; init; } = RequestContextAttributionSources.Unknown;
    public string? SessionId { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public IReadOnlyList<RequestContextLayer> Layers { get; init; } = [];
    public int? MessageTokens { get; init; }
    public int? ToolDefinitionTokens { get; init; }
    public int? SystemMessageTokens { get; init; }
    public int? HistoryMessageTokens { get; init; }
    /// <summary>系统提示词层（Role=System 且非压缩摘要）。</summary>
    public int? SystemPromptTokens { get; init; }
    /// <summary>压缩摘要层（正文含 compact_summary 标记）。</summary>
    public int? CompactionSummaryTokens { get; init; }
    /// <summary>对话消息层（Role=User/Assistant 且非摘要）。</summary>
    public int? ConversationTokens { get; init; }
    /// <summary>工具结果层（Role=Tool 且非摘要）。</summary>
    public int? ToolResultTokens { get; init; }
    /// <summary>思维链层（已从角色桶中扣除）。</summary>
    public int? ReasoningTokens { get; init; }
    public int ToolCount { get; init; }
    public string? ToolDefinitionHash { get; init; }
    public long ToolDefinitionUtf8Bytes { get; init; }
    public long ToolDefinitionGzipBytes { get; init; }
    public double? ToolDefinitionEntropy { get; init; }
    public double? SystemMessageEntropy { get; init; }
    public double? HistoryMessageEntropy { get; init; }

    /// <summary>
    /// 在请求准备边界冻结 <paramref name="sessionId"/> 当前的上下文层与 usage 估算，
    /// 返回深拷贝；之后 session 快照被后续请求覆盖也不会改变该对象。
    /// </summary>
    public static RequestContextAttribution? Capture(
        ContextAssemblyStore? assemblyStore,
        ContextUsageSnapshotStore? usageStore,
        string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        ContextAssemblySnapshot? assembly = null;
        ContextUsageSnapshot? usage = null;
        assemblyStore?.TryGet(sessionId, out assembly);
        usageStore?.TryGet(sessionId, out usage);

        if (assembly is null && usage is null)
            return null;

        var layers = assembly is null
            ? []
            : assembly.Layers
                .Select(layer => new RequestContextLayer
                {
                    LayerName = layer.LayerName,
                    TokenCount = layer.TokenCount,
                    ContentPreview = layer.ContentPreview,
                    FullContent = layer.FullContent,
                })
                .ToList();

        return new RequestContextAttribution
        {
            Source = RequestContextAttributionSources.RequestPrepareFrozen,
            SessionId = sessionId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Layers = layers,
            MessageTokens = usage?.MessageTokens,
            ToolDefinitionTokens = usage?.ToolDefinitionTokens,
            SystemMessageTokens = usage?.SystemMessageTokens,
            HistoryMessageTokens = usage?.HistoryMessageTokens,
            // 分层六桶随请求冻结一并携带：它们是内存态快照的一部分，若不落账，
            // 进程重启后 DB 回退源就拿不到分层，面板会退化成单色条。
            SystemPromptTokens = usage?.SystemPromptTokens,
            CompactionSummaryTokens = usage?.CompactionSummaryTokens,
            ConversationTokens = usage?.ConversationTokens,
            ToolResultTokens = usage?.ToolResultTokens,
            ReasoningTokens = usage?.ReasoningTokens,
            ToolCount = usage?.ToolCount ?? 0,
            ToolDefinitionHash = usage?.ToolDefinitionHash,
            ToolDefinitionUtf8Bytes = usage?.ToolDefinitionUtf8Bytes ?? 0,
            ToolDefinitionGzipBytes = usage?.ToolDefinitionGzipBytes ?? 0,
            ToolDefinitionEntropy = usage?.ToolDefinitionEntropy,
            SystemMessageEntropy = usage?.SystemMessageEntropy,
            HistoryMessageEntropy = usage?.HistoryMessageEntropy,
        };
    }
}
