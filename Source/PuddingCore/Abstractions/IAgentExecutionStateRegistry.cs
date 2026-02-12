namespace PuddingCode.Abstractions;

/// <summary>
/// Tracks the current runtime execution occupying an agent.
/// </summary>
public interface IAgentExecutionStateRegistry
{
    /// <summary>
    /// Gets the current agent execution availability snapshot.
    /// </summary>
    AgentExecutionAvailability Get(string workspaceId, string agentId);

    /// <summary>
    /// Marks an agent as busy for a runtime execution when it is currently idle.
    /// </summary>
    bool TryBegin(string workspaceId, string agentId, string executionId, string? currentTask);

    /// <summary>
    /// Marks an agent idle when the matching runtime execution completes.
    /// </summary>
    bool Complete(string workspaceId, string agentId, string executionId);
}
