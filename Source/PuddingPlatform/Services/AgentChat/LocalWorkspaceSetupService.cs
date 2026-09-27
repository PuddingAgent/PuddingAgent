using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>Local first-use business flow, independent of web accounts and controllers.</summary>
public sealed class LocalWorkspaceSetupService(PlatformDbContext db, WorkspaceAgentFileService agents,
    LlmProviderFileService providers)
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    public async Task<string> EnsureAsync(string workspaceId, string workspaceName, string roleName,
        string? providerId, string? modelId, CancellationToken ct)
    {
        if (workspaceId.Length is < 1 or > 48 || !char.IsAsciiLetterOrDigit(workspaceId[0])
            || workspaceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("工作空间标识不合法。");
        if (string.IsNullOrWhiteSpace(workspaceName) || workspaceName.Length > 128
            || string.IsNullOrWhiteSpace(roleName) || roleName.Length > 80)
            throw new ArgumentException("工作空间或角色名称不合法。");
        await SetupGate.WaitAsync(ct);
        try
        {
            var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
            if (workspace is { IsEnabled: false } or { IsFrozen: true })
                throw new InvalidOperationException("工作空间已停用或冻结。");
            // Retrying completion reuses the established role, without modifying its settings.
            if (workspace is not null)
            {
                var existing = await agents.ListAgentsAsync(workspaceId, ct);
                if (existing.Count > 0) return existing[0].AgentId;
            }
            if (providerId is not null || modelId is not null)
            {
                var provider = (await providers.ListProvidersAsync(ct)).FirstOrDefault(p => p.ProviderId == providerId && p.IsEnabled);
                if (provider is null || !(await providers.ListModelsAsync(provider.ProviderId, ct))
                    .Any(m => m.ModelId == modelId && !m.IsDeprecated && !m.IsEmbedding))
                    throw new InvalidOperationException("所选模型已不可用，请重新打开初始化窗口选择。");
            }
            if (workspace is null)
            {
                // Commit the workspace before file-backed role creation. A failed role creation can be retried.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var team = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == "platform-team", ct);
                if (team is null)
                {
                    team = new TeamEntity { TeamId = "platform-team", Name = "本机工作空间", IsEnabled = true };
                    db.Teams.Add(team); await db.SaveChangesAsync(ct);
                }
                workspace = new WorkspaceEntity { WorkspaceId = workspaceId, Slug = workspaceId,
                    TeamEntityId = team.Id, Name = workspaceName, Description = "Desktop 本机工作空间",
                    TeamAccessPolicy = WorkspaceAccessPolicy.None, CompanyAccessPolicy = WorkspaceAccessPolicy.None };
                db.Workspaces.Add(workspace); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            var agent = await agents.CreateAgentAsync(workspaceId, new CreateWorkspaceAgentRequest(
                Name: roleName, Description: "协助实现、审阅与验证代码", DisplayName: roleName,
                AvatarId: null, AvatarUrl: null, SourceTemplateId: "global:general-assistant",
                SystemPromptOverride: null, PreferredProviderId: providerId, PreferredModelId: modelId), ct);
            return agent.AgentId;
        }
        finally { SetupGate.Release(); }
    }
}
