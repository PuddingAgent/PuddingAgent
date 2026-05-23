using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 配置数据种子迁移服务：
/// 启动时从 default-data/ 和 data/config/ 文件读取配置数据，写入 DB。
/// 仅在对应 DB 表为空时执行（幂等）。
/// 文件降级为 seed source，DB 成为唯一事实来源。
/// </summary>
public sealed class DataSeedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<DataSeedService> _logger;

    public DataSeedService(
        IDbContextFactory<PlatformDbContext> dbFactory,
        PuddingDataPaths paths,
        ILogger<DataSeedService> logger)
    {
        _dbFactory = dbFactory;
        _paths = paths;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        await SeedLlmProvidersAsync(db, ct);
        await SeedGlobalAgentTemplatesAsync(db, ct);

        _logger.LogInformation("Data seed completed");
    }

    // ── LLM Providers ────────────────────────────────────────────

    private async Task SeedLlmProvidersAsync(PlatformDbContext db, CancellationToken ct)
    {
        if (await db.LlmProviders.AnyAsync(ct))
        {
            _logger.LogDebug("LLM providers already seeded, skipping");
            return;
        }

        // Try default-data first, then data/config, then embedded default
        var filePath = Path.Combine(_paths.DataRoot, "default-data", "config", "llm.providers.json");
        if (!File.Exists(filePath))
            filePath = _paths.SystemConfigFile("llm.providers.json");
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("No llm.providers.json found for seeding");
            return;
        }

        var json = await File.ReadAllTextAsync(filePath, ct);
        var config = JsonSerializer.Deserialize<LlmProvidersSeedConfig>(json, JsonOptions);
        if (config?.Providers is null) return;

        foreach (var p in config.Providers)
        {
            var entity = new LlmProviderEntity
            {
                ProviderId = p.ProviderId,
                Name = p.Name,
                Protocol = p.Protocol ?? "openai",
                BaseUrl = p.BaseUrl ?? "",
                ApiKey = p.ApiKey,
                Description = p.Description,
                IsEnabled = p.IsEnabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.LlmProviders.Add(entity);
            await db.SaveChangesAsync(ct); // save to get entity.Id

            if (p.Models is not null)
            {
                foreach (var m in p.Models)
                {
                    db.LlmModels.Add(new LlmModelEntity
                    {
                        ProviderId = entity.Id,
                        ModelId = m.ModelId,
                        Name = m.Name,
                        Description = m.Description,
                        MaxContextTokens = m.MaxContextTokens,
                        MaxOutputTokens = m.MaxOutputTokens,
                        InputPricePer1MTokens = m.InputPricePer1MTokens,
                        OutputPricePer1MTokens = m.OutputPricePer1MTokens,
                        CapabilityTagsJson = m.CapabilityTags is { Count: > 0 }
                            ? JsonSerializer.Serialize(m.CapabilityTags) : "[]",
                        IsDefault = m.IsDefault,
                        IsDeprecated = m.IsDeprecated,
                        SortOrder = m.SortOrder,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }

            db.LlmProviderQuotas.Add(new LlmProviderQuotaEntity
            {
                ProviderId = entity.Id,
                DailyTokenLimit = p.Quota?.DailyTokenLimit,
                MonthlyTokenLimit = p.Quota?.MonthlyTokenLimit,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} LLM providers", config.Providers.Count);
    }

    // ── Global Agent Templates ───────────────────────────────────

    private async Task SeedGlobalAgentTemplatesAsync(PlatformDbContext db, CancellationToken ct)
    {
        if (await db.GlobalAgentTemplates.AnyAsync(ct))
        {
            _logger.LogDebug("Global agent templates already seeded, skipping");
            return;
        }

        var templatesDir = Path.Combine(_paths.DataRoot, "default-data", "templates");
        if (!Directory.Exists(templatesDir))
        {
            _logger.LogWarning("No default-data/templates directory found for seeding");
            return;
        }

        var dirs = Directory.GetDirectories(templatesDir);
        foreach (var dir in dirs)
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            var json = await File.ReadAllTextAsync(manifestPath, ct);
            var manifest = JsonSerializer.Deserialize<AgentTemplateSeedManifest>(json, JsonOptions);
            if (manifest is null) continue;

            var templateId = Path.GetFileName(dir);
            var entity = new GlobalAgentTemplateEntity
            {
                TemplateId = templateId,
                Name = manifest.Name ?? templateId,
                Description = manifest.Description,
                Role = manifest.Role ?? "Service",
                SystemPrompt = manifest.SystemPrompt,
                UserPromptTemplate = manifest.UserPromptTemplate,
                PersonaPrompt = await ReadMdFileAsync(Path.Combine(dir, "SOUL.md"), ct),
                ToolsDescription = await ReadMdFileAsync(Path.Combine(dir, "TOOLS.md"), ct),
                BootstrapTemplate = await ReadMdFileAsync(Path.Combine(dir, "BOOTSTRAP.md"), ct),
                AgentsPrompt = await ReadMdFileAsync(Path.Combine(dir, "AGENTS.md"), ct),
                MemoryPrompt = await ReadMdFileAsync(Path.Combine(dir, "MEMORY.md"), ct),
                AvatarEmoji = manifest.AvatarEmoji,
                AvatarId = manifest.AvatarId,
                PreferredProviderId = manifest.PreferredProviderId,
                PreferredModelId = manifest.PreferredModelId,
                MemoryLlmProviderId = manifest.MemoryLlmProviderId,
                MemoryLlmModelId = manifest.MemoryLlmModelId,
                MemorySearchMode = manifest.MemorySearchMode ?? "deep",
                ReasoningEffort = manifest.ReasoningEffort,
                MaxContextTokens = manifest.MaxContextTokens,
                MaxReplyTokens = manifest.MaxReplyTokens,
                ContainerImage = manifest.ContainerImage,
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = manifest.SortOrder ?? 100,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            db.GlobalAgentTemplates.Add(entity);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} global agent templates", dirs.Length);
    }

    private static async Task<string?> ReadMdFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    // ── Seed config models ───────────────────────────────────────

    private sealed class LlmProvidersSeedConfig
    {
        public List<ProviderSeedConfig>? Providers { get; set; }
    }

    private sealed class ProviderSeedConfig
    {
        public string ProviderId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Protocol { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Description { get; set; }
        public bool IsEnabled { get; set; } = true;
        public QuotaSeedConfig? Quota { get; set; }
        public List<ModelSeedConfig>? Models { get; set; }
    }

    private sealed class QuotaSeedConfig
    {
        public long? DailyTokenLimit { get; set; }
        public long? MonthlyTokenLimit { get; set; }
    }

    private sealed class ModelSeedConfig
    {
        public string ModelId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int MaxContextTokens { get; set; } = 8192;
        public int MaxOutputTokens { get; set; } = 2048;
        public decimal InputPricePer1MTokens { get; set; }
        public decimal OutputPricePer1MTokens { get; set; }
        public List<string>? CapabilityTags { get; set; }
        public bool IsDefault { get; set; }
        public bool IsDeprecated { get; set; }
        public int SortOrder { get; set; } = 100;
    }

    private sealed class AgentTemplateSeedManifest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Role { get; set; }
        public string? SystemPrompt { get; set; }
        public string? UserPromptTemplate { get; set; }
        public string? AvatarEmoji { get; set; }
        public string? AvatarId { get; set; }
        public string? PreferredProviderId { get; set; }
        public string? PreferredModelId { get; set; }
        public string? MemoryLlmProviderId { get; set; }
        public string? MemoryLlmModelId { get; set; }
        public string? MemorySearchMode { get; set; }
        public string? ReasoningEffort { get; set; }
        public int MaxContextTokens { get; set; } = 8192;
        public int MaxReplyTokens { get; set; } = 2048;
        public string? ContainerImage { get; set; }
        public int? SortOrder { get; set; }
    }
}
