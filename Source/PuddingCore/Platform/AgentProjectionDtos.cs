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
    IReadOnlyList<ProcessSummaryItem> ProcessItems);

/// <summary>Compact process item shown through progressive disclosure in chat UI.</summary>
public sealed record ProcessSummaryItem(
    string Id,
    string Kind,
    string Status,
    string Text,
    DateTimeOffset Timestamp,
    string? Name = null,
    string? Arguments = null,
    string? Output = null,
    int? ExitCode = null,
    string? Message = null);

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

    /// <summary>Optional message-level metadata (e.g. visionArtifactId, inputMode).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
