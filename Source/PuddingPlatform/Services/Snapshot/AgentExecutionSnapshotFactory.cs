using PuddingCode.Platform;

namespace PuddingPlatform.Services.Snapshot;

/// <summary>
/// ADR-059: Agent Execution Snapshot Factory — 组装 Agent/Template/LLM/Skill 配置为不可变快照。
/// 初始实现：返回简化快照，后续阶段接入完整的 Agent/Template/Provider/Skill 文件解析。
/// </summary>
public sealed class AgentExecutionSnapshotFactory(
    ILogger<AgentExecutionSnapshotFactory> logger) : IAgentExecutionSnapshotFactory
{
    public Task<AgentExecutionSnapshot> CreateAsync(
        string workspaceId, string agentId,
        AgentExecutionSnapshot? previousSnapshot, CancellationToken ct)
    {
        if (previousSnapshot is not null)
        {
            logger.LogDebug("[SnapshotFactory] Reusing previous snapshot {SnapshotId}",
                previousSnapshot.SnapshotId);
            return Task.FromResult(previousSnapshot);
        }

        var snapshot = new AgentExecutionSnapshot(
            SnapshotId: Guid.NewGuid().ToString("N"),
            WorkspaceId: workspaceId,
            AgentId: agentId,
            Revision: 0,
            SnapshotHash: $"sha256:{Guid.NewGuid():N}"[..48],
            DisplayName: null,
            AvatarUrl: null,
            SystemPrompt: null,
            PersonaJson: null,
            ProviderId: null,
            ModelId: null,
            CapabilityPolicy: null,
            ToolDefinitions: null,
            SkillReferences: null,
            MemoryPolicyJson: null,
            BudgetTotalTokens: null,
            BudgetMaxRounds: null,
            Timeout: null,
            CreatedAt: DateTimeOffset.UtcNow);

        logger.LogInformation("[SnapshotFactory] Created snapshot={SnapshotId} agent={AgentId}",
            snapshot.SnapshotId, agentId);

        return Task.FromResult(snapshot);
    }

    public Task<AgentExecutionSnapshot?> FindByIdAsync(string snapshotId, CancellationToken ct)
    {
        logger.LogDebug("[SnapshotFactory] FindById {SnapshotId} — not yet persisted", snapshotId);
        return Task.FromResult<AgentExecutionSnapshot?>(null);
    }
}
