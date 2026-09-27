using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// Binds the agent directory tab to the existing Core services. Template and instance edits reload the
/// stored record first and submit it back with only the displayed fields replaced, so prompt/Markdown
/// documents and undisplayed policy fields are never cleared by a basic-info save.
/// </summary>
internal sealed partial class DesktopAgentDirectorySettings(IDesktopKernel kernel) : IAgentDirectorySettings
{
    private Task<T> Templates<T>(string operationId,
        Func<AgentTemplateFileService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<AgentTemplateFileService>(), token), cancellationToken);

    private Task<T> Instances<T>(string operationId,
        Func<WorkspaceAgentFileService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<WorkspaceAgentFileService>(), token), cancellationToken);

    public Task<IReadOnlyList<AgentWorkspaceOption>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("agents.workspaces.list", async (scope, token) =>
        {
            var db = scope.Services.GetRequiredService<PlatformDbContext>();
            return (IReadOnlyList<AgentWorkspaceOption>)await db.Workspaces.AsNoTracking()
                .OrderBy(workspace => workspace.Id)
                .Select(workspace => new AgentWorkspaceOption(workspace.WorkspaceId, workspace.Name))
                .ToArrayAsync(token);
        }, cancellationToken);

    public Task<IReadOnlyList<AgentTemplateSummary>> ListTemplatesAsync(CancellationToken cancellationToken = default)
        => Templates("agents.templates.list", async (service, token) =>
        {
            var templates = await service.ListTemplatesAsync(null, token);
            return (IReadOnlyList<AgentTemplateSummary>)templates
                .OrderBy(template => template.SortOrder)
                .ThenBy(template => template.TemplateId, StringComparer.OrdinalIgnoreCase)
                .Select(template => new AgentTemplateSummary(template.TemplateId, template.Name, template.Role,
                    template.Description ?? "", template.IsEnabled, template.IsBuiltIn, template.SortOrder,
                    template.AvatarId ?? "", template.SelectedCapabilityIds?.Count ?? 0,
                    template.SelectedSkillPackageIds?.Count ?? 0))
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<AgentAvatarOption>> ListAvatarsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("agents.avatars.list", (scope, _) =>
        {
            var catalog = scope.Services.GetRequiredService<PuddingCode.Platform.IAgentAvatarCatalog>();
            return Task.FromResult((IReadOnlyList<AgentAvatarOption>)catalog.List()
                .Where(avatar => avatar.IsEnabled)
                .OrderBy(avatar => avatar.SortOrder)
                .Select(avatar => new AgentAvatarOption(avatar.AvatarId, avatar.Name, avatar.RecommendedUse ?? "", avatar.IsEnabled))
                .ToArray());
        }, cancellationToken);

    public Task<IReadOnlyList<AgentTemplatePreset>> ListPresetsAsync(CancellationToken cancellationToken = default)
        => Templates("agents.presets.list", async (service, token) =>
        {
            var presets = await service.ListPresetTemplatesAsync(token);
            return (IReadOnlyList<AgentTemplatePreset>)presets
                .Select(preset => new AgentTemplatePreset(preset.TemplateId, preset.Name, preset.Role, preset.Description ?? ""))
                .ToArray();
        }, cancellationToken);

    public Task ImportPresetAsync(string templateId, CancellationToken cancellationToken = default)
        => Templates("agents.presets.import", async (service, token) =>
        {
            await service.ImportPresetTemplateAsync(templateId, token);
            return true;
        }, cancellationToken);

    public Task SaveTemplateAsync(AgentTemplateEdit edit, CancellationToken cancellationToken = default)
        => Templates("agents.templates.save", async (service, token) =>
        {
            var existing = await service.GetTemplateAsync(edit.TemplateId, token);
            if (existing is null)
            {
                await service.CreateTemplateAsync(new UpsertGlobalAgentTemplateRequest(
                    edit.TemplateId, edit.Name, edit.Description, edit.Role, null, null, null, null, 0, null,
                    null, null, edit.IsEnabled, edit.SortOrder, AvatarId: Avatar(edit.AvatarId)), token);
                return true;
            }
            // Submit the stored template back with only the displayed fields replaced.
            await service.UpdateTemplateAsync(edit.TemplateId, new UpsertGlobalAgentTemplateRequest(
                existing.TemplateId, edit.Name, edit.Description, edit.Role, existing.SystemPrompt, existing.UserPromptTemplate,
                existing.PreferredProviderId, existing.PreferredModelId, existing.MaxContextTokens, existing.ContainerImage,
                existing.SelectedCapabilityIds, existing.SelectedSkillPackageIds, edit.IsEnabled, edit.SortOrder,
                existing.PersonaPrompt, existing.ToolsDescription, existing.BootstrapTemplate, null,
                Avatar(edit.AvatarId), existing.MemoryLlmProviderId, existing.MemoryLlmModelId, existing.EmbeddingProviderId,
                existing.EmbeddingModelId, existing.MemorySearchMode, existing.ReasoningEffort, existing.MaxRounds,
                existing.MaxElapsedSeconds, existing.MaxToolCallsTotal, existing.ConsciousProfileId,
                existing.SubconsciousProfileId, existing.AgentsPrompt, existing.MemoryPrompt), token);
            return true;
        }, cancellationToken);

    public Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default)
        => Templates("agents.templates.delete", async (service, token) =>
        {
            await service.DeleteTemplateAsync(templateId, token);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<AgentInstanceSummary>> ListInstancesAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Instances("agents.instances.list", async (service, token) =>
        {
            var agents = await service.ListAgentsAsync(workspaceId, token);
            return (IReadOnlyList<AgentInstanceSummary>)agents
                .Select(agent => new AgentInstanceSummary(agent.AgentId, agent.Name, agent.DisplayName ?? agent.Name,
                    agent.Role ?? "", agent.Description ?? "", agent.SourceTemplateId ?? "", agent.AvatarId ?? "",
                    agent.IsEnabled, agent.IsFrozen, !string.IsNullOrWhiteSpace(agent.MainSessionId), agent.UpdatedAt))
                .ToArray();
        }, cancellationToken);

    public Task CreateInstanceAsync(AgentInstanceCreate create, CancellationToken cancellationToken = default)
        => Instances("agents.instances.create", async (service, token) =>
        {
            await service.CreateAgentAsync(create.WorkspaceId, new CreateWorkspaceAgentRequest(
                create.Name, create.Description, create.Name, null, null, create.SourceTemplateId, null, null, null), token);
            return true;
        }, cancellationToken);

    public Task SaveInstanceAsync(AgentInstanceEdit edit, CancellationToken cancellationToken = default)
        => Instances("agents.instances.save", async (service, token) =>
        {
            var current = await service.GetAgentAsync(edit.WorkspaceId, edit.AgentId, token)
                ?? throw new InvalidOperationException($"角色 {edit.AgentId} 不存在。");
            // Keep the stored profile shape; only the displayed fields change.
            await service.UpdateAgentProfileAsync(edit.WorkspaceId, edit.AgentId, new UpdateWorkspaceAgentRequest(
                edit.Name, edit.Description, edit.Name, Avatar(edit.AvatarId) ?? current.AvatarId, current.AvatarUrl, current.SourceTemplateId,
                current.SystemPromptOverride, current.PreferredProviderId, current.PreferredModelId, edit.IsEnabled,
                current.HeartbeatPrompt, edit.Role, current.SystemPrompt), token);
            return true;
        }, cancellationToken);

    public Task DeleteInstanceAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => Instances("agents.instances.delete", async (service, token) =>
        {
            await service.DeleteAgentAsync(workspaceId, agentId, token);
            return true;
        }, cancellationToken);

    public Task SetInstanceFrozenAsync(string workspaceId, string agentId, bool frozen, CancellationToken cancellationToken = default)
        => Instances("agents.instances.freeze", async (service, token) =>
        {
            await service.SetFrozenAsync(workspaceId, agentId, frozen, token);
            return true;
        }, cancellationToken);

    private static string? Avatar(string avatarId) => string.IsNullOrWhiteSpace(avatarId) ? null : avatarId;
}
