using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// 文件配置 LLM 路由解析器。模型选择和 Provider 身份只来自 ILlmConfigService，
/// 不访问数据库，也不从 endpoint、密钥或 model 字符串反推 Provider。
/// </summary>
public sealed class FileLlmResolver : ILlmResolver
{
    private readonly ILlmConfigService _llmConfigService;
    private readonly ILogger<FileLlmResolver> _logger;

    public FileLlmResolver(
        ILlmConfigService llmConfigService,
        ILogger<FileLlmResolver> logger)
    {
        _llmConfigService = llmConfigService;
        _logger = logger;
    }

    public Task<ResolvedLlmRoute> ResolveRouteAsync(
        string? modelRoute = null,
        IReadOnlyCollection<string>? requiredCapabilityTags = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var enabledProviderIds = _llmConfigService.GetEnabledProviders()
            .Select(provider => provider.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var models = _llmConfigService.GetAllModels()
            .Where(model => !model.IsDeprecated && enabledProviderIds.Contains(model.ProviderId))
            .ToList();

        if (!string.IsNullOrWhiteSpace(modelRoute) && modelRoute.Contains('/'))
        {
            var parts = modelRoute.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || string.IsNullOrWhiteSpace(parts[0])
                || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new InvalidOperationException(
                    $"Invalid model route '{modelRoute}'. Expected 'providerId/modelId'.");
            }

            return Task.FromResult(ResolveRequired(parts[0], parts[1]));
        }

        if (!string.IsNullOrWhiteSpace(modelRoute))
        {
            var matches = models
                .Where(model => string.Equals(
                    model.ModelId,
                    modelRoute,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Model '{modelRoute}' exists under multiple providers. " +
                    "Specify the route as 'providerId/modelId'.");
            }
            if (matches.Count == 1)
                return Task.FromResult(ResolveRequired(matches[0].ProviderId, matches[0].ModelId));

            throw new InvalidOperationException(
                $"Model '{modelRoute}' not found in any enabled provider. " +
                "Specify a configured route as 'providerId/modelId'.");
        }

        var requiredTags = requiredCapabilityTags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .ToArray() ?? [];
        if (requiredTags.Length > 0)
        {
            var selected = models
                .Where(model => requiredTags.All(tag =>
                    model.CapabilityTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(model => model.SortOrder)
                .ThenBy(model => model.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (selected is not null)
            {
                _logger.LogInformation(
                    "[LlmResolver] Capability route required={Required} route={Provider}/{Model}",
                    string.Join(",", requiredTags),
                    selected.ProviderId,
                    selected.ModelId);
                return Task.FromResult(ResolveRequired(selected.ProviderId, selected.ModelId));
            }

            _logger.LogWarning(
                "[LlmResolver] No model matched required capabilities={Required}",
                string.Join(",", requiredTags));
            throw new InvalidOperationException(
                $"No enabled LLM model matches required capabilities: {string.Join(", ", requiredTags)}. " +
                "Add a matching model capabilityTags entry to data/config/llm.providers.json.");
        }

        var defaultProfile = _llmConfigService.GetDefaultProfile();
        return Task.FromResult(CreateRoute(
            defaultProfile.ProviderId,
            defaultProfile.ModelId,
            defaultProfile.Config));

        ResolvedLlmRoute ResolveRequired(string providerId, string modelId)
        {
            var config = _llmConfigService.Resolve(providerId, modelId)
                ?? throw new InvalidOperationException(
                    $"LLM route '{providerId}/{modelId}' is not configured or is disabled.");
            return CreateRoute(providerId, modelId, config);
        }
    }

    private static ResolvedLlmRoute CreateRoute(string providerId, string modelId, LlmConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ModelId))
            throw new InvalidOperationException(
                $"LLM route '{providerId}/{modelId}' resolved without a model in its configuration snapshot.");
        if (!string.Equals(config.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"LLM route '{providerId}/{modelId}' resolved mismatched config model '{config.ModelId}'.");
        }

        return new ResolvedLlmRoute
        {
            ProviderId = providerId,
            ModelId = modelId,
            Config = config,
        };
    }
}
