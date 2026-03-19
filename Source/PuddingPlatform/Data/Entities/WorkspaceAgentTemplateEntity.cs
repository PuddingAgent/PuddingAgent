using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Workspace 级 Agent 模板。
/// 属于某个 Workspace，可从全局模板继承并覆盖配置。
/// </summary>
public class WorkspaceAgentTemplateEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>所属 Workspace ID（字符串 slug，跨服务通过 ID 联结）</summary>
    [Required, MaxLength(128)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>该模板在 Workspace 内的唯一标识</summary>
    [Required, MaxLength(128)]
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>描述</summary>
    [MaxLength(2048)]
    public string? Description { get; set; }

    /// <summary>
    /// Agent 角色类型。值：Service | Task | Audit | Custom
    /// </summary>
    [Required, MaxLength(32)]
    public string Role { get; set; } = "Service";

    /// <summary>系统 Prompt</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>用户 Prompt 模板</summary>
    public string? UserPromptTemplate { get; set; }

    /// <summary>首选服务商（Provider.ProviderId）</summary>
    [MaxLength(64)]
    public string? PreferredProviderId { get; set; }

    /// <summary>首选模型（Model.ModelId）</summary>
    [MaxLength(128)]
    public string? PreferredModelId { get; set; }

    /// <summary>最大上下文 token 数</summary>
    public int MaxContextTokens { get; set; } = 8192;

    /// <summary>每轮最大回复 token 数</summary>
    public int MaxReplyTokens { get; set; } = 2048;

    /// <summary>
    /// 继承自某个全局模板（可选）。继承后覆盖字段优先于全局模板。
    /// </summary>
    [MaxLength(128)]
    public string? BaseGlobalTemplateId { get; set; }

    /// <summary>Agent 容器镜像（如 docker.xuanyuan.run/library/ubuntu:latest），留空则继承全局模板或使用平台默认</summary>
    [MaxLength(512)]
    public string? ContainerImage { get; set; }

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>排序权重</summary>
    public int SortOrder { get; set; } = 100;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
