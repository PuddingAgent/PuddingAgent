using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingRuntime.Services;

/// <summary>
/// 启动时将 JSON 配置种子到 SQLite（幂等 Upsert）。
/// 提供者 + 模型 + Agent 模板 + 工作区 + Agent 实例从 JSON 同步到数据库。
/// 已有的业务数据（聊天、记忆、会话等）不受影响。
/// </summary>
public class JsonConfigSeedService
{
    private readonly PlatformDbContext _db;
    private readonly ILogger<JsonConfigSeedService> _logger;

    public JsonConfigSeedService(PlatformDbContext db, ILogger<JsonConfigSeedService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync()
    {
        var config = PuddingConfigLoader.Load();
        if (config is null)
        {
            _logger.LogWarning("[ConfigSeed] 未找到配置文件 {Path}，跳过种子", PuddingConfigLoader.DefaultPath);
            return;
        }

        await SeedProvidersAsync(config.Providers);
        await SeedAgentTemplatesAsync(config.AgentTemplates);
        await SeedWorkspacesAsync(config.Workspaces);
    }

    private async Task SeedProvidersAsync(List<JsonProvider>? providers)
    {
        if (providers is null or { Count: 0 }) return;

        foreach (var p in providers)
        {
            var existing = await _db.Set<LlmProviderEntity>()
                .Include(x => x.Models)
                .FirstOrDefaultAsync(x => x.ProviderId == p.ProviderId);

            if (existing is null)
            {
                var entity = new LlmProviderEntity
                {
                    ProviderId = p.ProviderId,
                    Name = p.Name,
                    Protocol = p.Protocol,
                    BaseUrl = p.BaseUrl,
                    ApiKey = p.ApiKey,
                    Description = p.Description,
                    IsEnabled = p.IsEnabled,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _db.Set<LlmProviderEntity>().Add(entity);
                await _db.SaveChangesAsync();
                existing = entity;
                _logger.LogInformation("[ConfigSeed] 创建 Provider: {Id}", p.ProviderId);
            }
            else
            {
                // 幂等更新非业务关键字段
                existing.Name = p.Name;
                existing.Protocol = p.Protocol;
                existing.BaseUrl = p.BaseUrl;
                existing.ApiKey = p.ApiKey;
                existing.Description = p.Description;
                existing.IsEnabled = p.IsEnabled;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("[ConfigSeed] 更新 Provider: {Id}", p.ProviderId);
            }

            await SeedModelsAsync(existing, p.Models);
        }

        await _db.SaveChangesAsync();
    }

    private async Task SeedModelsAsync(LlmProviderEntity provider, List<JsonProviderModel>? models)
    {
        if (models is null or { Count: 0 }) return;

        var existingModels = await _db.Set<LlmModelEntity>()
            .Where(m => m.ProviderId == provider.Id)
            .ToListAsync();

        foreach (var m in models)
        {
            var existing = existingModels.FirstOrDefault(em => em.ModelId == m.ModelId);
            if (existing is null)
            {
                _db.Set<LlmModelEntity>().Add(new LlmModelEntity
                {
                    ProviderId = provider.Id,
                    ModelId = m.ModelId,
                    Name = m.Name,
                    Description = m.Description,
                    MaxContextTokens = m.MaxContextTokens,
                    MaxOutputTokens = m.MaxOutputTokens,
                    CapabilityTagsJson = m.CapabilityTags is not null
                        ? System.Text.Json.JsonSerializer.Serialize(m.CapabilityTags)
                        : "[]",
                    IsDefault = m.IsDefault,
                    SortOrder = m.SortOrder,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                _logger.LogInformation("[ConfigSeed] 创建 Model: {Provider}/{Model}", provider.ProviderId, m.ModelId);
            }
            else
            {
                existing.Name = m.Name;
                existing.Description = m.Description;
                existing.MaxContextTokens = m.MaxContextTokens;
                existing.MaxOutputTokens = m.MaxOutputTokens;
                existing.CapabilityTagsJson = m.CapabilityTags is not null
                    ? System.Text.Json.JsonSerializer.Serialize(m.CapabilityTags)
                    : existing.CapabilityTagsJson;
                existing.IsDefault = m.IsDefault;
                existing.SortOrder = m.SortOrder;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task SeedAgentTemplatesAsync(List<JsonAgentTemplate>? templates)
    {
        if (templates is null or { Count: 0 }) return;

        foreach (var t in templates)
        {
            var existing = await _db.Set<GlobalAgentTemplateEntity>()
                .FirstOrDefaultAsync(x => x.TemplateId == t.TemplateId);

            if (existing is null)
            {
                _db.Set<GlobalAgentTemplateEntity>().Add(new GlobalAgentTemplateEntity
                {
                    TemplateId = t.TemplateId,
                    Name = t.Name,
                    Description = t.Description,
                    Role = t.Role,
                    SystemPrompt = t.SystemPrompt,
                    PersonaPrompt = t.PersonaPrompt,
                    AvatarEmoji = t.AvatarEmoji,
                    PreferredProviderId = t.PreferredProviderId,
                    PreferredModelId = t.PreferredModelId,
                    MemorySearchMode = t.MemorySearchMode,
                    ReasoningEffort = t.ReasoningEffort,
                    MaxContextTokens = t.MaxContextTokens,
                    MaxReplyTokens = t.MaxReplyTokens,
                    IsBuiltIn = t.IsBuiltIn,
                    IsEnabled = t.IsEnabled,
                    SortOrder = t.SortOrder,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                _logger.LogInformation("[ConfigSeed] 创建 AgentTemplate: {Id}", t.TemplateId);
            }
            else
            {
                existing.Name = t.Name;
                existing.Description = t.Description;
                existing.Role = t.Role;
                existing.SystemPrompt = t.SystemPrompt;
                existing.PersonaPrompt = t.PersonaPrompt;
                existing.AvatarEmoji = t.AvatarEmoji;
                existing.PreferredProviderId = t.PreferredProviderId;
                existing.PreferredModelId = t.PreferredModelId;
                existing.MemorySearchMode = t.MemorySearchMode;
                existing.ReasoningEffort = t.ReasoningEffort;
                existing.MaxContextTokens = t.MaxContextTokens;
                existing.MaxReplyTokens = t.MaxReplyTokens;
                existing.IsBuiltIn = t.IsBuiltIn;
                existing.IsEnabled = t.IsEnabled;
                existing.SortOrder = t.SortOrder;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("[ConfigSeed] 更新 AgentTemplate: {Id}", t.TemplateId);
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task SeedWorkspacesAsync(List<JsonWorkspace>? workspaces)
    {
        if (workspaces is null or { Count: 0 }) return;

        foreach (var w in workspaces)
        {
            var existing = await _db.Set<WorkspaceEntity>()
                .FirstOrDefaultAsync(x => x.WorkspaceId == w.WorkspaceId);

            WorkspaceEntity workspaceEntity;
            if (existing is null)
            {
                workspaceEntity = new WorkspaceEntity
                {
                    WorkspaceId = w.WorkspaceId,
                    Slug = w.Slug,
                    Name = w.Name,
                    Description = w.Description,
                    IsEnabled = w.IsEnabled,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _db.Set<WorkspaceEntity>().Add(workspaceEntity);
                await _db.SaveChangesAsync();
                _logger.LogInformation("[ConfigSeed] 创建 Workspace: {Id}", w.WorkspaceId);
            }
            else
            {
                workspaceEntity = existing;
                existing.Name = w.Name;
                existing.Description = w.Description;
                existing.IsEnabled = w.IsEnabled;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("[ConfigSeed] 更新 Workspace: {Id}", w.WorkspaceId);
            }

            await SeedAgentsAsync(workspaceEntity, w.Agents);
        }

        await _db.SaveChangesAsync();
    }

    private async Task SeedAgentsAsync(WorkspaceEntity workspace, List<JsonAgent>? agents)
    {
        if (agents is null or { Count: 0 }) return;

        foreach (var a in agents)
        {
            var existing = await _db.Set<WorkspaceAgentEntity>()
                .FirstOrDefaultAsync(x => x.AgentId == a.AgentId && x.WorkspaceEntityId == workspace.Id);

            if (existing is null)
            {
                _db.Set<WorkspaceAgentEntity>().Add(new WorkspaceAgentEntity
                {
                    AgentId = a.AgentId,
                    WorkspaceEntityId = workspace.Id,
                    Name = a.Name,
                    DisplayName = a.DisplayName,
                    Description = a.Description,
                    SourceTemplateId = a.SourceTemplateId,
                    IsEnabled = a.IsEnabled,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                _logger.LogInformation("[ConfigSeed] 创建 Agent: {Ws}/{Agent}", workspace.WorkspaceId, a.AgentId);
            }
            else
            {
                existing.Name = a.Name;
                existing.DisplayName = a.DisplayName;
                existing.Description = a.Description;
                existing.SourceTemplateId = a.SourceTemplateId;
                existing.IsEnabled = a.IsEnabled;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("[ConfigSeed] 更新 Agent: {Ws}/{Agent}", workspace.WorkspaceId, a.AgentId);
            }
        }
    }
}
