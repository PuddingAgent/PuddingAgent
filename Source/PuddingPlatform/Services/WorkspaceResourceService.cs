using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Mcp;

namespace PuddingPlatform.Services;

public sealed record KnowledgeBaseDraft(string Name, string? Description, string KbType, bool IsEnabled);
public sealed record WorkspaceSkillDraft(string Name, string? Description, string SkillType, string? ConfigJson, bool IsEnabled);
public sealed record WorkspaceWorkflowDraft(string Name, string? Description, string DefinitionJson, string Status, bool IsEnabled);

/// <summary>
/// 工作区资源（知识库 / 工作区技能 / 工作流）应用操作——从三个直接使用 DbContext 的 Controller
/// 原位下沉。三个资源的形状一致：先确认工作区存在，再按 WorkspaceEntityId 作用域做 CRUD，
/// 因此共用一个服务；工作区隔离是这里的核心不变量（跨工作区 id 一律 NotFound）。
/// MCP 技能的 configJson 校验与规范化沿用 Core 的 McpServerConfig，未做任何放宽。
/// </summary>
public sealed class WorkspaceResourceService(PlatformDbContext db, IMcpConnectionManager? mcpConnectionManager = null)
{
    // ── 知识库 ────────────────────────────────────────────────────────

    public async Task<SkillHubResult<List<KnowledgeBaseDto>>> ListKnowledgeBasesAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<List<KnowledgeBaseDto>>.NotFound(WorkspaceMissing(workspaceId));
        var list = await db.KnowledgeBases.AsNoTracking()
            .Where(kb => kb.WorkspaceEntityId == workspace.Id)
            .OrderBy(kb => kb.Id)
            .Select(kb => ToDto(kb))
            .ToListAsync(ct);
        return SkillHubResult<List<KnowledgeBaseDto>>.Ok(list);
    }

    public async Task<SkillHubResult<KnowledgeBaseDto>> CreateKnowledgeBaseAsync(
        string workspaceId, KnowledgeBaseDraft draft, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<KnowledgeBaseDto>.NotFound(WorkspaceMissing(workspaceId));
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<KnowledgeBaseDto>.BadRequest("知识库名称不能为空。");
        if (string.IsNullOrWhiteSpace(draft.KbType)) return SkillHubResult<KnowledgeBaseDto>.BadRequest("kbType 不能为空。");

        var entity = new KnowledgeBaseEntity
        {
            KbId = Guid.NewGuid().ToString(),
            WorkspaceEntityId = workspace.Id,
            Name = draft.Name.Trim(),
            Description = draft.Description,
            KbType = draft.KbType.Trim(),
            DocumentCount = 0,
            IsEnabled = draft.IsEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeBases.Add(entity);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<KnowledgeBaseDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<KnowledgeBaseDto>> UpdateKnowledgeBaseAsync(
        string workspaceId, string kbId, KnowledgeBaseDraft draft, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<KnowledgeBaseDto>.NotFound(WorkspaceMissing(workspaceId));
        var entity = await db.KnowledgeBases
            .FirstOrDefaultAsync(kb => kb.WorkspaceEntityId == workspace.Id && kb.KbId == kbId, ct);
        if (entity is null) return SkillHubResult<KnowledgeBaseDto>.NotFound($"知识库 '{kbId}' 不在工作区 '{workspaceId}' 内");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<KnowledgeBaseDto>.BadRequest("知识库名称不能为空。");
        if (string.IsNullOrWhiteSpace(draft.KbType)) return SkillHubResult<KnowledgeBaseDto>.BadRequest("kbType 不能为空。");

        entity.Name = draft.Name.Trim();
        entity.Description = draft.Description;
        entity.KbType = draft.KbType.Trim();
        entity.IsEnabled = draft.IsEnabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<KnowledgeBaseDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<KnowledgeBaseDto>> DeleteKnowledgeBaseAsync(
        string workspaceId, string kbId, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<KnowledgeBaseDto>.NotFound(WorkspaceMissing(workspaceId));
        var entity = await db.KnowledgeBases
            .FirstOrDefaultAsync(kb => kb.WorkspaceEntityId == workspace.Id && kb.KbId == kbId, ct);
        if (entity is null) return SkillHubResult<KnowledgeBaseDto>.NotFound($"知识库 '{kbId}' 不在工作区 '{workspaceId}' 内");

        db.KnowledgeBases.Remove(entity);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<KnowledgeBaseDto>.Ok(ToDto(entity));
    }

    // ── 工作区技能 ────────────────────────────────────────────────────

    public async Task<SkillHubResult<List<WorkspaceSkillDto>>> ListSkillsAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<List<WorkspaceSkillDto>>.NotFound(WorkspaceMissing(workspaceId));
        var list = await db.WorkspaceSkills.AsNoTracking()
            .Where(skill => skill.WorkspaceEntityId == workspace.Id)
            .OrderBy(skill => skill.Id)
            .Select(skill => ToDto(skill))
            .ToListAsync(ct);
        return SkillHubResult<List<WorkspaceSkillDto>>.Ok(list);
    }

    public async Task<SkillHubResult<WorkspaceSkillDto>> CreateSkillAsync(
        string workspaceId, WorkspaceSkillDraft draft, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceSkillDto>.NotFound(WorkspaceMissing(workspaceId));
        if (!TryNormalizeSkill(draft, out var skillType, out var configJson, out var error))
            return SkillHubResult<WorkspaceSkillDto>.BadRequest(error!);

        var entity = new WorkspaceSkillEntity
        {
            SkillId = Guid.NewGuid().ToString(),
            WorkspaceEntityId = workspace.Id,
            Name = draft.Name.Trim(),
            Description = draft.Description,
            SkillType = skillType,
            ConfigJson = configJson,
            IsEnabled = draft.IsEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkspaceSkills.Add(entity);
        await db.SaveChangesAsync(ct);
        await RefreshMcpAsync(workspaceId, IsMcp(entity.SkillType), ct);
        return SkillHubResult<WorkspaceSkillDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<WorkspaceSkillDto>> UpdateSkillAsync(
        string workspaceId, string skillId, WorkspaceSkillDraft draft, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceSkillDto>.NotFound(WorkspaceMissing(workspaceId));
        var entity = await db.WorkspaceSkills
            .FirstOrDefaultAsync(skill => skill.WorkspaceEntityId == workspace.Id && skill.SkillId == skillId, ct);
        if (entity is null) return SkillHubResult<WorkspaceSkillDto>.NotFound($"技能 '{skillId}' 不在工作区 '{workspaceId}' 内");
        if (!TryNormalizeSkill(draft, out var skillType, out var configJson, out var error))
            return SkillHubResult<WorkspaceSkillDto>.BadRequest(error!);

        var wasMcp = IsMcp(entity.SkillType);
        entity.Name = draft.Name.Trim();
        entity.Description = draft.Description;
        entity.SkillType = skillType;
        entity.ConfigJson = configJson;
        entity.IsEnabled = draft.IsEnabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // A skill that was MCP or becomes MCP changes the workspace connection set.
        await RefreshMcpAsync(workspaceId, wasMcp || IsMcp(entity.SkillType), ct);
        return SkillHubResult<WorkspaceSkillDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<WorkspaceSkillDto>> DeleteSkillAsync(
        string workspaceId, string skillId, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceSkillDto>.NotFound(WorkspaceMissing(workspaceId));
        var entity = await db.WorkspaceSkills
            .FirstOrDefaultAsync(skill => skill.WorkspaceEntityId == workspace.Id && skill.SkillId == skillId, ct);
        if (entity is null) return SkillHubResult<WorkspaceSkillDto>.NotFound($"技能 '{skillId}' 不在工作区 '{workspaceId}' 内");

        var wasMcp = IsMcp(entity.SkillType);
        db.WorkspaceSkills.Remove(entity);
        await db.SaveChangesAsync(ct);
        await RefreshMcpAsync(workspaceId, wasMcp, ct);
        return SkillHubResult<WorkspaceSkillDto>.Ok(ToDto(entity));
    }

    // ── 工作流 ────────────────────────────────────────────────────────

    public async Task<SkillHubResult<List<WorkflowDto>>> ListWorkflowsAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<List<WorkflowDto>>.NotFound(WorkspaceMissing(workspaceId));
        var list = await db.Workflows.AsNoTracking()
            .Where(workflow => workflow.WorkspaceEntityId == workspace.Id)
            .OrderBy(workflow => workflow.Id)
            .Select(workflow => ToDto(workflow))
            .ToListAsync(ct);
        return SkillHubResult<List<WorkflowDto>>.Ok(list);
    }

    public async Task<SkillHubResult<WorkflowDto>> CreateWorkflowAsync(
        string workspaceId, WorkspaceWorkflowDraft draft, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkflowDto>.NotFound(WorkspaceMissing(workspaceId));
        if (!ValidateWorkflow(draft, out var error)) return SkillHubResult<WorkflowDto>.BadRequest(error!);

        var entity = new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid().ToString(),
            WorkspaceEntityId = workspace.Id,
            Name = draft.Name.Trim(),
            Description = draft.Description,
            DefinitionJson = draft.DefinitionJson,
            Status = draft.Status.Trim(),
            IsEnabled = draft.IsEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Workflows.Add(entity);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkflowDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<WorkflowDto>> UpdateWorkflowAsync(
        string workspaceId, string workflowId, WorkspaceWorkflowDraft draft, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkflowDto>.NotFound(WorkspaceMissing(workspaceId));
        var entity = await db.Workflows
            .FirstOrDefaultAsync(workflow => workflow.WorkspaceEntityId == workspace.Id && workflow.WorkflowId == workflowId, ct);
        if (entity is null) return SkillHubResult<WorkflowDto>.NotFound($"工作流 '{workflowId}' 不在工作区 '{workspaceId}' 内");
        if (!ValidateWorkflow(draft, out var error)) return SkillHubResult<WorkflowDto>.BadRequest(error!);

        entity.Name = draft.Name.Trim();
        entity.Description = draft.Description;
        entity.DefinitionJson = draft.DefinitionJson;
        entity.Status = draft.Status.Trim();
        entity.IsEnabled = draft.IsEnabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkflowDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<WorkflowDto>> DeleteWorkflowAsync(
        string workspaceId, string workflowId, CancellationToken ct = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkflowDto>.NotFound(WorkspaceMissing(workspaceId));
        var entity = await db.Workflows
            .FirstOrDefaultAsync(workflow => workflow.WorkspaceEntityId == workspace.Id && workflow.WorkflowId == workflowId, ct);
        if (entity is null) return SkillHubResult<WorkflowDto>.NotFound($"工作流 '{workflowId}' 不在工作区 '{workspaceId}' 内");

        db.Workflows.Remove(entity);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkflowDto>.Ok(ToDto(entity));
    }

    // ── 共享规则 ──────────────────────────────────────────────────────

    /// <summary>Core's own vocabulary; the DTO stores free text, so the page must not invent other values.</summary>
    public static IReadOnlyList<string> KnowledgeBaseTypes { get; } = ["VectorStore", "Graph", "FileIndex"];
    public static IReadOnlyList<string> SkillTypes { get; } = ["MCP", "BuiltIn", "CustomScript", "HttpTool"];
    public static IReadOnlyList<string> WorkflowStatuses { get; } = ["Draft", "Active", "Paused"];

    private static string WorkspaceMissing(string workspaceId) => $"Workspace '{workspaceId}' 不存在";

    private static bool ValidateWorkflow(WorkspaceWorkflowDraft draft, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(draft.Name)) { error = "工作流名称不能为空。"; return false; }
        if (string.IsNullOrWhiteSpace(draft.Status)) { error = "status 不能为空。"; return false; }
        if (!string.IsNullOrWhiteSpace(draft.DefinitionJson) && !IsJson(draft.DefinitionJson!))
        {
            error = "definitionJson 不是合法 JSON。";
            return false;
        }
        return true;
    }

    private static bool IsJson(string value)
    {
        try { using var _ = System.Text.Json.JsonDocument.Parse(value); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>MCP config is validated and canonicalised by Core's own parser, never by ad-hoc JSON checks.</summary>
    private static bool TryNormalizeSkill(WorkspaceSkillDraft draft, out string skillType, out string? configJson, out string? error)
    {
        skillType = draft.SkillType?.Trim() ?? string.Empty;
        configJson = draft.ConfigJson;
        error = null;
        if (string.IsNullOrWhiteSpace(draft.Name)) { error = "Skill name is required."; return false; }
        if (string.IsNullOrWhiteSpace(skillType)) { error = "skillType is required."; return false; }
        if (!IsMcp(skillType)) return true;

        skillType = "MCP";
        if (!McpServerConfig.TryParse(draft.ConfigJson, out var config, out error)) return false;
        configJson = config!.ToCanonicalJson();
        return true;
    }

    private static bool IsMcp(string? skillType) =>
        string.Equals(skillType, "MCP", StringComparison.OrdinalIgnoreCase);

    private async Task RefreshMcpAsync(string workspaceId, bool relevant, CancellationToken ct)
    {
        if (relevant && mcpConnectionManager is not null)
            await mcpConnectionManager.RefreshWorkspaceAsync(workspaceId, ct);
    }

    private Task<WorkspaceEntity?> FindWorkspaceAsync(string workspaceId, CancellationToken ct) =>
        db.Workspaces.AsNoTracking().FirstOrDefaultAsync(workspace => workspace.WorkspaceId == workspaceId, ct);

    private static KnowledgeBaseDto ToDto(KnowledgeBaseEntity entity) => new(
        entity.KbId, entity.Name, entity.Description, entity.KbType,
        entity.DocumentCount, entity.IsEnabled, entity.CreatedAt, entity.UpdatedAt);

    private static WorkspaceSkillDto ToDto(WorkspaceSkillEntity entity) => new(
        entity.SkillId, entity.Name, entity.Description, entity.SkillType,
        entity.ConfigJson, entity.IsEnabled, entity.CreatedAt, entity.UpdatedAt);

    private static WorkflowDto ToDto(WorkflowEntity entity) => new(
        entity.WorkflowId, entity.Name, entity.Description, entity.DefinitionJson,
        entity.Status, entity.IsEnabled, entity.CreatedAt, entity.UpdatedAt);
}
