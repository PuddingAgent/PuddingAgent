using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-04 Smart sub-agent slice: the seven Smart role routes on a workspace role instance, plus the single
/// instance-request builder shared by the basic, model-policy and Smart saves so no save can silently
/// clear a field another save owns.
/// </summary>
internal sealed partial class DesktopAgentDirectorySettings
{
    public Task<IReadOnlyDictionary<string, string>> ReadSmartRoutesAsync(
        string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => Instances("agents.smart.read", async (service, token) =>
        {
            var agent = await service.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            var routes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in SmartRoleRoutes.Roles)
                routes[slot.RoleId] = CurrentRoute(agent, slot.RoleId) ?? "";
            return (IReadOnlyDictionary<string, string>)routes;
        }, cancellationToken);

    public Task SaveSmartRoutesAsync(string workspaceId, string agentId,
        IReadOnlyDictionary<string, string> routes, CancellationToken cancellationToken = default)
        => Instances("agents.smart.save", async (service, token) =>
        {
            foreach (var slot in SmartRoleRoutes.Roles)
            {
                var errors = SmartRoleRoutes.Validate(routes.GetValueOrDefault(slot.RoleId), slot.Title);
                if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
            }
            var current = await service.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            // UpdateAgentProfileAsync deliberately forces the stored Smart fields back, so a basic profile
            // edit cannot clobber routing. Writing routing therefore has to go through UpdateAgentAsync.
            await service.UpdateAgentAsync(workspaceId, agentId, InstanceRequest(current,
                smartRoutes: routes), token);
            return true;
        }, cancellationToken);

    private static string? CurrentRoute(WorkspaceAgentDto agent, string roleId) => roleId switch
    {
        "explorer" => agent.ExplorerModel,
        "researcher" => agent.ResearcherModel,
        "planner" => agent.PlannerModel,
        "reviewer" => agent.ReviewerModel,
        "developer" => agent.DeveloperModel,
        "deployer" => agent.DeployerModel,
        "tester" => agent.TesterModel,
        _ => null
    };

    private static string? RouteFor(IReadOnlyDictionary<string, string> routes, string roleId) =>
        routes.TryGetValue(roleId, out var route) ? route : null;

    /// <summary>
    /// Builds the full instance profile update from the stored instance. Null means "keep the stored
    /// value"; pass an empty string to clear a field explicitly.
    /// </summary>
    private static UpdateWorkspaceAgentRequest InstanceRequest(
        WorkspaceAgentDto current,
        string? name = null, string? description = null, string? displayName = null, string? role = null, bool? isEnabled = null,
        string? preferredProviderId = null, string? preferredModelId = null,
        string? memoryLlmProviderId = null, string? memoryLlmModelId = null,
        string? embeddingProviderId = null, string? embeddingModelId = null,
        string? memorySearchMode = null, string? reasoningEffort = null,
        IReadOnlyDictionary<string, string>? smartRoutes = null,
        int? maxRounds = null, int? maxElapsedSeconds = null, int? maxToolCallsTotal = null, string? containerImage = null,
        AgentGrantSelection? capabilities = null, AgentGrantSelection? skillPackages = null)
        => new(
            name ?? current.Name,
            description ?? current.Description,
            displayName ?? current.DisplayName,
            current.AvatarId,
            current.AvatarUrl,
            current.SourceTemplateId,
            current.SystemPromptOverride,
            preferredProviderId ?? current.PreferredProviderId,
            preferredModelId ?? current.PreferredModelId,
            isEnabled ?? current.IsEnabled,
            current.HeartbeatPrompt,
            role ?? current.Role,
            current.SystemPrompt,
            current.UserPromptTemplate,
            memorySearchMode ?? current.MemorySearchMode,
            reasoningEffort ?? current.ReasoningEffort,
            maxRounds ?? current.MaxRounds,
            maxElapsedSeconds ?? current.MaxElapsedSeconds,
            maxToolCallsTotal ?? current.MaxToolCallsTotal,
            containerImage ?? current.ContainerImage,
            memoryLlmProviderId ?? current.MemoryLlmProviderId,
            memoryLlmModelId ?? current.MemoryLlmModelId,
            embeddingProviderId ?? current.EmbeddingProviderId,
            embeddingModelId ?? current.EmbeddingModelId,
            current.AllowFileWrite,
            current.AllowShellExecution,
            current.AllowNetworkAccess,
            GrantValue(capabilities, current.SelectedCapabilityIds),
            GrantValue(skillPackages, current.SkillPackageIds),
            current.AllowedToolNames,
            smartRoutes is null ? current.ExplorerModel : RouteFor(smartRoutes, "explorer"),
            smartRoutes is null ? current.ResearcherModel : RouteFor(smartRoutes, "researcher"),
            smartRoutes is null ? current.PlannerModel : RouteFor(smartRoutes, "planner"),
            smartRoutes is null ? current.ReviewerModel : RouteFor(smartRoutes, "reviewer"),
            smartRoutes is null ? current.DeveloperModel : RouteFor(smartRoutes, "developer"),
            smartRoutes is null ? current.DeployerModel : RouteFor(smartRoutes, "deployer"),
            smartRoutes is null ? current.TesterModel : RouteFor(smartRoutes, "tester"));
}
