using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-04 model & memory slice: chat / memory / embedding provider-model pairs, memory search mode and
/// reasoning effort, for both the global template and a workspace role instance.
///
/// Every save reads the stored record first and submits it back with only these fields replaced, so
/// prompts, Markdown documents and policy fields this page never shows are preserved. Core stays the
/// authority: it resolves the profile and validates model ownership again at execution time.
/// </summary>
internal sealed partial class DesktopAgentDirectorySettings
{
    public Task<IReadOnlyList<AgentModelCatalogEntry>> ListModelCatalogAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("agents.models.catalog", async (scope, token) =>
        {
            var llm = scope.Services.GetRequiredService<LlmProviderFileService>();
            var config = await llm.LoadAsync(token);
            var catalog = new List<AgentModelCatalogEntry>();
            foreach (var provider in config.Providers)
                foreach (var model in provider.Models)
                    catalog.Add(new AgentModelCatalogEntry(provider.ProviderId, provider.Name, model.ModelId, model.Name,
                        model.IsEmbedding, provider.IsEnabled, model.IsDeprecated));
            return (IReadOnlyList<AgentModelCatalogEntry>)catalog;
        }, cancellationToken);

    public Task<AgentModelPolicy> ReadTemplateModelPolicyAsync(string templateId, CancellationToken cancellationToken = default)
        => Templates("agents.models.template.read", async (service, token) =>
        {
            var template = await service.GetTemplateAsync(templateId, token)
                ?? throw new InvalidOperationException($"模板 {templateId} 不存在。");
            return new AgentModelPolicy(
                new AgentModelChoice(template.PreferredProviderId ?? "", template.PreferredModelId ?? ""),
                new AgentModelChoice(template.MemoryLlmProviderId ?? "", template.MemoryLlmModelId ?? ""),
                new AgentModelChoice(template.EmbeddingProviderId ?? "", template.EmbeddingModelId ?? ""),
                AgentModelPolicyText.NormalizeMemorySearchMode(template.MemorySearchMode),
                AgentModelPolicyText.NormalizeReasoningEffort(template.ReasoningEffort));
        }, cancellationToken);

    public Task SaveTemplateModelPolicyAsync(string templateId, AgentModelPolicy policy, CancellationToken cancellationToken = default)
        => Templates("agents.models.template.save", async (service, token) =>
        {
            Guard(policy);
            var current = await service.GetTemplateAsync(templateId, token)
                ?? throw new InvalidOperationException($"模板 {templateId} 不存在。");
            await service.UpdateTemplateAsync(templateId, TemplateRequest(current,
                preferredProviderId: policy.Chat.ProviderId, preferredModelId: policy.Chat.ModelId,
                memoryLlmProviderId: policy.Memory.ProviderId, memoryLlmModelId: policy.Memory.ModelId,
                embeddingProviderId: policy.Embedding.ProviderId, embeddingModelId: policy.Embedding.ModelId,
                memorySearchMode: AgentModelPolicyText.NormalizeMemorySearchMode(policy.MemorySearchMode),
                reasoningEffort: AgentModelPolicyText.NormalizeReasoningEffort(policy.ReasoningEffort)), token);
            return true;
        }, cancellationToken);

    public Task<AgentModelPolicy> ReadInstanceModelPolicyAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => Instances("agents.models.instance.read", async (service, token) =>
        {
            var agent = await service.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            return new AgentModelPolicy(
                new AgentModelChoice(agent.PreferredProviderId ?? "", agent.PreferredModelId ?? ""),
                new AgentModelChoice(agent.MemoryLlmProviderId ?? "", agent.MemoryLlmModelId ?? ""),
                new AgentModelChoice(agent.EmbeddingProviderId ?? "", agent.EmbeddingModelId ?? ""),
                AgentModelPolicyText.NormalizeMemorySearchMode(agent.MemorySearchMode),
                AgentModelPolicyText.NormalizeReasoningEffort(agent.ReasoningEffort));
        }, cancellationToken);

    public Task SaveInstanceModelPolicyAsync(string workspaceId, string agentId, AgentModelPolicy policy, CancellationToken cancellationToken = default)
        => Instances("agents.models.instance.save", async (service, token) =>
        {
            Guard(policy);
            var current = await service.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            await service.UpdateAgentProfileAsync(workspaceId, agentId, new UpdateWorkspaceAgentRequest(
                current.Name, current.Description, current.DisplayName, current.AvatarId, current.AvatarUrl,
                current.SourceTemplateId, current.SystemPromptOverride,
                policy.Chat.ProviderId, policy.Chat.ModelId,
                current.IsEnabled, current.HeartbeatPrompt, current.Role, current.SystemPrompt,
                current.UserPromptTemplate, AgentModelPolicyText.NormalizeMemorySearchMode(policy.MemorySearchMode),
                AgentModelPolicyText.NormalizeReasoningEffort(policy.ReasoningEffort), current.MaxRounds,
                current.MaxElapsedSeconds, current.MaxToolCallsTotal, current.ContainerImage,
                policy.Memory.ProviderId, policy.Memory.ModelId,
                policy.Embedding.ProviderId, policy.Embedding.ModelId,
                current.AllowFileWrite, current.AllowShellExecution, current.AllowNetworkAccess,
                current.SelectedCapabilityIds, current.SkillPackageIds, current.AllowedToolNames,
                current.ExplorerModel, current.ResearcherModel, current.PlannerModel, current.ReviewerModel,
                current.DeveloperModel, current.DeployerModel, current.TesterModel), token);
            return true;
        }, cancellationToken);

    /// <summary>
    /// A half-filled pair is never a valid inheritance signal: Core's chat path rejects it too, so the
    /// boundary refuses it rather than writing a provider without a model.
    /// </summary>
    private static void Guard(AgentModelPolicy policy)
    {
        if (policy.Chat.IsPartial || policy.Memory.IsPartial || policy.Embedding.IsPartial)
            throw new ArgumentException("服务商与模型必须同时指定，或同时留空表示继承。");
    }

    /// <summary>
    /// Builds the full template upsert from the stored template. Null means "keep the stored value";
    /// pass an empty string to clear a field explicitly.
    /// </summary>
    private static UpsertGlobalAgentTemplateRequest TemplateRequest(
        GlobalAgentTemplateDto current,
        string? name = null, string? description = null, string? role = null,
        bool? isEnabled = null, int? sortOrder = null, string? avatarId = null,
        string? systemPrompt = null, string? userPromptTemplate = null, string? personaPrompt = null,
        string? agentsPrompt = null, string? toolsDescription = null, string? bootstrapTemplate = null,
        string? memoryPrompt = null, string? preferredProviderId = null, string? preferredModelId = null,
        string? memoryLlmProviderId = null, string? memoryLlmModelId = null,
        string? embeddingProviderId = null, string? embeddingModelId = null,
        string? memorySearchMode = null, string? reasoningEffort = null)
        => new(
            current.TemplateId,
            name ?? current.Name,
            description ?? current.Description,
            role ?? current.Role,
            systemPrompt ?? current.SystemPrompt,
            userPromptTemplate ?? current.UserPromptTemplate,
            preferredProviderId ?? current.PreferredProviderId,
            preferredModelId ?? current.PreferredModelId,
            current.MaxContextTokens,
            current.ContainerImage,
            current.SelectedCapabilityIds,
            current.SelectedSkillPackageIds,
            isEnabled ?? current.IsEnabled,
            sortOrder ?? current.SortOrder,
            personaPrompt ?? current.PersonaPrompt,
            toolsDescription ?? current.ToolsDescription,
            bootstrapTemplate ?? current.BootstrapTemplate,
            null,
            avatarId ?? current.AvatarId,
            memoryLlmProviderId ?? current.MemoryLlmProviderId,
            memoryLlmModelId ?? current.MemoryLlmModelId,
            embeddingProviderId ?? current.EmbeddingProviderId,
            embeddingModelId ?? current.EmbeddingModelId,
            memorySearchMode ?? current.MemorySearchMode,
            reasoningEffort ?? current.ReasoningEffort,
            current.MaxRounds,
            current.MaxElapsedSeconds,
            current.MaxToolCallsTotal,
            current.ConsciousProfileId,
            current.SubconsciousProfileId,
            agentsPrompt ?? current.AgentsPrompt,
            memoryPrompt ?? current.MemoryPrompt);
}
