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
    /// <summary>R2：候选在生成期间已过期（代次/revision/覆盖状态被别的提交改变）⇒ 零写入放弃。</summary>
    SkippedStaleCandidate,
    /// <summary>R2：后台压缩在提交前发现前台需求 ⇒ 让位给下一条消息，零写入。</summary>
    SkippedForegroundDemand,
    Failed,
}

/// <summary>
/// 有效输入上限（<see cref="ContextHealthSnapshot.EffectiveWindowTokens"/>）**为什么**比模型窗口小。
/// <para>
/// UI 不能用 `windowLimit − effectiveLimit` 反推「预留输出」：这个差值可能来自实际输出预算、
/// Provider 输入上限、安全余量，或者三者叠加（诊断 2026-10-07 §4.4）。字段未知时必须显式写未知。
/// </para>
/// </summary>
public static class ContextEffectiveWindowSources
{
    /// <summary>仅由实际请求输出预算限制。</summary>
    public const string OutputReserve = "output_reserve";

    /// <summary>仅由 Provider 输入上限限制。</summary>
    public const string ProviderInputLimit = "provider_input_limit";

    /// <summary>仅由安全余量限制。</summary>
    public const string SafetyMargin = "safety_margin";

    /// <summary>由输出预算 + 安全余量共同限制（最常见）。</summary>
    public const string OutputReserveAndSafetyMargin = "output_reserve_and_safety_margin";

    /// <summary>
    /// 门禁用**内置回退输出预算**计算（容量没给出实际输出预算），因此这份「预留」不可归因到真实配置。
    /// 与 <see cref="OutputReserve"/> 区分：后者是配置里真实存在的预算。
    /// </summary>
    public const string FallbackOutputReserve = "fallback_output_reserve";

    /// <summary>没有任何预留：有效输入上限就是模型窗口。</summary>
    public const string ModelWindow = "model_window";

    /// <summary>来源未知（调用方未提供分量）。</summary>
    public const string Unknown = "unknown";
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

    /// <summary>Provider 的输入 token 上限；null = 未配置/未知（此时该约束不参与取小）。</summary>
    public int? ProviderInputLimitTokens { get; init; }

    /// <summary>
    /// 容量侧给出的**实际请求输出预算**（真实配置值）。
    /// null = 容量没有给出预算，门禁退回内置回退值计算（见 <see cref="EffectiveWindowSource"/> 的
    /// <see cref="ContextEffectiveWindowSources.FallbackOutputReserve"/>）——不得把回退值冒充用户预算。
    /// </summary>
    public int? RequestedOutputBudgetTokens { get; init; }

    /// <summary>参与门禁计算的安全余量（与出站硬预算同一冻结参数）。</summary>
    public int? SafetyBufferTokens { get; init; }

    /// <summary>
    /// 有效输入上限的受限来源，取值见 <see cref="ContextEffectiveWindowSources"/>。
    /// UI 据此为「窗口里不可用于输入的那段」命名，而不是一律猜成「预留输出」。
    /// </summary>
    public string EffectiveWindowSource { get; init; } = ContextEffectiveWindowSources.Unknown;

    public string UsageSource { get; init; } = "unknown";
    public string UsageConfidence { get; init; } = "estimated";
    public string? UsageRecordedAtUtc { get; init; }

    /// <summary>
    /// **上一次主请求**的 Provider 实测输入（prompt tokens；只取主调用槽位，压缩/审批等辅助调用
    /// 不得污染）。它与 <see cref="UsedTokens"/> 是**两个口径**：后者可能是「当前已准备好、尚未发出」
    /// 请求的预算估算。UI 必须并排展示、各自标注来源，不得互相覆盖 ——
    /// 诊断 2026-10-07 §4.1 记录的正是两者共用一个数值槽位导致的跳变。
    /// null = 本次会话还没有任何主请求实测。
    /// </summary>
    public int? LastMeasuredPromptTokens { get; init; }

    /// <summary>上述实测所对应请求的**原始（校准前）估算**，用于解释校准系数。</summary>
    public int? LastMeasuredRawEstimatedTokens { get; init; }

    /// <summary>上述实测所属调用的身份（session:trace:round:purpose），便于与日志/事件对表。</summary>
    public string? LastMeasuredInvocationId { get; init; }

    /// <summary>上述实测的记录时刻（ISO-8601，UTC）。</summary>
    public string? LastMeasuredAtUtc { get; init; }
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

    /// <summary>
    /// R2：本次压缩是否为**后台可选维护**（终态后维护/空闲维护）。后台压缩在提交前会重验前台需求，
    /// 有需求即零写入放弃（让位给下一条消息）；前台/手动压缩不受此限（它们本身就是前台工作）。
    /// </summary>
    public bool IsBackground { get; init; }
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

    /// <summary>
    /// F2：把 warm-prefix checkpoint **落库** —— 精确到行的 <c>CompactedBy</c> 覆盖标注 +
    /// 摘要行（<c>ContentType = compact_summary</c>）+ 覆盖清单 + <c>Session.CompactionGeneration</c> 自增，
    /// 全部在**同一个 <c>SaveChangesAsync</c> 事务**里完成（要么全写、要么全不写）。
    /// <para>
    /// 与 <see cref="CompactAsync"/> 的分工：候选与摘要内容由调用方（Runtime 的 warm-prefix 路径，
    /// 已经过 CAS 校验与真实重算）产出；本方法**只负责持久化**，不重新选候选、不调用模型。
    /// </para>
    /// <para>
    /// 调用方**必须**已持有压缩提交单飞租约（ADR-095 D4）—— 本方法不再自行取锁，
    /// 否则会与已持有的租约形成自死锁。
    /// </para>
    /// <para>
    /// 默认实现返回 <see cref="WarmPrefixCheckpointCommitStatus.Unsupported"/>：既有实现者（测试替身）
    /// 无需同步修改；调用方据此**降级为内存-only** 并如实上报 <c>durableCheckpoint = false</c>，
    /// 绝不假装已持久化。真实实现在 <c>ContextCompactionService</c>。
    /// </para>
    /// </summary>
    Task<WarmPrefixCheckpointCommitResult> CommitWarmPrefixCheckpointAsync(
        WarmPrefixCheckpointCommitRequest request,
        CancellationToken ct = default)
        => Task.FromResult(new WarmPrefixCheckpointCommitResult(WarmPrefixCheckpointCommitStatus.Unsupported));
}

/// <summary>warm-prefix checkpoint 落库结果状态。判据显式，调用方不得把「非 Committed」当成功。</summary>
public enum WarmPrefixCheckpointCommitStatus
{
    /// <summary>已提交：摘要行、覆盖标注、覆盖清单与代际在同一事务里写入成功。</summary>
    Committed,

    /// <summary>该实现不支持持久化（默认实现）：调用方降级为内存-only。</summary>
    Unsupported,

    /// <summary>会话行不存在（尚未落库的会话）：无可提交的持久基线。</summary>
    SessionNotFound,

    /// <summary>代际 CAS 失败：候选冻结时的代际与当前不一致，不得用旧候选覆盖新的持久事实。</summary>
    GenerationMismatch,

    /// <summary>覆盖集合中有 id 在当前库里找不到（消息未落库/已删除）：一律不写，绝不部分标注。</summary>
    CoveredMessagesNotFound,

    /// <summary>覆盖集合中有消息已被别的摘要覆盖：合并会串链，一律不写。</summary>
    AlreadyCovered,
}

/// <summary>warm-prefix checkpoint 落库请求（F2）：只传事实与 id。</summary>
public sealed record WarmPrefixCheckpointCommitRequest(
    string SessionId,
    string WorkspaceId,
    string? AgentId,
    string CompactionId,
    int ExpectedSourceGeneration,
    IReadOnlyList<string> CoveredMessageIds,
    string Summary,
    int MessagesBefore,
    int MessagesAfter,
    long TokensBefore,
    int MeasuredTokensAfter,
    string Reason,
    /// <summary>
    /// 被替换跨度的**内容键**（角色 + 正文）。与 <see cref="CoveredMessageIds"/> 并存，作为
    /// **权威回退**：内存历史对象在归一化/渲染等环节会被复制，按引用或按内存表解析都可能失效，
    /// 而库里的行是唯一权威。服务端只在「键 → 行」**唯一匹配**时才认，重复内容一律视为不可解析。
    /// 全为 null/空则只按 id 解析（保持既有语义）。
    /// </summary>
    IReadOnlyList<WarmPrefixSpanKey>? SpanKeys = null);

/// <summary>跨度实体的内容键（用于在库里回退解析持久 MessageId）。</summary>
public sealed record WarmPrefixSpanKey(string Role, string Content);

/// <summary>warm-prefix checkpoint 落库结果。</summary>
public sealed record WarmPrefixCheckpointCommitResult(
    WarmPrefixCheckpointCommitStatus Status,
    string? SummaryMessageId = null,
    int TargetGeneration = 0,
    int CoveredCount = 0,
    IReadOnlyList<string>? ProblemMessageIds = null)
{
    public bool Committed => Status == WarmPrefixCheckpointCommitStatus.Committed;
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
