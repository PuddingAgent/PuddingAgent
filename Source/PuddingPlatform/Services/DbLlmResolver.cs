using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// 数据库 LLM 解析器 — 从 PlatformDbContext 查询 LLM 服务商与模型注册表，
/// 返回完整的 LlmConfig 给 Runtime 执行引擎使用。
/// 
/// 职责：纯数据查询，不负责缓存、不负责调度。
/// </summary>
public class DbLlmResolver : ILlmResolver
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ILogger<DbLlmResolver> _logger;
    private readonly IConfiguration _configuration;

    public DbLlmResolver(
        IDbContextFactory<PlatformDbContext> dbFactory,
        IConfiguration configuration,
        ILogger<DbLlmResolver> logger)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LlmConfig?> ResolveAsync(
        string providerId, string? modelId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var provider = await db.LlmProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderId == providerId && p.IsEnabled, ct);

        if (provider is null)
        {
            _logger.LogWarning("[LlmResolver] Provider {ProviderId} not found or disabled", providerId);
            return null;
        }

        var resolvedModelId = modelId;
        if (string.IsNullOrWhiteSpace(resolvedModelId))
        {
            var defaultModel = await db.LlmModels
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ProviderId == provider.Id && m.IsDefault && !m.IsDeprecated, ct);

            resolvedModelId = defaultModel?.ModelId
                ?? await db.LlmModels.AsNoTracking()
                    .Where(m => m.ProviderId == provider.Id && !m.IsDeprecated)
                    .OrderBy(m => m.SortOrder)
                    .Select(m => m.ModelId)
                    .FirstOrDefaultAsync(ct);
        }

        // .env API key 优先于 DB 存储的 key（开发环境常见场景）
        var envApiKey = _configuration["LLM_API_KEY"];
        var apiKey = !string.IsNullOrWhiteSpace(envApiKey)
            ? envApiKey
            : provider.ApiKey;

        _logger.LogInformation(
            "[LlmResolver] Resolved provider={ProviderId} model={ModelId} endpoint={Endpoint} hasApiKey={HasKey} keySource={KeySource}",
            providerId, resolvedModelId, provider.BaseUrl, !string.IsNullOrWhiteSpace(apiKey),
            !string.IsNullOrWhiteSpace(envApiKey) ? "env" : "db");

        return new LlmConfig
        {
            Endpoint = provider.BaseUrl,
            ApiKey = apiKey,
            ModelId = resolvedModelId ?? "gpt-4o-mini",
        };
    }

    public async Task<LlmConfig?> ResolveDefaultAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 优先选择已配置 ApiKey 的 provider，其次按 Id 排序
        var defaultProvider = await db.LlmProviders
            .AsNoTracking()
            .Where(p => p.IsEnabled)
            .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.ApiKey))
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (defaultProvider is null)
        {
            _logger.LogWarning("[LlmResolver] No enabled providers in database, falling back to .env");
            return ResolveFromEnv();
        }

        return await ResolveAsync(defaultProvider.ProviderId, modelId: null, ct);
    }

    public async Task<IReadOnlyList<string>> ListEnabledProviderIdsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.LlmProviders
            .AsNoTracking()
            .Where(p => p.IsEnabled)
            .Select(p => p.ProviderId)
            .ToListAsync(ct);
    }

    /// <summary>DB 无可用 provider 时，回退到 .env 配置。</summary>
    private LlmConfig ResolveFromEnv()
    {
        return new LlmConfig
        {
            Endpoint = _configuration["LLM_ENDPOINT"] ?? "https://api.openai.com/v1",
            ApiKey = _configuration["LLM_API_KEY"] ?? "",
            ModelId = _configuration["LLM_MODEL"] ?? "gpt-4o-mini",
        };
    }
}
