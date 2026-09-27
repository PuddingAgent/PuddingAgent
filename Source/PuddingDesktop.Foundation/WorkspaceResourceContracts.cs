namespace PuddingDesktop.Foundation;

public sealed record KnowledgeBaseSummary(
    string KbId, string Name, string Description, string KbType, int DocumentCount,
    bool IsEnabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string TypeText => WorkspaceResourceText.DescribeKbType(KbType);
    public string StateText => IsEnabled ? "已启用" : "已停用";
}

public sealed record WorkspaceSkillSummary(
    string SkillId, string Name, string Description, string SkillType, string ConfigJson,
    bool IsEnabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string TypeText => WorkspaceResourceText.DescribeSkillType(SkillType);
    public string StateText => IsEnabled ? "已启用" : "已停用";
    public bool IsMcp => string.Equals(SkillType, "MCP", StringComparison.OrdinalIgnoreCase);
}

public sealed record WorkspaceWorkflowSummary(
    string WorkflowId, string Name, string Description, string DefinitionJson, string Status,
    bool IsEnabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string StatusText => WorkspaceResourceText.DescribeWorkflowStatus(Status);
    public string StateText => IsEnabled ? "已启用" : "已停用";
}

/// <summary>An empty id means "create"; otherwise the row is updated in place.</summary>
public sealed record KnowledgeBaseEdit(
    string WorkspaceId, string KbId, string Name, string Description, string KbType, bool IsEnabled);

public sealed record WorkspaceSkillEdit(
    string WorkspaceId, string SkillId, string Name, string Description, string SkillType,
    string ConfigJson, bool IsEnabled);

public sealed record WorkspaceWorkflowEdit(
    string WorkspaceId, string WorkflowId, string Name, string Description, string DefinitionJson,
    string Status, bool IsEnabled);

/// <summary>
/// Task-shaped operations for the workspace resource cards, implemented in Composition against the shared
/// WorkspaceResourceService.
/// </summary>
public interface IWorkspaceResourceSettings
{
    Task<IReadOnlyList<KnowledgeBaseSummary>> ListKnowledgeBasesAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task SaveKnowledgeBaseAsync(KnowledgeBaseEdit edit, CancellationToken cancellationToken = default);
    Task DeleteKnowledgeBaseAsync(string workspaceId, string kbId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceSkillSummary>> ListSkillsAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task SaveSkillAsync(WorkspaceSkillEdit edit, CancellationToken cancellationToken = default);
    Task DeleteSkillAsync(string workspaceId, string skillId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceWorkflowSummary>> ListWorkflowsAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task SaveWorkflowAsync(WorkspaceWorkflowEdit edit, CancellationToken cancellationToken = default);
    Task DeleteWorkflowAsync(string workspaceId, string workflowId, CancellationToken cancellationToken = default);
}

public static class WorkspaceResourceText
{
    /// <summary>Mirrors WorkspaceResourceService's vocabularies; Core stores free text, so the page must not invent values.</summary>
    public static IReadOnlyList<string> KnowledgeBaseTypes { get; } = ["VectorStore", "Graph", "FileIndex"];
    public static IReadOnlyList<string> SkillTypes { get; } = ["MCP", "BuiltIn", "CustomScript", "HttpTool"];
    public static IReadOnlyList<string> WorkflowStatuses { get; } = ["Draft", "Active", "Paused"];

    public const string KnowledgeBaseNotice =
        "kbType 沿用 Core 的取值（VectorStore / Graph / FileIndex）；文档数量由 Core 统计，界面只读。";

    public const string McpConfigNotice =
        "MCP 技能的 configJson 由 Core 自己的解析器校验并规范化：写错会直接失败，界面不做“差不多能用”的放宽。";

    public const string WorkflowNotice =
        "definitionJson 允许为空（Core 原样保存），非空时必须是合法 JSON；状态取值沿用 Draft / Active / Paused。";

    public static string DescribeKbType(string? kbType) => kbType switch
    {
        null or "" => "未设置",
        var value when string.Equals(value, "VectorStore", StringComparison.OrdinalIgnoreCase) => "VectorStore（向量库）",
        var value when string.Equals(value, "Graph", StringComparison.OrdinalIgnoreCase) => "Graph（图谱）",
        var value when string.Equals(value, "FileIndex", StringComparison.OrdinalIgnoreCase) => "FileIndex（文件索引）",
        var value => value
    };

    public static string DescribeSkillType(string? skillType) => skillType switch
    {
        null or "" => "未设置",
        var value when string.Equals(value, "MCP", StringComparison.OrdinalIgnoreCase) => "MCP",
        var value when string.Equals(value, "BuiltIn", StringComparison.OrdinalIgnoreCase) => "BuiltIn（内置）",
        var value when string.Equals(value, "CustomScript", StringComparison.OrdinalIgnoreCase) => "CustomScript（自定义脚本）",
        var value when string.Equals(value, "HttpTool", StringComparison.OrdinalIgnoreCase) => "HttpTool（HTTP 工具）",
        var value => value
    };

    public static string DescribeWorkflowStatus(string? status) => status switch
    {
        null or "" => "未设置",
        var value when string.Equals(value, "Draft", StringComparison.OrdinalIgnoreCase) => "Draft（草稿）",
        var value when string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase) => "Active（已启用）",
        var value when string.Equals(value, "Paused", StringComparison.OrdinalIgnoreCase) => "Paused（已暂停）",
        var value => value
    };

    public static bool IsKnownKbType(string? kbType) =>
        kbType is not null && KnowledgeBaseTypes.Contains(kbType, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownSkillType(string? skillType) =>
        skillType is not null && SkillTypes.Contains(skillType, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownWorkflowStatus(string? status) =>
        status is not null && WorkflowStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    public static bool IsJson(string value)
    {
        try { using var _ = System.Text.Json.JsonDocument.Parse(value); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static IReadOnlyList<string> Validate(KnowledgeBaseEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.WorkspaceId)) errors.Add("缺少工作区。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("知识库名称不能为空。");
        if (!IsKnownKbType(edit.KbType)) errors.Add($"kbType 必须是 {string.Join(" / ", KnowledgeBaseTypes)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(WorkspaceSkillEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.WorkspaceId)) errors.Add("缺少工作区。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("技能名称不能为空。");
        if (!IsKnownSkillType(edit.SkillType)) errors.Add($"skillType 必须是 {string.Join(" / ", SkillTypes)} 之一。");
        // MCP config is Core-validated, but an obviously malformed blob should be caught before saving.
        if (string.Equals(edit.SkillType, "MCP", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(edit.ConfigJson) && !IsJson(edit.ConfigJson))
            errors.Add("MCP 的 configJson 不是合法 JSON。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(WorkspaceWorkflowEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.WorkspaceId)) errors.Add("缺少工作区。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("工作流名称不能为空。");
        if (!IsKnownWorkflowStatus(edit.Status)) errors.Add($"状态必须是 {string.Join(" / ", WorkflowStatuses)} 之一。");
        if (!string.IsNullOrWhiteSpace(edit.DefinitionJson) && !IsJson(edit.DefinitionJson))
            errors.Add("definitionJson 不是合法 JSON（留空表示没有定义）。");
        return errors;
    }
}
