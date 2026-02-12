namespace PuddingCode.Abstractions;

/// <summary>
/// Provides the agent roster visible in a workspace or room.
/// </summary>
public interface IAgentRosterProvider
{
    /// <summary>
    /// Lists agents that can participate in agent-to-agent messaging.
    /// </summary>
    Task<IReadOnlyList<AgentRosterItem>> ListAgentsAsync(
        string workspaceId,
        string roomId,
        bool includeBusy,
        bool includeFrozen,
        CancellationToken ct);
}

/// <summary>
/// Agent roster item exposed to agents and context assembly.
/// </summary>
public sealed record AgentRosterItem(
    string AgentId,
    string DisplayName,
    string Address,
    string Status,
    bool CanReceiveMessages,
    IReadOnlyList<string> Capabilities,
    string? CurrentTask);
