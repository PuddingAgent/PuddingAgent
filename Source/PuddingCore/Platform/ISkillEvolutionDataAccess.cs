namespace PuddingCode.Platform;

/// <summary>
/// Lightweight row for execution commands used by skill evolution queries.
/// Defined in PuddingCore to allow PuddingRuntime to consume without referencing PuddingPlatform.
/// </summary>
public sealed class ExecutionCommandRow
{
    public string CommandId { get; init; } = string.Empty;
    public string UserMessageId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string AgentInstanceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? TurnId { get; init; }
    public long? CompletedAt { get; init; }
    public long CreatedAt { get; init; }
    public string? MetadataJson { get; init; }
}

/// <summary>
/// Lightweight row for conversation events used by skill evolution queries.
/// </summary>
public sealed class ConversationEventRow
{
    public string CommandId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public long Sequence { get; init; }
}

/// <summary>
/// Data access for skill evolution trajectory queries.
/// Implemented by PuddingPlatform using PlatformDbContext.
/// </summary>
public interface ISkillEvolutionDataAccess
{
    /// <summary>
    /// Get recent successful execution commands for an agent.
    /// Ordered by CompletedAt desc (or CreatedAt if null), up to 'limit' rows.
    /// </summary>
    Task<IReadOnlyList<ExecutionCommandRow>> GetRecentSuccessfulCommandsAsync(
        string workspaceId, string agentInstanceId, int limit, CancellationToken ct);

    /// <summary>
    /// Get conversation events for a set of command IDs filtered by event types.
    /// Ordered by Sequence ascending.
    /// </summary>
    Task<IReadOnlyList<ConversationEventRow>> GetEventsByCommandIdsAsync(
        string[] commandIds, string[] eventTypes, CancellationToken ct);

    /// <summary>
    /// Get message contents by message IDs. Returns a dictionary of MessageId -> Content.
    /// </summary>
    Task<Dictionary<string, string>> GetMessageContentsByIdsAsync(
        string[] messageIds, CancellationToken ct);
}
