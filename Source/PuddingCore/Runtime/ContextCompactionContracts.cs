using System.Text.Json.Serialization;

using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<ContextHealthState>))]
public enum ContextHealthState
{
    Healthy,
    Warning,
    Unhealthy,
    Critical,
    Blocking,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContextCompactionMode>))]
public enum ContextCompactionMode
{
    Manual,
    Auto,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContextCompactionLevel>))]
public enum ContextCompactionLevel
{
    Micro,
    SessionMemory,
    Full,
}

public sealed record ContextHealthSnapshot(
    string SessionId,
    int UsedTokens,
    int ContextWindowTokens,
    int EffectiveWindowTokens,
    int RemainingTokens,
    double UsageRatio,
    ContextHealthState State,
    bool ShouldSuggestCompact,
    bool ShouldAutoCompact,
    bool ShouldBlockSend)
{
    public string UsageSource { get; init; } = "unknown";
    public string UsageConfidence { get; init; } = "estimated";
    public string? UsageRecordedAtUtc { get; init; }
    public int? MessageTokens { get; init; }
    public int? ToolDefinitionTokens { get; init; }
    public int? SystemMessageTokens { get; init; }
    public int? HistoryMessageTokens { get; init; }
    public int? MessageCount { get; init; }
    public int? ToolCount { get; init; }
    public int? ProviderPromptTokens { get; init; }
        public int? ProviderCompletionTokens { get; init; }
    public int? ProviderTotalTokens { get; init; }
}

/// <summary>
/// Token capacity forecast for the current context window.
/// </summary>
public sealed record CapacityPrediction(
    int UsedTokens,
    int ModelWindow,
    int RemainingTokens,
    int EstimatedMessagesUntilWarning,
    int EstimatedMessagesUntilCritical,
    int EstimatedMessagesUntilBlocking,
    int AverageMessageTokens)
{
    public double UsageRatio => ModelWindow > 0 ? (double)UsedTokens / ModelWindow : 0;
    public double RemainingRatio => ModelWindow > 0 ? (double)RemainingTokens / ModelWindow : 0;
}

public sealed record ContextCompactionRequest(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    ContextCompactionMode Mode,
    ContextCompactionLevel Level,
    string Reason,
    string? AgentWorkSummary = null,
    string? CompactionId = null,
    string? AgentTemplateId = null,
    string? UserId = null,
    LlmConfig? LlmConfig = null,
    CapabilityPolicy? CapabilityPolicy = null,
    IReadOnlyList<LlmToolDefinition>? ToolDefinitions = null,
    IReadOnlyList<SkillPackageInfo>? SkillPackages = null);

public sealed record ContextCompactionDiagnostics(
    string CompactionId,
    string WorkspaceId,
    string? AgentId,
    string PreviousSessionId,
    string? NewSessionId,
    string? NewSessionTitle,
    string? PreviousLastMessageId,
    long? PreviousLastMessageSequence,
    int ActiveMessageCountBefore,
    int TextCandidateMessageCount,
    int CompactedMessageCount,
    int KeptRecentMessageCount,
    int SummaryInputMessageCount,
    long? SummaryInputFirstSequence,
    long? SummaryInputLastSequence,
    string? SummaryInputFirstMessageId,
    string? SummaryInputLastMessageId,
    string SummaryMessageId,
    int BeforeTokens,
    int AfterTokens,
    int SummaryCharacterCount,
    int SummaryEstimatedTokens,
    string StartedAtUtc,
    string CompletedAtUtc,
    long DurationMs,
    string SummaryGenerator,
    string Reason);

public sealed record ContextCompactionResult(
    string SessionId,
    string SummaryMessageId,
    ContextCompactionMode Mode,
    ContextCompactionLevel Level,
    int BeforeTokens,
    int AfterTokens,
    int CompactedMessageCount,
    string SummaryPreview,
    string SummaryMarkdown,
    IReadOnlyList<string>? MemoryNotes = null,
    ContextCompactionDiagnostics? Diagnostics = null);

public sealed record ContextCompactionSummaryRequest(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    IReadOnlyList<ContextCompactionMessage> Messages,
    string Reason,
    string? AgentWorkSummary = null,
    string? AgentTemplateId = null,
    string? UserId = null,
    LlmConfig? LlmConfig = null,
    CapabilityPolicy? CapabilityPolicy = null,
    IReadOnlyList<LlmToolDefinition>? ToolDefinitions = null,
    IReadOnlyList<SkillPackageInfo>? SkillPackages = null);

public sealed record ContextCompactionMessage(
    string MessageId,
    long Sequence,
    string Role,
    string Content);

public interface IContextCompactionService
{
        Task<ContextHealthSnapshot> GetHealthAsync(
        string sessionId,
        CancellationToken ct = default,
        int? contextWindowTokens = null,
        int? maxOutputTokens = null,
        int toolCount = 0);

    Task<ContextCompactionResult> CompactAsync(
        ContextCompactionRequest request,
        CancellationToken ct = default);
}

public interface IContextCompactionSummaryGenerator
{
    Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// 压缩事件的 SSE 推送接口。
/// 由 PuddingPlatform 实现，将生命周期事实写入 Conversation Event Store；
/// SSE 只负责从持久事件投递到前端。
/// ContextWindowManager.TryAutoCompactAsync 调用此接口通知前端压缩开始/完成/失败。
/// </summary>
public interface ISessionCompactionEventEmitter
{
    /// <summary>发送 compaction 生命周期 SSE 事件（started/completed/failed）。</summary>
    Task EmitAsync(
        string sessionId,
        string workspaceId,
        string eventType,
        object payload,
        CancellationToken ct = default);
}

/// <summary>
/// 压缩前冲洗（Pre-Compaction Flush）服务接口。
/// 借鉴 Claude Code：在上下文压缩前，用 Flash LLM 快速提取关键事实，
/// 防止压缩导致重要信息丢失。
/// </summary>
public interface IPreCompactionFlushService
{
    /// <summary>
    /// 执行冲洗：从当前会话消息中提取关键事实并保存。
    /// 失败不抛异常，由调用方降级处理。
    /// </summary>
    Task<PreCompactionFlushResult> FlushAsync(
        PreCompactionFlushRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// 压缩前冲洗请求。
/// </summary>
public sealed record PreCompactionFlushRequest(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    IReadOnlyList<ContextCompactionMessage> Messages,
    string Reason)
{
    public string? AgentTemplateId { get; init; }
    public string? AgentWorkSummary { get; init; }
}

/// <summary>
/// 压缩前冲洗结果。
/// </summary>
public sealed record PreCompactionFlushResult(
    int FactsExtracted,
    long DurationMs,
    string? FlushContent = null)
{
    public bool Success => FactsExtracted > 0;
}
