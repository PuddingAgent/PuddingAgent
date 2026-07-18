using System.Security.Cryptography;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Snapshot;

/// <summary>
/// ADR-059: Agent Execution Snapshot Factory — 组装 Agent/Template/LLM/Skill 配置为不可变快照。
/// 快照只消费统一解析后的 AgentRuntimeProfile，不自行读取 Agent、模板、Provider 或 Skill 存储。
/// 哈希输入显式排除 LLM 密钥与 Skill 下载地址。
/// </summary>
public sealed class AgentExecutionSnapshotFactory(
    ILogger<AgentExecutionSnapshotFactory> logger) : IAgentExecutionSnapshotFactory
{
    public Task<AgentExecutionSnapshot> CreateAsync(
        AgentRuntimeProfile profile,
        AgentExecutionSnapshot? previousSnapshot, CancellationToken ct)
    {
        if (previousSnapshot is not null)
        {
            logger.LogDebug("[SnapshotFactory] Reusing previous snapshot {SnapshotId}",
                previousSnapshot.SnapshotId);
            return Task.FromResult(previousSnapshot);
        }

        var toolReferences = profile.ToolDefinitions?
            .Select(tool => new SnapshotToolRef(tool.Name, Version: null, Source: profile.CapabilitySource))
            .ToArray();
        var skillReferences = profile.SkillPackages?
            .Select(skill => new SnapshotSkillRef(skill.SkillPackageId, Revision: 0))
            .ToArray();
        var hashInput = JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.WorkspaceId,
            profile.AgentId,
            profile.DisplayName,
            profile.AvatarUrl,
            profile.SourceTemplateId,
            profile.ConsciousProfileId,
            profile.PreferredProviderId,
            profile.PreferredModelId,
            profile.CapabilityPolicy,
            tools = profile.ToolDefinitions?.Select(tool => tool.Name).OrderBy(name => name),
            skills = profile.SkillPackages?
                .Select(skill => new { skill.SkillPackageId, skill.Version })
                .OrderBy(skill => skill.SkillPackageId),
        });
        var snapshotHash = $"sha256:{Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant()}";

        var snapshot = new AgentExecutionSnapshot(
            SnapshotId: Guid.NewGuid().ToString("N"),
            WorkspaceId: profile.WorkspaceId,
            AgentId: profile.AgentId,
            Revision: 0,
            SnapshotHash: snapshotHash,
            DisplayName: profile.DisplayName,
            AvatarUrl: profile.AvatarUrl,
            SystemPrompt: null,
            PersonaJson: null,
            ProviderId: profile.PreferredProviderId,
            ProfileId: profile.ConsciousProfileId,
            ModelId: profile.PreferredModelId,
            CapabilityPolicy: profile.CapabilityPolicy,
            ToolDefinitions: toolReferences,
            SkillReferences: skillReferences,
            MemoryPolicyJson: null,
            BudgetTotalTokens: null,
            BudgetMaxRounds: null,
            Timeout: null,
            CreatedAt: DateTimeOffset.UtcNow);

        logger.LogInformation("[SnapshotFactory] Created snapshot={SnapshotId} agent={AgentId}",
            snapshot.SnapshotId, profile.AgentId);

        return Task.FromResult(snapshot);
    }

    public Task<AgentExecutionSnapshot?> FindByIdAsync(string snapshotId, CancellationToken ct)
    {
        logger.LogDebug("[SnapshotFactory] FindById {SnapshotId} — not yet persisted", snapshotId);
        return Task.FromResult<AgentExecutionSnapshot?>(null);
    }
}
