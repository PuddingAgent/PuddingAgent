namespace PuddingPlatform.Data.Entities;

/// <summary>工作空间内注册的技能/工具（MCP、内置、自定义脚本等）。</summary>
public class WorkspaceSkillEntity
{
    public int Id { get; set; }

    /// <summary>业务层唯一 ID（GUID）。</summary>
    public string SkillId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>所属工作空间的 PK。</summary>
    public int WorkspaceEntityId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>技能类型：MCP | BuiltIn | CustomScript | HttpTool。</summary>
    public string SkillType { get; set; } = "BuiltIn";

    /// <summary>技能配置 JSON（如 MCP Server URL、HTTP endpoint 等）。</summary>
    public string? ConfigJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public WorkspaceEntity Workspace { get; set; } = null!;
}
