namespace PuddingPlatform.Data.Entities;

/// <summary>工作空间内定义的工作流（描述多 Agent 协作流程）。</summary>
public class WorkflowEntity
{
    public int Id { get; set; }

    /// <summary>业务层唯一 ID（GUID）。</summary>
    public string WorkflowId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>所属工作空间的 PK。</summary>
    public int WorkspaceEntityId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>工作流定义 JSON（DAG 节点图描述）。</summary>
    public string? DefinitionJson { get; set; }

    /// <summary>状态：Draft | Active | Paused。</summary>
    public string Status { get; set; } = "Draft";

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public WorkspaceEntity Workspace { get; set; } = null!;
}
