using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// Agent 现场保存与恢复服务。
/// Phase 2 骨架：内存存储。Phase 4 接入 SQLite。
/// </summary>
public class AgentCheckpointService : IAgentCheckpointService
{
    private readonly ILogger<AgentCheckpointService> _logger;
    private readonly Dictionary<string, AgentCheckpoint> _store = new(); // Key = SessionId
    private readonly object _lock = new();

    public AgentCheckpointService(ILogger<AgentCheckpointService> logger)
    {
        _logger = logger;
    }

    public Task<AgentCheckpoint> SaveCheckpointAsync(
        string sessionId,
        string agentId,
        string workspaceId,
        string callStackJson,
        string? pendingToolsJson = null,
        string? contextSnapshotJson = null,
        CancellationToken ct = default)
    {
        var checkpoint = new AgentCheckpoint
        {
            SessionId = sessionId,
            AgentId = agentId,
            WorkspaceId = workspaceId,
            CallStack = callStackJson,
            PendingTools = pendingToolsJson,
            ContextSnapshot = contextSnapshotJson,
            Status = "active",
        };

        lock (_lock)
        {
            _store[sessionId] = checkpoint;
        }

        _logger.LogInformation("[AgentCheckpoint] Checkpoint saved: session={SessionId} agent={AgentId}",
            sessionId, agentId);

        return Task.FromResult(checkpoint);
    }

    public Task<AgentCheckpoint?> RestoreLatestAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_store.TryGetValue(sessionId, out var checkpoint) && checkpoint.Status == "active")
            {
                checkpoint = checkpoint with { Status = "restored" };
                _store[sessionId] = checkpoint;

                _logger.LogInformation("[AgentCheckpoint] Checkpoint restored: session={SessionId}", sessionId);
                return Task.FromResult<AgentCheckpoint?>(checkpoint);
            }
        }

        _logger.LogWarning("[AgentCheckpoint] No active checkpoint for session={SessionId}", sessionId);
        return Task.FromResult<AgentCheckpoint?>(null);
    }

    public Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _store.Remove(sessionId);
        }
        _logger.LogDebug("[AgentCheckpoint] Checkpoints deleted for session={SessionId}", sessionId);
        return Task.CompletedTask;
    }

    public Task<AgentCheckpoint?> GetActiveAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_store.TryGetValue(sessionId, out var checkpoint) && checkpoint.Status == "active")
                return Task.FromResult<AgentCheckpoint?>(checkpoint);
        }
        return Task.FromResult<AgentCheckpoint?>(null);
    }
}
