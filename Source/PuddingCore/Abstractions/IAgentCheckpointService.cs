using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Agent 现场保存与恢复 — 用于 Urgent/Important 事件打断后的上下文恢复。
/// </summary>
public interface IAgentCheckpointService
{
    /// <summary>
    /// 保存 Agent 当前执行现场。sessionId 同一时刻最多一个活跃检查点。
    /// </summary>
    Task<AgentCheckpoint> SaveCheckpointAsync(
        string sessionId,
        string agentId,
        string workspaceId,
        string callStackJson,
        string? pendingToolsJson = null,
        string? contextSnapshotJson = null,
        CancellationToken ct = default);

    /// <summary>
    /// 恢复最近活跃检查点。恢复后自动标记为 restored。
    /// </summary>
    Task<AgentCheckpoint?> RestoreLatestAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 删除指定会话的所有检查点。
    /// </summary>
    Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 获取指定会话的当前活跃检查点（不恢复）。
    /// </summary>
    Task<AgentCheckpoint?> GetActiveAsync(string sessionId, CancellationToken ct = default);
}
