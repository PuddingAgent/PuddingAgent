using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
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
        var providerId = global?.PreferredProviderId;
        var modelId = global?.PreferredModelId;
        var config = string.IsNullOrWhiteSpace(providerId)
            ? null
            : _llmConfigService.Resolve(providerId, modelId);
        var reasoningEffort = global?.ReasoningEffort;
        if (config is not null && config.ReasoningEffort is null && !string.IsNullOrWhiteSpace(reasoningEffort))
            config = config with { ReasoningEffort = reasoningEffort };

        return new LlmRoutingConfig
        {
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

    private static (string CanonicalId, bool IsExplicitGlobal) NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? (templateId[globalPrefix.Length..], true)
            : (templateId, false);
    }
}
