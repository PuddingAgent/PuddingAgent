using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// 文件配置 LLM 解析器 — 兼容旧 ILlmResolver 接口，实际从 ILlmConfigService 读取
/// data/config/llm.providers.json，不再访问数据库中的 provider/model 表。
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

    public Task<LlmConfig?> ResolveAsync(
        string providerId, string? modelId = null, CancellationToken ct = default)
    {
        var config = _llmConfigService.Resolve(providerId, modelId);
        if (config is null)
        {
            _logger.LogWarning("[LlmResolver] Provider/model not found in file config provider={ProviderId} model={ModelId}",
                providerId, modelId);
        }

        return Task.FromResult(config);
    }

    public Task<LlmConfig> ResolveDefaultAsync(CancellationToken ct = default)
    {
        var config = _llmConfigService.GetDefault()
            ?? throw new InvalidOperationException("Global LLM default (profiles.conscious) is not configured in data/config/llm.providers.json.");
        return Task.FromResult(config);
    }

    public Task<IReadOnlyList<string>> ListEnabledProviderIdsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _llmConfigService.GetEnabledProviders().Select(p => p.ProviderId).ToList());

    public Task<LlmConfig?> ResolveByCapabilityAsync(
        string[] requiredTags,
        string[]? preferredTags = null,
        string? providerId = null,
        CancellationToken ct = default)
    {
        var allModels = _llmConfigService.GetAllModels()
            .Where(m => !m.IsDeprecated)
            .ToList();

        if (!string.IsNullOrWhiteSpace(providerId))
            allModels = allModels.Where(m => string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)).ToList();

        var required = requiredTags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        var candidates = allModels
            .Where(m => required.All(rt => m.CapabilityTags.Contains(rt, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (candidates.Count == 0) return Task.FromResult<LlmConfig?>(null);

        var preferred = preferredTags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        var best = candidates
            .OrderByDescending(m => preferred.Count(pt => m.CapabilityTags.Contains(pt, StringComparer.OrdinalIgnoreCase)))
            .ThenBy(m => m.SortOrder)
            .First();

        _logger.LogInformation("[LlmResolver] Capability resolve required={Required} preferred={Preferred} → {Provider}/{Model}",
            string.Join(",", required), string.Join(",", preferred), best.ProviderId, best.ModelId);

        var config = _llmConfigService.Resolve(best.ProviderId, best.ModelId);
        return Task.FromResult(config);
    }

    public IReadOnlyList<LlmModelInfo> GetAllModels()
        => _llmConfigService.GetAllModels();
}
