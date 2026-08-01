using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// LLM 配置解析器实现：
/// - 从文件化 GlobalAgentTemplate + WorkspaceAgentTemplates 读取 LLM 路由配置
/// - 不受 IsEnabled 限制——只过滤 TemplateId 匹配
/// - Provider/Model 详情只从 data/config/llm.providers.json 解析
/// </summary>
public sealed class AgentLLMConfigResolver : ILLMConfigResolver
{
    private readonly AgentTemplateFileService _templateFileService;
    private readonly AgentProfileProvider _agentProfileProvider;
    private readonly ILlmConfigService _llmConfigService;
    private readonly ILogger<AgentLLMConfigResolver> _logger;

    public AgentLLMConfigResolver(
        AgentTemplateFileService templateFileService,
        AgentProfileProvider agentProfileProvider,
        ILlmConfigService llmConfigService,
        ILogger<AgentLLMConfigResolver> logger)
    {
        _templateFileService = templateFileService;
        _agentProfileProvider = agentProfileProvider;
        _llmConfigService = llmConfigService;
        _logger = logger;
    }

    public async Task<AgentRoleLlmRoutingConfig> ResolveRoleAsync(
        string workspaceId,
        string configurationAgentInstanceId,
        string roleId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationAgentInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);

        AgentFileProfile profile;
        try
        {
            profile = await _agentProfileProvider.LoadAsync(configurationAgentInstanceId, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InvalidOperationException)
        {
            throw new AgentConfigurationException(
                configurationAgentInstanceId,
                $"Agent '{configurationAgentInstanceId}' definition is incomplete or invalid: {ex.Message}");
        }

        if (!string.Equals(profile.Instance.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentConfigurationException(
                configurationAgentInstanceId,
                $"Agent '{configurationAgentInstanceId}' belongs to workspace " +
                $"'{profile.Instance.WorkspaceId}', not '{workspaceId}'.");
        }

        var normalizedRole = roleId.Trim().ToLowerInvariant();
        var (providerId, modelId, profileId) = ResolveRoleBinding(profile, normalizedRole);
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            throw new AgentConfigurationException(
                configurationAgentInstanceId,
                $"Agent '{configurationAgentInstanceId}' is missing the provider/model binding for role '{normalizedRole}'.");
        }

        var config = _llmConfigService.Resolve(providerId, modelId);
        if (config is null)
        {
            throw new AgentConfigurationException(
                configurationAgentInstanceId,
                $"Agent '{configurationAgentInstanceId}' role '{normalizedRole}' references unavailable route " +
                $"'{providerId}/{modelId}' in data/config/llm.providers.json. No fallback model was selected.");
        }

        if (!string.Equals(config.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentConfigurationException(
                configurationAgentInstanceId,
                $"Agent '{configurationAgentInstanceId}' role '{normalizedRole}' resolved model " +
                $"'{config.ModelId}', but the configured model is '{modelId}'.");
        }

        var resolvedProfileId = string.IsNullOrWhiteSpace(profileId)
            ? $"agent:{configurationAgentInstanceId}:{normalizedRole}"
            : profileId;

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var configuredProfile = _llmConfigService.ResolveProfile(profileId);
            if (configuredProfile is null
                || !string.Equals(configuredProfile.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(configuredProfile.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentConfigurationException(
                    configurationAgentInstanceId,
                    $"Agent '{configurationAgentInstanceId}' role '{normalizedRole}' profile '{profileId}' " +
                    $"does not resolve to the configured route '{providerId}/{modelId}'.");
            }
        }

        _logger.LogInformation(
            "[LLMConfig] Resolved Agent role route workspace={WorkspaceId} agent={AgentId} role={RoleId} provider={ProviderId} profile={ProfileId} model={ModelId}",
            workspaceId,
            configurationAgentInstanceId,
            normalizedRole,
            providerId,
            resolvedProfileId,
            modelId);

        return new AgentRoleLlmRoutingConfig
        {
            RoleId = normalizedRole,
            ConfigurationAgentInstanceId = configurationAgentInstanceId,
            ProviderId = providerId,
            ProfileId = resolvedProfileId,
            ModelId = modelId,
            Config = config,
            SearchMode = normalizedRole == AgentLlmRoleIds.Subconscious
                ? profile.Instance.MemorySearchMode ?? "deep"
                : "deep",
        };
    }

    public async Task<LlmRoutingConfig?> ResolveConsciousAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (canonicalId, isExplicitGlobal) = NormalizeTemplateId(templateId);
        if (string.IsNullOrWhiteSpace(canonicalId))
            return null;

        var global = await _templateFileService.GetTemplateAsync(canonicalId, ct);

        // 工作区模板已迁移到文件管理，不再从 DB 查询 workspace-specific 覆盖
        var profileId = global?.ConsciousProfileId;
        var resolvedProfile = string.IsNullOrWhiteSpace(profileId)
            ? null
            : _llmConfigService.ResolveProfile(profileId);
        var providerId = resolvedProfile?.ProviderId ?? global?.PreferredProviderId;
        var modelId = resolvedProfile?.ModelId ?? global?.PreferredModelId;
        var config = resolvedProfile?.Config
            ?? (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)
                ? null
                : _llmConfigService.Resolve(providerId, modelId));
        var reasoningEffort = global?.ReasoningEffort;
        if (config is not null && config.ReasoningEffort is null && !string.IsNullOrWhiteSpace(reasoningEffort))
            config = config with { ReasoningEffort = reasoningEffort };

        return new LlmRoutingConfig
        {
            ProfileId = resolvedProfile?.ProfileId ?? profileId,
            ProviderId = providerId,
            ModelId = config?.ModelId ?? modelId,
            Endpoint = config?.Endpoint,
#pragma warning disable CS0618
            ApiKey = config?.ApiKey,
#pragma warning restore CS0618
            Config = config,
        };
    }

    public async Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (canonicalId, isExplicitGlobal) = NormalizeTemplateId(templateId);
        if (string.IsNullOrWhiteSpace(canonicalId))
            return null;

        // 全局模板以文件模板为主源；工作区覆盖已迁移到文件管理
        var global = await _templateFileService.GetTemplateAsync(canonicalId, ct);

        var providerId = global?.MemoryLlmProviderId;
        var modelId = global?.MemoryLlmModelId;
        var searchMode = global?.MemorySearchMode ?? "deep";

        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                $"Memory LLM provider/model must be configured explicitly. template={canonicalId} " +
                $"workspace={workspaceId ?? "(none)"} provider={providerId ?? "(none)"} model={modelId ?? "(none)"}.");
        }

        var providerConfig = _llmConfigService.Resolve(providerId, modelId);
        if (providerConfig is null)
        {
            throw new InvalidOperationException(
                $"Memory LLM provider/model not found or disabled in data/config/llm.providers.json. provider={providerId} model={modelId}.");
        }
        modelId = providerConfig.ModelId;

        if (string.IsNullOrWhiteSpace(providerConfig.Endpoint)
            || string.IsNullOrWhiteSpace(providerConfig.ApiKey)
            || string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                $"Memory LLM config is incomplete in data/config/llm.providers.json. provider={providerId} model={modelId ?? "(none)"}.");
        }

        _logger.LogDebug(
            "[LLMConfig] Resolved memory config template={Template} ws={Workspace} endpoint={HasEndpoint} model={Model} mode={Mode}",
            canonicalId,
            workspaceId,
            !string.IsNullOrWhiteSpace(providerConfig.Endpoint),
            modelId,
            searchMode);

        return new MemoryLlmRoutingConfig
        {
            ProviderId = providerId,
            Endpoint = providerConfig.Endpoint,
#pragma warning disable CS0618
            ApiKey = providerConfig.ApiKey,
#pragma warning restore CS0618
            ModelId = modelId,
            SearchMode = searchMode,
        };
    }

        // ── 新接口：基于 Agent 实例 LLM binding，不依赖模板文件 ──

    public Task<LlmRoutingConfig?> ResolveAsync(
        AgentLlmBinding binding,
        CancellationToken ct = default)
    {
        // Callers must supply an explicit provider/model pair. The provider registry
        // enriches the pair with endpoint/credentials but never selects a default.
        var providerId = binding.ProviderId;
        var modelId = binding.ModelId;

        LlmRoutingConfig? result = null;

        // Resolve endpoint/credentials from llm.providers.json by provider/model
        if (!string.IsNullOrWhiteSpace(providerId)
            && !string.IsNullOrWhiteSpace(modelId))
        {
            var config = _llmConfigService.Resolve(providerId, modelId);
            if (config is not null)
            {
                modelId = config.ModelId;
                result = new LlmRoutingConfig
                {
                    ProviderId = providerId,
                    ModelId = modelId,
                    Endpoint = config.Endpoint,
#pragma warning disable CS0618
                    ApiKey = config.ApiKey,
#pragma warning restore CS0618
                    Config = config,
                };
            }
        }

        // Apply agent-level reasoning effort override
        if (result?.Config is not null)
        {
            var cfg = result.Config;
            if (!string.IsNullOrWhiteSpace(binding.ReasoningEffort) && cfg.ReasoningEffort is null)
                cfg = cfg with { ReasoningEffort = binding.ReasoningEffort };
            result = result with { Config = cfg };
        }

        return Task.FromResult(result);
    }

    public Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        AgentLlmBinding binding,
        CancellationToken ct = default)
    {
        var providerId = binding.ProviderId;
        var modelId = binding.ModelId;

        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return Task.FromResult<MemoryLlmRoutingConfig?>(null);

        var config = _llmConfigService.Resolve(providerId, modelId);
        if (config is null)
            return Task.FromResult<MemoryLlmRoutingConfig?>(null);

        return Task.FromResult<MemoryLlmRoutingConfig?>(new MemoryLlmRoutingConfig
        {
            ProviderId = providerId,
            Endpoint = config.Endpoint,
#pragma warning disable CS0618
            ApiKey = config.ApiKey,
#pragma warning restore CS0618
            ModelId = config.ModelId,
            SearchMode = "deep",
        });
    }

    private static (string? ProviderId, string? ModelId, string? ProfileId) ResolveRoleBinding(
        AgentFileProfile profile,
        string roleId)
    {
        return roleId switch
        {
            AgentLlmRoleIds.Conscious => (
                profile.Instance.PreferredProviderId,
                profile.Instance.PreferredModelId,
                profile.LlmConfig.Conscious?.ProfileId),
            AgentLlmRoleIds.Subconscious => (
                profile.Instance.MemoryLlmProviderId,
                profile.Instance.MemoryLlmModelId,
                profile.LlmConfig.Subconscious?.ProfileId),
            AgentLlmRoleIds.Explorer => ParseModelRoute(profile.Instance.ExplorerModel, roleId),
            AgentLlmRoleIds.Researcher => ParseModelRoute(profile.Instance.ResearcherModel, roleId),
            AgentLlmRoleIds.Planner => ParseModelRoute(profile.Instance.PlannerModel, roleId),
            AgentLlmRoleIds.Reviewer => ParseModelRoute(profile.Instance.ReviewerModel, roleId),
            AgentLlmRoleIds.Developer => ParseModelRoute(profile.Instance.DeveloperModel, roleId),
            AgentLlmRoleIds.Deployer => ParseModelRoute(profile.Instance.DeployerModel, roleId),
            AgentLlmRoleIds.Tester => ParseModelRoute(profile.Instance.TesterModel, roleId),
            _ => throw new AgentConfigurationException(
                profile.Instance.AgentInstanceId,
                $"Unknown Agent LLM role '{roleId}'."),
        };
    }

    private static (string? ProviderId, string? ModelId, string? ProfileId) ParseModelRoute(
        string? route,
        string roleId)
    {
        if (string.IsNullOrWhiteSpace(route))
            return (null, null, null);

        var parts = route.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new InvalidOperationException(
                $"Agent role '{roleId}' route '{route}' must use the format 'providerId/modelId'.");
        }

        return (parts[0], parts[1], null);
    }

    private static (string CanonicalId, bool IsExplicitGlobal) NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? (templateId[globalPrefix.Length..], true)
            : (templateId, false);
    }
}
