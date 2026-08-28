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
        // allModels 保留全量注册信息（含已弃用模型与禁用 provider 下的模型），
        // 仅用于失败路径的区分度诊断；models 才是可解析候选集。
        var allModels = _llmConfigService.GetAllModels().ToList();
        var models = allModels
            .Where(model => !model.IsDeprecated && enabledProviderIds.Contains(model.ProviderId))
            .ToList();
        var requiredTags = requiredCapabilityTags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

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

            return Task.FromResult(ResolveRequired(parts[0], parts[1], requiredTags));
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
                var candidateRoutes = string.Join(", ",
                    matches.Select(m => $"{m.ProviderId}/{m.ModelId}"));
                throw new InvalidOperationException(
                    $"Model '{modelRoute}' exists under multiple providers: {candidateRoutes}. " +
                    "Specify the route as 'providerId/modelId'.");
            }
            if (matches.Count == 1)
                return Task.FromResult(ResolveRequired(
                    matches[0].ProviderId,
                    matches[0].ModelId,
                    requiredTags));

            throw new InvalidOperationException(
                BuildUnknownModelError(modelRoute, allModels, models));
        }

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
                return Task.FromResult(ResolveRequired(
                    selected.ProviderId,
                    selected.ModelId,
                    requiredTags));
            }

            _logger.LogWarning(
                "[LlmResolver] No model matched required capabilities={Required}",
                string.Join(",", requiredTags));
            throw new InvalidOperationException(
                $"No enabled LLM model matches required capabilities: {string.Join(", ", requiredTags)}. " +
                "Add a matching model capabilityTags entry to data/config/llm.providers.json. " +
                BuildRouteAdvisory(providerId: null, models));
        }

        throw new InvalidOperationException(
            "An explicit LLM route is required. Configure providerId/modelId on the Agent manifest " +
            "or pass modelRoute as 'providerId/modelId'; the LLM resource pool does not select defaults. " +
            BuildRouteAdvisory(providerId: null, models));

        ResolvedLlmRoute ResolveRequired(
            string providerId,
            string modelId,
            IReadOnlyCollection<string> capabilityTags)
        {
            var config = _llmConfigService.Resolve(providerId, modelId)
                ?? throw new InvalidOperationException(
                    BuildUnresolvedRouteError(providerId, modelId, allModels, models, enabledProviderIds));

            if (capabilityTags.Count > 0)
            {
                var model = models.FirstOrDefault(candidate =>
                    string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
                var missingTags = capabilityTags
                    .Where(tag => model is null || !model.CapabilityTags.Contains(
                        tag,
                        StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (missingTags.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"LLM route '{providerId}/{modelId}' does not satisfy required capabilities: " +
                        $"{string.Join(", ", missingTags)}.");
                }
            }

            return CreateRoute(providerId, modelId, config);
        }
    }

    // ── 失败路径诊断：区分度明确的错误 + 建议替代路由 ──────────────────────

    /// <summary>
    /// 裸 modelId 未命中可解析候选集时的错误：先区分「已注册但不可解析」
    /// （模型 isDeprecated 或 provider 禁用）与「完全未注册」，再附建议替代。
    /// </summary>
    private static string BuildUnknownModelError(
        string modelRoute,
        IReadOnlyList<LlmModelInfo> allModels,
        IReadOnlyList<LlmModelInfo> models)
    {
        var registered = allModels
            .Where(m => string.Equals(m.ModelId, modelRoute, StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.ProviderId}/{m.ModelId}{(m.IsDeprecated ? " (deprecated)" : string.Empty)}")
            .ToArray();
        if (registered.Length > 0)
        {
            return
                $"Model '{modelRoute}' is registered but not resolvable (model deprecated or provider disabled): " +
                $"{string.Join(", ", registered)}. " +
                BuildRouteAdvisory(providerId: null, models);
        }

        return
            $"Model '{modelRoute}' not found in any enabled provider. " +
            "Specify a configured route as 'providerId/modelId'. " +
            BuildRouteAdvisory(providerId: null, models);
    }

    /// <summary>
    /// 显式 'providerId/modelId' 解析失败时的错误，按三种根因区分：
    /// provider 未启用 / 模型已注册但被禁用（isDeprecated）/ 模型未注册（含
    /// 同 modelId 挂在其他 provider 下的候选路由提示）。
    /// </summary>
    private static string BuildUnresolvedRouteError(
        string providerId,
        string modelId,
        IReadOnlyList<LlmModelInfo> allModels,
        IReadOnlyList<LlmModelInfo> models,
        HashSet<string> enabledProviderIds)
    {
        if (!enabledProviderIds.Contains(providerId))
        {
            var enabledList = string.Join(", ",
                enabledProviderIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            return
                $"LLM provider '{providerId}' is disabled or unknown; route '{providerId}/{modelId}' cannot be resolved. " +
                $"Enabled providers: {enabledList}. " +
                BuildRouteAdvisory(providerId: null, models);
        }

        var registeredHere = allModels
            .Where(m => string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (registeredHere.Count > 0 && registeredHere.All(m => m.IsDeprecated))
        {
            return
                $"LLM route '{providerId}/{modelId}' is registered but the model is disabled (isDeprecated=true) and cannot be resolved. " +
                BuildRouteAdvisory(providerId, models);
        }

        var otherRoutes = models
            .Where(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.ProviderId}/{m.ModelId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (otherRoutes.Length > 0)
        {
            return
                $"LLM route '{providerId}/{modelId}' is not registered under enabled provider '{providerId}'. " +
                $"The same modelId is available under: {string.Join(", ", otherRoutes)}. " +
                BuildRouteAdvisory(providerId, models);
        }

        return
            $"LLM route '{providerId}/{modelId}' is not registered in data/config/llm.providers.json. " +
            BuildRouteAdvisory(providerId, models);
    }

    /// <summary>建议替代：优先同 provider 已启用模型，其次平台默认路由（isDefault）。</summary>
    private static string BuildRouteAdvisory(
        string? providerId,
        IReadOnlyList<LlmModelInfo> models)
    {
        if (providerId is not null)
        {
            var sameProvider = models
                .Where(m => string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                .Select(m => $"{m.ProviderId}/{m.ModelId}")
                .Take(3)
                .ToArray();
            if (sameProvider.Length > 0)
                return $"Suggested alternatives under provider '{providerId}': {string.Join(", ", sameProvider)}.";
        }

        var defaults = models
            .Where(m => m.IsDefault)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"{m.ProviderId}/{m.ModelId}")
            .Take(3)
            .ToArray();
        if (defaults.Length > 0)
            return $"Suggested platform default routes: {string.Join(", ", defaults)}.";

        return "No enabled LLM model is available in data/config/llm.providers.json.";
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
