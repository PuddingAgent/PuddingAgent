using System.Collections.Concurrent;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services.Messaging;

/// <summary>
/// In-process agent execution state registry used by runtime dispatch gating.
/// </summary>
public sealed class AgentExecutionStateRegistry : IAgentExecutionStateRegistry
{
    private readonly ConcurrentDictionary<string, AgentExecutionAvailability> _states = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public AgentExecutionAvailability Get(string workspaceId, string agentId) =>
        _states.TryGetValue(Key(workspaceId, agentId), out var state)
            ? state
            : Idle(workspaceId, agentId);

    /// <inheritdoc />
    public bool TryBegin(string workspaceId, string agentId, string executionId, string? currentTask)
    {
        var key = Key(workspaceId, agentId);
        var busy = new AgentExecutionAvailability(
            workspaceId,
            agentId,
            Status: "busy",
            CurrentExecutionId: executionId,
            CurrentTask: currentTask);

        while (true)
        {
                        if (!_states.TryGetValue(key, out var current))
                return _states.TryAdd(key, busy);

            // 仅拒绝 busy 状态（已在执行中），idle 允许立即开始
            // heartbeat avoidance cooldown 由 MessageDeliveryDispatcher 单独检查（IAgentExecutionAvailabilityProvider.Get + CanStartMessageDelivery）
            if (string.Equals(current.Status, "busy", StringComparison.OrdinalIgnoreCase))
                return false;

            if (_states.TryUpdate(key, busy, current))
                return true;
        }
    }

    /// <inheritdoc />
    public bool Complete(string workspaceId, string agentId, string executionId)
    {
        var key = Key(workspaceId, agentId);

        while (true)
        {
            if (!_states.TryGetValue(key, out var current))
                return false;

            if (!string.Equals(current.CurrentExecutionId, executionId, StringComparison.Ordinal))
                return false;

            if (_states.TryUpdate(key, Idle(workspaceId, agentId, lastCompletedAt: DateTime.UtcNow), current))
                return true;
        }
    }

    private static AgentExecutionAvailability Idle(
        string workspaceId,
        string agentId,
        DateTime? lastCompletedAt = null) =>
        new(
            workspaceId,
            agentId,
            Status: "idle",
            CurrentExecutionId: null,
            CurrentTask: null,
            LastCompletedAt: lastCompletedAt);

    private static string Key(string workspaceId, string agentId) => $"{workspaceId}\u001f{agentId}";
}
