using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>工作空间内配置的 Agent 实例（已部署，区别于 AgentTemplate 定义）。</summary>
public class WorkspaceAgentEntity
{
    public int Id { get; set; }

    /// <summary>业务层唯一 ID（GUID）。</summary>
    public string AgentId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>所属工作空间的 PK。</summary>
    public int WorkspaceEntityId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Agent 展示名称（聊天界面显示）。未设置时回退到 Name。</summary>
    public string? DisplayName { get; set; }

    /// <summary>Agent 头像 URL。legacy fallback，未设置时从模板解析。</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>系统预置头像 ID，对应 AgentAvatarEntity.AvatarId。优先级最高。</summary>
    [MaxLength(128)]
    public string? AvatarId { get; set; }

    /// <summary>来源模板 ID（WorkspaceAgentTemplate 或 GlobalAgentTemplate 的 TemplateId）。</summary>
    public string? SourceTemplateId { get; set; }

    /// <summary>覆盖模板的系统提示词（可选）。</summary>
    public string? SystemPromptOverride { get; set; }

    /// <summary>优先使用的 LLM 服务商 ID（可选，默认跟随模板）。</summary>
    public string? PreferredProviderId { get; set; }

    /// <summary>优先使用的模型 ID（可选）。</summary>
    public string? PreferredModelId { get; set; }

    public bool IsEnabled { get; set; } = true;
    public bool IsFrozen { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public WorkspaceEntity Workspace { get; set; } = null!;
}
