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

        // LLM provider / model / agent template seeding removed — now file-based (A方案)
        await SeedWorkspacesAsync(config.Workspaces);
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
