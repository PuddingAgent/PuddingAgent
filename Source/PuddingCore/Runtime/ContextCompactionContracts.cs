using System.Text.Json.Serialization;

using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>Shared defaults for automatic context pressure protection.</summary>
public static class ContextCompactionDefaults
{
    public const double TriggerRatio = 0.80;
}

/// <summary>
/// 上下文门禁（gate）阈值常量。口径：分母为 <see cref="ContextHealthSnapshot.EffectiveWindowTokens"/>
/// （有效输入窗口 = min(模型窗口 - 预留输出 - 安全缓冲, provider 输入上限)），语义是「输入还剩多少」。
/// 2026-09-22 超限事故可见性修复：这组阈值原先是 <c>ContextHealthEvaluator</c> 里的裸字面量
/// （0.60 / 0.75 / 0.92），现集中为常量，既供评估器引用，也可随 context_health 诊断输出，
/// 避免「显示口径（UsageRatio，分母=模型窗口）」与「门禁口径」再次混淆。
/// </summary>
public static class ContextHealthGateThresholds
{
    /// <summary>Warning：输入占用达到有效输入窗口的 60%。</summary>
    public const double WarningRatio = 0.60;

    /// <summary>Unhealthy：输入占用达到有效输入窗口的 75%。</summary>
    public const double UnhealthyRatio = 0.75;

    /// <summary>Critical：输入占用达到压缩触发阈值（默认 0.80，可被 AutoCompactionThreshold 覆盖）。</summary>
    public const double TriggerRatio = ContextCompactionDefaults.TriggerRatio;

    /// <summary>Blocking：输入占用达到有效输入窗口的 92%，发送前必须硬门禁。</summary>
    public const double BlockingRatio = 0.92;
}

/// <summary>门禁阈值快照（随 context_health 诊断输出，用于归因门禁判定口径）。</summary>
public sealed record ContextHealthThresholds(
    double Warning,
    double Unhealthy,
    double Trigger,
    double Blocking)
{
    /// <summary>默认阈值（0.60 / 0.75 / TriggerRatio / 0.92）。</summary>
    public static ContextHealthThresholds Default { get; } = new(
        ContextHealthGateThresholds.WarningRatio,
        ContextHealthGateThresholds.UnhealthyRatio,
        ContextHealthGateThresholds.TriggerRatio,
        ContextHealthGateThresholds.BlockingRatio);
}

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

/// <summary>
/// 压缩终态语义（A2 无收益抑制）。Applied 是唯一算成功的终态；
/// Skipped* 表示本次未实际写入摘要（无收益抑制 / 无候选 / 当前轮守卫 / 会话冷却）；
/// Failed 表示异常失败（不永久屏蔽，可恢复，由调用方 catch 侧归类）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextCompactionOutcome>))]
public enum ContextCompactionOutcome
{
    Applied,
    SkippedNoGain,
    SkippedNoCandidate,
    SkippedCurrentTurn,
    SkippedCooldown,
    Failed,
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
    bool ShouldBlockSend,
    double GateRatio = 0)
{
    /// <summary>
    /// 门禁阈值常量快照。默认取 <see cref="ContextHealthGateThresholds"/> 常量默认值，
    /// 评估器会把本次实际生效的触发阈值写回 <see cref="ContextHealthThresholds.Trigger"/>。
    /// </summary>
    public ContextHealthThresholds GateThresholds { get; init; } = ContextHealthThresholds.Default;

    public string UsageSource { get; init; } = "unknown";
    public string UsageConfidence { get; init; } = "estimated";
    public string? UsageRecordedAtUtc { get; init; }
    public int? MessageTokens { get; init; }
    public int? ToolDefinitionTokens { get; init; }
    public int? SystemMessageTokens { get; init; }
    public int? HistoryMessageTokens { get; init; }
    /// <summary>系统提示词层（不含压缩摘要）。</summary>
    public int? SystemPromptTokens { get; init; }
    /// <summary>压缩摘要层（压缩后记忆占用）。</summary>
    public int? CompactionSummaryTokens { get; init; }
    /// <summary>对话消息层。</summary>
    public int? ConversationTokens { get; init; }
    /// <summary>工具结果层。</summary>
    public int? ToolResultTokens { get; init; }
    /// <summary>思维链层（已从角色桶中扣除）。</summary>
    public int? ReasoningTokens { get; init; }
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
    IReadOnlyList<SkillPackageInfo>? SkillPackages = null,
    IReadOnlyList<string>? PreCompactionFacts = null)
{
    /// <summary>P0-4f-1a step6: 本次压缩所属执行的 trace_id（可空；不可得时显式传 null，不 fallback 生成）。</summary>
    public string? TraceId { get; init; }
}

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
    ContextCompactionDiagnostics? Diagnostics = null,
    bool SkippedDueToTokenIncrease = false,
    bool SkippedDueToCurrentTurnGuard = false,
    ContextCompactionOutcome Outcome = ContextCompactionOutcome.Applied);

/// <summary>
/// 压缩覆盖清单（方案 §6.3）。
/// 持久化为一次成功或失败的 Compact 生成的覆盖事实：记录了本次压缩到底覆盖了哪些
/// source message、前后代际、字节/Token 变化，以及是否降级或失败。
/// 数据库事务只允许依据 manifest 中确实被覆盖（OmittedCount == 0）的 message id
/// 写入 <c>CompactedBy</c>，二者必须落在同一个事务里，防止「选中待压缩集合」与
/// 「实际送入摘要的集合」脱节。
/// </summary>
public sealed record CompactionCoverageManifest(
    string CompactionId,
    string SessionId,
    int SourceGeneration,
    int TargetGeneration,
    IReadOnlyList<string> SourceMessageIds,
    IReadOnlyList<string> SourceHashes,
    int CoveredCount,
    int OmittedCount,
    int DuplicateCount,
    long RawUtf8BytesBefore,
    long RawUtf8BytesAfter,
    long TokensBefore,
    long TokensAfter,
    string? FinalSummaryId,
    string? FinalSummaryHash,
    string Generator,
    bool Degraded,
    string? FailureReason);

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

public sealed record ActiveCompactionSnapshot(string CompactionId, DateTimeOffset StartedAt);

public interface IContextCompactionService
{
    /// <summary>
    /// 该会话当前是否有在途压缩（进程内权威事实，随重启归零）。
    /// 客户端不得用「事件序列里最后一个压缩事件是 started」推断运行态：终态事件丢失
    /// 或进程重启都会留下孤儿 started，会被误报成「正在压缩」，并在每次刷新后复活。
    /// </summary>
    /// <remarks>
    /// 默认实现返回 false：既有实现者（测试替身）无需同步修改，且对它们而言“无在途压缩”
    /// 本就是正确语义。真实实现在 <c>ContextCompactionService</c>。若将来新增包装/装饰器，
    /// 必须显式转发，不得依赖默认值（否则刷新后会漏棒真在跑的压缩）。
    /// </remarks>
    bool IsCompactionRunning(string sessionId) => false;

    ActiveCompactionSnapshot? GetActiveCompaction(string sessionId) => null;

        Task<ContextHealthSnapshot> GetHealthAsync(
        string sessionId,
        CancellationToken ct = default,
        int? contextWindowTokens = null,
        int? maxOutputTokens = null,
        int? maxInputTokens = null,
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
        string? traceId,
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
    string? FlushContent = null,
    IReadOnlyList<string>? Facts = null)
{
    public bool Success => FactsExtracted > 0;
}
