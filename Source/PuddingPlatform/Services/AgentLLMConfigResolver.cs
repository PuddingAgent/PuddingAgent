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

        var roleBinding = normalizedRole == AgentLlmRoleIds.Subconscious
            ? profile.LlmConfig.Subconscious
            : profile.LlmConfig.Conscious;
        config = ApplyRequestedReplyLimit(
            config,
            roleBinding?.MaxReplyTokens ?? profile.Instance.MaxReplyTokens);

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
            var cfg = ApplyRequestedReplyLimit(result.Config, binding.MaxReplyTokens);
            if (!string.IsNullOrWhiteSpace(binding.ReasoningEffort) && cfg.ReasoningEffort is null)
                cfg = cfg with { ReasoningEffort = binding.ReasoningEffort };
            result = result with { Config = cfg };
        }

        return Task.FromResult(result);
    }

    private static LlmConfig ApplyRequestedReplyLimit(LlmConfig config, int? requestedMaxReplyTokens)
    {
        if (requestedMaxReplyTokens is not > 0)
            return config;

        var modelLimit = config.MaxOutputTokens is > 0
            ? config.MaxOutputTokens.Value
            : requestedMaxReplyTokens.Value;
        return config with
        {
            MaxOutputTokens = Math.Min(modelLimit, requestedMaxReplyTokens.Value),
        };
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
