using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingController.Data;
using PuddingController.Data.Entities;

namespace PuddingController.Services;

/// <summary>
/// Workspace 配置目录——PostgreSQL 为持久化层，内存字典作为读缓存。
/// 首次启动时自动从 DB 加载；DB 为空则播种默认 Workspace。
/// </summary>
public class InMemoryWorkspaceCatalog : IWorkspaceCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, WorkspaceDefinition> _cache = new();
    private readonly IDbContextFactory<ControllerDbContext> _dbFactory;

    public InMemoryWorkspaceCatalog(IDbContextFactory<ControllerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    // ── IWorkspaceCatalog ──────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var entities = await db.WorkspaceDefinitions.ToListAsync(ct);
        _cache.Clear();
        foreach (var e in entities)
            _cache[e.WorkspaceId] = ToDefinition(e);

        if (_cache.IsEmpty)
            await SeedDefaultsAsync(db, ct);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
        => await LoadAsync(ct);

    public WorkspaceDefinition? GetWorkspace(string workspaceId)
        => _cache.GetValueOrDefault(workspaceId);

    public WorkspaceDefinition? FindByChannel(string channelId)
        => _cache.Values.FirstOrDefault(ws => ws.ChannelBindings.Any(c => c.ChannelId == channelId));

    public IReadOnlyList<WorkspaceDefinition> GetAll()
        => _cache.Values.ToList();

    // ── 扩展方法（持久化写入）──────────────────────────────────────

    /// <summary>更新缓存并异步写入 DB（向后兼容的同步入口）。</summary>
    public void Upsert(WorkspaceDefinition workspace)
    {
        _cache[workspace.WorkspaceId] = workspace;
        _ = UpsertDbAsync(workspace, CancellationToken.None);
    }

    /// <summary>更新缓存并等待 DB 写入完成（async 代码中推荐使用）。</summary>
    public async Task UpsertAsync(WorkspaceDefinition workspace, CancellationToken ct = default)
    {
        _cache[workspace.WorkspaceId] = workspace;
        await UpsertDbAsync(workspace, ct);
    }

    /// <summary>从缓存删除并异步从 DB 删除（向后兼容的同步入口）。</summary>
    public bool Remove(string workspaceId)
    {
        var removed = _cache.TryRemove(workspaceId, out _);
        if (removed) _ = RemoveDbAsync(workspaceId, CancellationToken.None);
        return removed;
    }

    /// <summary>从缓存删除并等待 DB 删除完成（async 代码中推荐使用）。</summary>
    public async Task<bool> RemoveAsync(string workspaceId, CancellationToken ct = default)
    {
        _cache.TryRemove(workspaceId, out _);
        return await RemoveDbAsync(workspaceId, ct);
    }

    // ── DB 操作 ────────────────────────────────────────────────────

    private async Task UpsertDbAsync(WorkspaceDefinition workspace, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = ToEntity(workspace);
        var existing = await db.WorkspaceDefinitions.FindAsync([workspace.WorkspaceId], ct);
        if (existing is null)
            db.WorkspaceDefinitions.Add(entity);
        else
            db.Entry(existing).CurrentValues.SetValues(entity);
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> RemoveDbAsync(string workspaceId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = await db.WorkspaceDefinitions.FindAsync([workspaceId], ct);
        if (existing is null) return false;
        db.WorkspaceDefinitions.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task SeedDefaultsAsync(ControllerDbContext db, CancellationToken ct)
    {
        var allTemplates = BuiltInAgentTemplates.GetAll();
        var ws = new WorkspaceDefinition
        {
            WorkspaceId = "default",
            Name = "Default Workspace",
            Description = "预置的默认工作空间",
            AgentTemplateIds = allTemplates
                .Where(t => t.TemplateType != AgentTemplateType.Audit)
                .Select(t => t.TemplateId).ToList(),
            AuditAgentTemplateIds = allTemplates
                .Where(t => t.TemplateType == AgentTemplateType.Audit)
                .Select(t => t.TemplateId).ToList(),
            ChannelBindings =
            [
                new ChannelBindingDefinition { ChannelId = "cli", ChannelType = "cli" },
                new ChannelBindingDefinition { ChannelId = "web-chat-default", ChannelType = "web-chat" },
            ],
            PermissionPolicy = new PermissionPolicyDefinition { DefaultDeny = false },
        };
        _cache[ws.WorkspaceId] = ws;
        db.WorkspaceDefinitions.Add(ToEntity(ws));
        await db.SaveChangesAsync(ct);
    }

    // ── Entity ↔ Domain 映射 ───────────────────────────────────────

    private sealed record WorkspaceExtras(
        KnowledgeBaseDefinition? KnowledgeBase,
        StorageBindingDefinition? StorageBinding,
        KnowledgeGraphDefinition? KnowledgeGraph,
        IReadOnlyList<WorkflowBindingDefinition> WorkflowBindings);

    private static WorkspaceDefinition ToDefinition(WorkspaceDefinitionEntity e)
    {
        WorkspaceExtras? extras = null;
        if (e.ExtrasJson is not null)
            extras = JsonSerializer.Deserialize<WorkspaceExtras>(e.ExtrasJson, JsonOpts);

        return new WorkspaceDefinition
        {
            WorkspaceId = e.WorkspaceId,
            Name = e.Name,
            Description = e.Description,
            IsEnabled = e.IsEnabled,
            IsFrozen = e.IsFrozen,
            ChannelBindings = JsonSerializer.Deserialize<List<ChannelBindingDefinition>>(e.ChannelBindingsJson, JsonOpts) ?? [],
            AgentTemplateIds = JsonSerializer.Deserialize<List<string>>(e.AgentTemplateIdsJson, JsonOpts) ?? [],
            AuditAgentTemplateIds = JsonSerializer.Deserialize<List<string>>(e.AuditAgentTemplateIdsJson, JsonOpts) ?? [],
            PermissionPolicy = e.PermissionPolicyJson is not null
                ? JsonSerializer.Deserialize<PermissionPolicyDefinition>(e.PermissionPolicyJson, JsonOpts)
                : null,
            KnowledgeBase = extras?.KnowledgeBase,
            StorageBinding = extras?.StorageBinding,
            KnowledgeGraph = extras?.KnowledgeGraph,
            WorkflowBindings = extras?.WorkflowBindings ?? [],
        };
    }

    private static WorkspaceDefinitionEntity ToEntity(WorkspaceDefinition d)
    {
        var extras = new WorkspaceExtras(d.KnowledgeBase, d.StorageBinding, d.KnowledgeGraph, d.WorkflowBindings.ToList());
        bool hasExtras = extras.KnowledgeBase is not null || extras.StorageBinding is not null
            || extras.KnowledgeGraph is not null || extras.WorkflowBindings.Count > 0;

        return new WorkspaceDefinitionEntity
        {
            WorkspaceId = d.WorkspaceId,
            Name = d.Name,
            Description = d.Description,
            IsEnabled = d.IsEnabled,
            IsFrozen = d.IsFrozen,
            ChannelBindingsJson = JsonSerializer.Serialize(d.ChannelBindings, JsonOpts),
            AgentTemplateIdsJson = JsonSerializer.Serialize(d.AgentTemplateIds, JsonOpts),
            AuditAgentTemplateIdsJson = JsonSerializer.Serialize(d.AuditAgentTemplateIds, JsonOpts),
            PermissionPolicyJson = d.PermissionPolicy is not null
                ? JsonSerializer.Serialize(d.PermissionPolicy, JsonOpts)
                : null,
            ExtrasJson = hasExtras ? JsonSerializer.Serialize(extras, JsonOpts) : null,
        };
    }
}
