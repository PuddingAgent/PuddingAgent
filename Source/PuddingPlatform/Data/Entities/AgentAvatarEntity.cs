using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 系统预置 Agent 头像元数据表。
/// 由 avatars.json 种子产生，服务端统一管理所有预置头像。
/// 模板通过 AvatarId 引用，前端只消费解析后的 URL。
/// </summary>
public class AgentAvatarEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>稳定业务 ID，如 "neutral"、"thinking"</summary>
    [Required, MaxLength(128)]
    public string AvatarId { get; set; } = string.Empty;

    /// <summary>展示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>PNG 文件名</summary>
    [Required, MaxLength(256)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>服务端静态 URL，如 "/assets/agent-avatars/agent-avatar-neutral.png"</summary>
    [Required, MaxLength(512)]
    public string UrlPath { get; set; } = string.Empty;

    /// <summary>人格气质描述</summary>
    [MaxLength(512)]
    public string? Personality { get; set; }

    /// <summary>发色描述</summary>
    [MaxLength(128)]
    public string? HairColor { get; set; }

    /// <summary>表情描述</summary>
    [MaxLength(128)]
    public string? Expression { get; set; }

    /// <summary>视觉特征 JSON 数组，如 ["像素二次元", "圆形头像", "柔和边缘光"]</summary>
    public string VisualTraitsJson { get; set; } = "[]";

    /// <summary>推荐使用场景</summary>
    [MaxLength(512)]
    public string? RecommendedUse { get; set; }

    /// <summary>是否系统内置</summary>
    public bool IsBuiltIn { get; set; } = true;

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>默认排序，SortOrder 最小且 IsEnabled 的头像为系统默认头像</summary>
    public int SortOrder { get; set; } = 100;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
