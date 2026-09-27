using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Tools;
using PuddingPlatform.Services;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-04 capability/skill grants (the tab that DS-06 and DS-07 unblocked).
///
/// Templates store their grants and are the source new instances inherit from. Instances hold an
/// independent snapshot taken at creation: sending null keeps that snapshot, sending an empty list
/// clears it. This adapter never claims a live inheritance link between the two.
/// </summary>
internal sealed partial class DesktopAgentDirectorySettings
{
    public Task<AgentGrantOptions> ListGrantOptionsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("agents.grants.options", async (scope, token) =>
        {
            var tools = scope.Services.GetRequiredService<IPuddingToolCatalogService>().ListTools();
            var capabilities = tools
                .OrderBy(tool => tool.SortOrder)
                .ThenBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
                .Select(tool => new AgentGrantOption(tool.ToolId, tool.Name, tool.Category.ToString(),
                    ToolPluginText.IsExecutable(tool.RuntimeStatus), "capability"))
                .ToArray();
            var packages = await scope.Services.GetRequiredService<PuddingPlatform.Services.SkillPackageService>()
                .ListAsync(token);
            var skills = packages
                .OrderBy(package => package.SortOrder)
                .ThenBy(package => package.SkillPackageId, StringComparer.OrdinalIgnoreCase)
                .Select(package => new AgentGrantOption(package.SkillPackageId,
                    $"{package.Name}（{package.Version}）", package.FileName ?? "", package.IsEnabled, "skillPackage"))
                .ToArray();
            return new AgentGrantOptions(capabilities, skills);
        }, cancellationToken);

    public Task<AgentGrantSet> ReadTemplateGrantsAsync(string templateId, CancellationToken cancellationToken = default)
        => Templates("agents.grants.template.read", async (service, token) =>
        {
            var template = await service.GetTemplateAsync(templateId, token)
                ?? throw new InvalidOperationException($"模板 {templateId} 不存在。");
            return new AgentGrantSet(AgentGrantText.Normalize(template.SelectedCapabilityIds),
                AgentGrantText.Normalize(template.SelectedSkillPackageIds));
        }, cancellationToken);

    public Task SaveTemplateGrantsAsync(string templateId, AgentGrantSet grants, CancellationToken cancellationToken = default)
        => Templates("agents.grants.template.save", async (service, token) =>
        {
            var existing = await service.GetTemplateAsync(templateId, token)
                ?? throw new InvalidOperationException($"模板 {templateId} 不存在。");
            await service.UpdateTemplateAsync(templateId, TemplateRequest(existing,
                capabilities: AgentGrantText.Normalize(grants.CapabilityIds),
                skillPackages: AgentGrantText.Normalize(grants.SkillPackageIds)), token);
            return true;
        }, cancellationToken);

    public Task<AgentInstanceGrantState> ReadInstanceGrantsAsync(
        string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("agents.grants.instance.read", async (scope, token) =>
        {
            var agent = await scope.Services.GetRequiredService<WorkspaceAgentFileService>()
                .GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            var grants = new AgentGrantSet(AgentGrantText.Normalize(agent.SelectedCapabilityIds),
                AgentGrantText.Normalize(agent.SkillPackageIds));
            // The template supplies the comparison baseline only; the snapshot itself was taken at creation.
            var templateGrants = AgentGrantSet.Empty;
            if (!string.IsNullOrWhiteSpace(agent.SourceTemplateId))
            {
                var template = await scope.Services.GetRequiredService<AgentTemplateFileService>()
                    .GetTemplateAsync(agent.SourceTemplateId, token);
                if (template is not null)
                    templateGrants = new AgentGrantSet(AgentGrantText.Normalize(template.SelectedCapabilityIds),
                        AgentGrantText.Normalize(template.SelectedSkillPackageIds));
            }
            return new AgentInstanceGrantState(agent.AgentId, agent.DisplayName ?? agent.Name,
                agent.SourceTemplateId ?? "", grants, templateGrants);
        }, cancellationToken);

    public Task SaveInstanceGrantsAsync(string workspaceId, string agentId, AgentGrantSelection capabilities,
        AgentGrantSelection skillPackages, CancellationToken cancellationToken = default)
        => Instances("agents.grants.instance.save", async (service, token) =>
        {
            var current = await service.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            await service.UpdateAgentProfileAsync(workspaceId, agentId, InstanceRequest(current,
                capabilities: capabilities, skillPackages: skillPackages), token);
            return true;
        }, cancellationToken);

    /// <summary>Unspecified keeps the stored snapshot (Core receives null); otherwise the explicit list is sent.</summary>
    private static List<string>? GrantValue(AgentGrantSelection? selection, List<string>? stored) =>
        selection is null ? stored : selection.Unspecified ? null : [.. AgentGrantText.Normalize(selection.Ids)];
}
