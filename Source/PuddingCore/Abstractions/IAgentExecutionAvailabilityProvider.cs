namespace PuddingCode.Abstractions;

/// <summary>
/// Provides current execution availability for an agent before automatic runtime dispatch.
/// </summary>
public interface IAgentExecutionAvailabilityProvider
{
    /// <summary>
    /// Gets whether the agent can accept a new automatic message delivery execution.
    /// </summary>
    Task<AgentExecutionAvailability> GetAsync(string workspaceId, string agentId, CancellationToken ct = default);
}

/// <summary>
/// Snapshot of an agent's execution availability.
/// </summary>
public sealed record AgentExecutionAvailability(
    string WorkspaceId,
    string AgentId,
    string Status,
    string? CurrentExecutionId,
    string? CurrentTask,
    DateTime? LastCompletedAt = null)
{
    /// <summary>
    /// True when message delivery dispatch may start a new runtime execution.
    /// Agents that just completed an execution (< 60 s ago) are treated as busy
    /// to avoid heartbeat interruptions of ongoing user conversations.
    /// </summary>
    public bool CanStartMessageDelivery =>
        string.Equals(Status, "idle", StringComparison.OrdinalIgnoreCase)
        && (!LastCompletedAt.HasValue
            || (DateTime.UtcNow - LastCompletedAt.Value) > HeartbeatAvoidanceCooldown);

    /// <summary>
    /// Minimum delay after an agent execution completes before a heartbeat may be delivered.
    /// Gives the agent time to settle — e.g., finish streaming a long response, or await
    /// the user's next message — before the system injects a heartbeat.
    /// </summary>
    public static readonly TimeSpan HeartbeatAvoidanceCooldown = TimeSpan.FromSeconds(60);
}
