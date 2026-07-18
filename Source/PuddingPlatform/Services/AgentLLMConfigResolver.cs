using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
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
    private readonly ILlmConfigService _llmConfigService;
    private readonly ILogger<AgentLLMConfigResolver> _logger;

    public AgentLLMConfigResolver(
        AgentTemplateFileService templateFileService,
        ILlmConfigService llmConfigService,
        ILogger<AgentLLMConfigResolver> logger)
    {
        _templateFileService = templateFileService;
        _llmConfigService = llmConfigService;
        _logger = logger;
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
            ?? (string.IsNullOrWhiteSpace(providerId)
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

        LlmConfig? providerConfig;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            providerConfig = _llmConfigService.GetMemoryConfig();
            if (providerConfig is null)
            {
                throw new InvalidOperationException(
                    $"Memory LLM provider/model is not configured. template={canonicalId} workspace={workspaceId ?? "(none)"} provider={providerId ?? "(none)"} model={modelId ?? "(none)"}.");
            }

            modelId = providerConfig.ModelId;
            providerId = null;
        }
        else
        {
            providerConfig = _llmConfigService.Resolve(providerId, modelId);
            if (providerConfig is null)
            {
                throw new InvalidOperationException(
                    $"Memory LLM provider/model not found or disabled in data/config/llm.providers.json. provider={providerId} model={modelId}.");
            }

            modelId = providerConfig.ModelId;
        }

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
        var providerId = binding.ProviderId;
        var modelId = binding.ModelId;

        LlmRoutingConfig? result = null;

        // 优先使用 profile 解析
        if (!string.IsNullOrWhiteSpace(binding.ProfileId))
        {
            var profile = _llmConfigService.ResolveProfile(binding.ProfileId);
            if (profile is not null)
            {
                providerId ??= profile.ProviderId;
                modelId ??= profile.ModelId;
                result = new LlmRoutingConfig
                {
                    ProfileId = binding.ProfileId,
                    ProviderId = providerId,
                    ModelId = modelId,
                    Endpoint = profile.Config?.Endpoint,
#pragma warning disable CS0618
                    ApiKey = profile.Config?.ApiKey,
#pragma warning restore CS0618
                    Config = profile.Config,
                };
            }
        }

        // 从 llm.providers.json 解析 provider/model
        if (result is null && !string.IsNullOrWhiteSpace(providerId))
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

        // 应用 agent 级别的 reasoning effort 和 token 限制
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
        {
            var memoryConfig = _llmConfigService.GetMemoryConfig();
            if (memoryConfig is null)
                return Task.FromResult<MemoryLlmRoutingConfig?>(null);

            return Task.FromResult<MemoryLlmRoutingConfig?>(new MemoryLlmRoutingConfig
            {
                Endpoint = memoryConfig.Endpoint,
#pragma warning disable CS0618
                ApiKey = memoryConfig.ApiKey,
#pragma warning restore CS0618
                ModelId = memoryConfig.ModelId,
                SearchMode = "deep",
            });
        }

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

    private static (string CanonicalId, bool IsExplicitGlobal) NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? (templateId[globalPrefix.Length..], true)
            : (templateId, false);
    }
}
