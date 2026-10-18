using PuddingConversationReadModel;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// 统一会话打开/分页查询的外部合同（阶段 C3；Plan §5.1–§5.3）。
/// <para>
/// 与旧 <c>AgentConversationView</c> 并存到阶段 D 切换前端；本阶段只新增只读查询入口，
/// 检索全部走有界查询与读模型，不在 GET 中重放/修复/回填。
/// </para>
/// </summary>
public enum ConversationSyncState
{
    Ready = 0,
    Syncing = 1,
}

/// <summary>最近消息页/历史页的单条有界消息。</summary>
public sealed record ConversationHistoryItem(
    string MessageId,
    string Role,
    string? TurnId,
    long CreatedAt,
    string Preview,
    bool HasMoreContent,
    int ContentLength,
    bool HasContentParts);

/// <summary>历史消息页结果；键集游标由最后一条返回项确定。</summary>
public sealed record ConversationHistoryPage(
    IReadOnlyList<ConversationHistoryItem> Items,
    long? NextBeforeCreatedAt,
    string? NextBeforeMessageId,
    bool HasMore);

/// <summary>Turn 摘要的只读视图（有界预览/计数/完整性均来自读模型）。</summary>
public sealed record ConversationTurnSummaryView(
    string TurnId,
    string? RootRunId,
    string? UserMessageId,
    string? AssistantMessageId,
    TurnTerminalState TerminalState,
    bool HasAssistantBody,
    ProcessSummary Process,
    string ActivePreview,
    bool ActivePreviewTruncated,
    long FirstSequence,
    long LastSequence,
    int Revision);

/// <summary>有界子代理摘要视图；溢出由 <see cref="ConversationRuntimeView.SubAgentRunsOmitted"/> 表达。</summary>
public sealed record ConversationSubAgentSummaryView(
    string RunId,
    string? SubAgentId,
    string? ParentRunId,
    string? TurnId,
    SubAgentRunLifecycleState State,
    long FirstSequence,
    long LastSequence);

/// <summary>首屏必需运行态：压缩状态 + 有界子代理摘要 + 读模型版本/修订。</summary>
public sealed record ConversationRuntimeView(
    bool CompactionRunning,
    string? CompactionRunId,
    bool CompactionLastAttemptFailed,
    IReadOnlyList<ConversationSubAgentSummaryView> SubAgentRuns,
    int SubAgentRunsOmitted,
    ReadModelInitializationState Initialization,
    int ReadModelVersion,
    int Revision);

/// <summary>
/// 统一打开快照：一次读事务内固定 <see cref="SnapshotCursor"/> 与 <see cref="EventHead"/> 的一致视图。
/// <see cref="RepresentationTag"/> 用于表示层条件请求（ETag）；不包含正文全文或事件重放结果。
/// </summary>
public sealed record ConversationOpenSnapshot(
    string ConversationId,
    long SnapshotCursor,
    long EventHead,
    ReadModelInitializationState Initialization,
    ConversationSyncState SyncState,
    int? RetryAfterMs,
    string RepresentationTag,
    ConversationHistoryPage History,
    ConversationTurnSummaryView? ActiveTurn,
    IReadOnlyList<ConversationTurnSummaryView> VisibleTurns,
    ConversationRuntimeView Runtime,
    SummaryCompleteness Completeness,
    string? CompletenessReason);

/// <summary>历史页查询（键集边界为 createdAt + messageId；首屏不传边界）。</summary>
public sealed record ConversationHistoryQuery(
    string ConversationId,
    long? BeforeCreatedAt = null,
    string? BeforeMessageId = null,
    int? Limit = null);

/// <summary>过程明细页查询（canonical sequence 键集 + 固定 through 上界）。</summary>
public sealed record ConversationProcessQuery(
    string ConversationId,
    string? TurnId = null,
    long? BeforeSequence = null,
    long? ThroughSequence = null,
    int? Limit = null);

/// <summary>过程明细条目（有界预览 + 结构化工具/子代理字段；全文走详情/分段读取）。</summary>
public sealed record ConversationProcessItem(
    long Sequence,
    string EventId,
    string Type,
    string Preview,
    bool PreviewTruncated,
    int OriginalPayloadBytes,
    string? ToolCallId = null,
    string? Name = null,
    string? Status = null,
    int? ExitCode = null,
    string? Error = null,
    string? SubAgentId = null,
    string? RunId = null,
    string? Arguments = null,
    string? Output = null);

/// <summary>
/// 单条消息正文的有界分段读取（Plan §5.4）：按 UTF-8 字节边界分页，永不整段加载超大正文；
/// 普通短消息一次返回全文（<c>HasMore=false</c>）。
/// </summary>
public sealed record ConversationMessageContentPage(
    string ConversationId,
    string MessageId,
    long OffsetBytes,
    long? NextOffsetBytes,
    long TotalBytes,
    string Content,
    bool HasMore);

/// <summary>过程明细页结果；空展示页也带可前进的扫描边界。</summary>
public sealed record ConversationProcessPage(
    IReadOnlyList<ConversationProcessItem> Items,
    long? NextBeforeSequence,
    bool HasMoreBefore,
    long ScannedBackThroughSequence,
    int OmittedNonDisplayable,
    string Truncation);

/// <summary>查询失败原因（控制器映射 400/409；不得用空会话或零计数掩盖）。</summary>
public sealed class ConversationQueryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
