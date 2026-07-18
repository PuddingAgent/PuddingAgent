namespace PuddingCode.Abstractions;

/// <summary>
/// Persists chat transcripts (user + assistant messages).
/// Core-facing contract; Platform provides the EF Core implementation.
/// </summary>
public interface IChatTranscriptWriter
{
    Task<long?> PersistMessageAsync(
        string sessionId,
        string role,
        string content,
        long createdAt,
        string? thinkingJson = null,
        string? usageJson = null,
        string? workspaceId = null,
        string? agentInstanceId = null,
        string? agentTemplateId = null,
        string? messageId = null,
        string? turnId = null,
        string? commandId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Lightweight DTO for workspace agent listing used by Runtime tools.
/// </summary>
public sealed class AgentInfo
{
    public string AgentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? SourceTemplateId { get; init; }
    public bool IsEnabled { get; init; }
}

/// <summary>
/// Query service for workspace agent instances.
/// Core-facing contract; Platform provides the adapter.
/// </summary>
public interface IWorkspaceAgentQueryService
{
    Task<IReadOnlyList<AgentInfo>> ListAgentsAsync(string workspaceId, CancellationToken ct = default);
}
