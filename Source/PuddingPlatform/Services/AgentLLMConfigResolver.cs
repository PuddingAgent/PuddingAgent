using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// LLM 配置解析器实现：
/// - 从 GlobalAgentTemplates + WorkspaceAgentTemplates 读取 LLM 路由配置
/// - 不受 IsEnabled 限制——只过滤 TemplateId 匹配
/// - 回退链路：Workspace → Global → 环境变量 → 显意识 LLM
/// </summary>
public sealed class AgentLLMConfigResolver : ILLMConfigResolver
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ILogger<AgentLLMConfigResolver> _logger;

    public AgentLLMConfigResolver(
        IDbContextFactory<PlatformDbContext> dbFactory,
        ILogger<AgentLLMConfigResolver> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<LlmRoutingConfig?> ResolveConsciousAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var canonicalId = NormalizeTemplateId(templateId);
        if (string.IsNullOrWhiteSpace(canonicalId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var global = await db.GlobalAgentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == canonicalId, ct);

        var ws = !string.IsNullOrWhiteSpace(workspaceId)
            ? await db.WorkspaceAgentTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TemplateId == canonicalId && t.WorkspaceId == workspaceId, ct)
            : null;

        return new LlmRoutingConfig
        {
            ProviderId = ws?.PreferredProviderId ?? global?.PreferredProviderId,
            ModelId = ws?.PreferredModelId ?? global?.PreferredModelId,
            Endpoint = Environment.GetEnvironmentVariable("LLM_ENDPOINT"),
            ApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY"),
        };
    }

    public async Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var canonicalId = NormalizeTemplateId(templateId);
        if (string.IsNullOrWhiteSpace(canonicalId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 全局模板
        var global = await db.GlobalAgentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == canonicalId, ct);

        // 工作区覆盖
        var ws = !string.IsNullOrWhiteSpace(workspaceId)
            ? await db.WorkspaceAgentTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TemplateId == canonicalId && t.WorkspaceId == workspaceId, ct)
            : null;

        var providerId = ws?.MemoryLlmProviderId
                         ?? global?.MemoryLlmProviderId
                         ?? Environment.GetEnvironmentVariable("MEMORY_LLM_PROVIDER_ID");

        var modelId = ws?.MemoryLlmModelId
                      ?? global?.MemoryLlmModelId;

        string? endpoint = null;
        string? apiKey = null;

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var provider = await db.LlmProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProviderId == providerId && p.IsEnabled, ct);

            if (provider is not null)
            {
                endpoint = provider.BaseUrl;
                apiKey = provider.ApiKey;

                if (string.IsNullOrWhiteSpace(modelId))
                {
                    modelId = await db.LlmModels
                        .AsNoTracking()
                        .Where(m => m.ProviderId == provider.Id && !m.IsDeprecated)
                        .OrderByDescending(m => m.IsDefault)
                        .ThenBy(m => m.SortOrder)
                        .ThenBy(m => m.Id)
                        .Select(m => m.ModelId)
                        .FirstOrDefaultAsync(ct);
                }
                else
                {
                    var modelExists = await db.LlmModels
                        .AsNoTracking()
                        .AnyAsync(m => m.ProviderId == provider.Id && m.ModelId == modelId && !m.IsDeprecated, ct);
                    if (!modelExists)
                    {
                        _logger.LogWarning(
                            "[LLMConfig] Memory model not found or deprecated provider={Provider} model={Model}",
                            providerId,
                            modelId);
                        modelId = null;
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "[LLMConfig] Memory provider not found or disabled provider={Provider}",
                    providerId);
            }
        }

        if (string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId))
        {
            var conscious = await ResolveConsciousAsync(canonicalId, workspaceId, ct);
            providerId = conscious?.ProviderId;
            modelId = conscious?.ModelId;

            if (!string.IsNullOrWhiteSpace(providerId))
            {
                var provider = await db.LlmProviders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProviderId == providerId && p.IsEnabled, ct);
                endpoint = provider?.BaseUrl;
                apiKey = provider?.ApiKey;
            }
            else
            {
                endpoint = conscious?.Endpoint;
                apiKey = conscious?.ApiKey;
            }
        }

        var searchMode = ws?.MemorySearchMode
                         ?? global?.MemorySearchMode
                         ?? "deep";

        _logger.LogDebug(
            "[LLMConfig] Resolved memory config template={Template} ws={Workspace} endpoint={HasEndpoint} model={Model} mode={Mode}",
            canonicalId, workspaceId, !string.IsNullOrWhiteSpace(endpoint), modelId, searchMode);

        return new MemoryLlmRoutingConfig
        {
            ProviderId = providerId,
            Endpoint = endpoint,
            ApiKey = apiKey,
            ModelId = modelId,
            SearchMode = searchMode,
        };
    }

    private static string NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? templateId[globalPrefix.Length..]
            : templateId;
    }
}
