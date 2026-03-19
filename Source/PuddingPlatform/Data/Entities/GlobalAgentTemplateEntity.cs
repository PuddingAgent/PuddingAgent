using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 全局系统内置 Agent 模板。
/// Platform 管理员配置，跨所有 Workspace 共享使用。
/// </summary>
public class GlobalAgentTemplateEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>唯一标识符（slug），如 "assistant", "code-reviewer"</summary>
    [Required, MaxLength(128)]
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>功能描述</summary>
    [MaxLength(2048)]
    public string? Description { get; set; }

    /// <summary>
    /// Agent 角色类型，与 PuddingCore 中 AgentTemplateType 对齐。
    /// 值：Service | Task | Audit | Custom
    /// </summary>
    [Required, MaxLength(32)]
    public string Role { get; set; } = "Service";

    /// <summary>系统 Prompt（对话前置指令）</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>用户 Prompt 模板（可含 {{variable}} 占位符）</summary>
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

    /// <summary>Agent 容器镜像（如 docker.xuanyuan.run/library/ubuntu:latest），留空则使用平台默认镜像</summary>
    [MaxLength(512)]
    public string? ContainerImage { get; set; }

    // ── 能力策略 ─────────────────────────────────────────────────
    /// <summary>是否允许执行 Shell/Bash 命令（BashSkill）。</summary>
    public bool AllowShellExecution { get; set; } = false;
    /// <summary>是否允许写文件。</summary>
    public bool AllowFileWrite { get; set; } = false;
    /// <summary>是否允许网络访问。</summary>
    public bool AllowNetworkAccess { get; set; } = false;
    /// <summary>工具白名单（JSON 数组，如 ["bash","file_write"]），空表示不额外限制。</summary>
    public string AllowedToolNamesJson { get; set; } = "[]";

    /// <summary>所选能力 ID 列表（JSON 数组，如 ["cap-bash"]）。</summary>
    public string SelectedCapabilityIdsJson { get; set; } = "[]";

    /// <summary>所选 Skill 包 ID 列表（JSON 数组）。Runtime 启动容器时挂载对应包目录。</summary>
    public string SelectedSkillPackageIdsJson { get; set; } = "[]";

    /// <summary>是否系统内置（内置模板不允许删除，只允许编辑）</summary>
    public bool IsBuiltIn { get; set; } = false;

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>排序权重</summary>
    public int SortOrder { get; set; } = 100;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
