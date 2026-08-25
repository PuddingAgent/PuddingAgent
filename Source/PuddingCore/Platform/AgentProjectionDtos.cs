namespace PuddingCode.Platform;

/// <summary>Workspace contact-list projection for an Agent in the chat client.</summary>
public sealed record AgentStatusProjection(
    string WorkspaceId,
    string OwnerUserId,
    string AgentId,
    string MainSessionId,
    string Status,
    string? ActiveRunId,
    string Summary,
    int UnreadCount,
    long EventCursor,
    DateTimeOffset UpdatedAt);

/// <summary>Renderable lifecycle view for one Agent run.</summary>
public sealed record AgentRunView(
    string RunId,
    string WorkspaceId,
    string OwnerUserId,
    string AgentId,
    string MainSessionId,
    string? CommandClientId,
    string Status,
    string StatusText,
    string Summary,
    long EventCursor,
    AgentOutputSnapshot OutputSnapshot,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Current output snapshot for an Agent run.</summary>
public sealed record AgentOutputSnapshot(
    string Markdown,
    IReadOnlyList<ProcessSummaryItem> ProcessItems,
    ConversationProcessSummary? ProcessSummary = null,
    TurnEventWindow? Window = null);

/// <summary>
/// Turn 事件窗口边界（AgentTurnCard 重构 2026-08-25）：快照/明细返回的事件
/// 集合不再是无边界信息的一页——前端据此判断截断（HasMoreBefore）并对齐
/// canonical sequence 游标。
/// </summary>
public sealed record TurnEventWindow(
    string TurnId,
    long ThroughSequence,
    long MinSequence,
    long MaxSequence,
    bool HasMoreBefore);

/// <summary>Compact process item shown through progressive disclosure in chat UI.</summary>
public sealed record ProcessSummaryItem(
    string Id,
    string Kind,
    string Status,
    string Text,
    DateTimeOffset Timestamp,
    /// <summary>Canonical event sequence（2026-08-25 硬切必填：前端不再合成顺序）。</summary>
    long Sequence,
    string? Name = null,
    string? Arguments = null,
    string? Output = null,
    int? ExitCode = null,
    string? Message = null,
    string? ToolCallId = null,
    string? DelegationRunId = null,
    string? TurnId = null,
    string? RunId = null);

/// <summary>
/// Payload-free statistics for a completed message process. Historical details are loaded on demand.
/// </summary>
public sealed record ConversationProcessSummary(
    int TotalItems,
    int ThinkingRounds,
    int ThinkingSteps,
    int ToolCalls,
    int ToolResults,
    int FailedTools,
    long DurationMs,
    bool HasDetails);

/// <summary>Full process details for one persisted conversation message.</summary>
public sealed record MessageProcessDetailsView(
    string MessageId,
    string? RunId,
    IReadOnlyList<ProcessSummaryItem> ProcessItems,
    TurnEventWindow? Window = null);

/// <summary>Renderable conversation projection for one Agent main session.</summary>
public sealed record AgentConversationView(
    string WorkspaceId,
    string OwnerUserId,
    string AgentId,
    string MainSessionId,
    IReadOnlyList<ConversationMessageView> Messages,
    AgentRunView? ActiveRun,
    long EventCursor,
    DateTimeOffset UpdatedAt);

/// <summary>Renderable chat message in an Agent conversation projection.</summary>
public sealed record ConversationMessageView(
    string MessageId,
    string? RunId,
    string Role,
    string SourceId,
    string SourceName,
    DateTimeOffset CreatedAt,
    string Content,
    string Status,
    IReadOnlyList<ProcessSummaryItem> ProcessItems)
{
    /// <summary>Canonical conversation Turn identity shared by the user message and Agent reply.</summary>
    public string? TurnId { get; init; }

    /// <summary>Business/UI source kind. This is distinct from the LLM transcript role.</summary>
    public string SourceKind { get; init; } = "";

    /// <summary>Message fabric or UI message type, for example user_message, agent_message, or agent_reply.</summary>
    public string MessageType { get; init; } = "";

        /// <summary>Role used when feeding the message to the LLM, when it differs from UI/business role.</summary>
    public string LlmRole { get; init; } = "";

    /// <summary>Optional message-level metadata (e.g. inputMode, channel routing facts).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// ADR-077：服务端 authoritative 内容部件安全摘要（type/artifactId/detail）。
    /// 图片字节、路径与 Provider file_id 不会出现在投影中。
    /// </summary>
    public IReadOnlyList<ConversationContentPartView>? ContentParts { get; init; }

    /// <summary>
    /// Compact historical process statistics. Full event payloads are intentionally excluded from
    /// the initial conversation projection and can be requested for this message on demand.
    /// </summary>
    public ConversationProcessSummary? ProcessSummary { get; init; }
}

/// <summary>投影用内容部件安全摘要（ADR-077）。</summary>
public sealed record ConversationContentPartView(
    string Type,
    string? ArtifactId,
    string? Detail);
