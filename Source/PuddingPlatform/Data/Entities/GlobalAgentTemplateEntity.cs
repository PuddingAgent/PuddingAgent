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

    // ── 个性层（Persona）─────────────────────────────────────
    /// <summary>人设与语气提示词（SOUL）。定义 Agent 的性格、边界、回复风格。</summary>
    [MaxLength(8000)]
    public string? PersonaPrompt { get; set; }

    /// <summary>工具使用约定描述（TOOLS）。解释用户自定义工具的用途和用法。</summary>
    [MaxLength(8000)]
    public string? ToolsDescription { get; set; }

    /// <summary>首次对话引导模板（BOOTSTRAP）。新会话首轮使用的问答模板。</summary>
    [MaxLength(4000)]
    public string? BootstrapTemplate { get; set; }

    /// <summary>Agent 展示用 Emoji（如 🤖🧠🔧）。legacy fallback，新功能不写入。</summary>
    [MaxLength(8)]
    public string? AvatarEmoji { get; set; }

    /// <summary>系统预置头像 ID，对应 AgentAvatarEntity.AvatarId。新功能主路径。</summary>
    [MaxLength(128)]
    public string? AvatarId { get; set; }

    /// <summary>首选服务商（Provider.ProviderId）</summary>
    [MaxLength(64)]
    public string? PreferredProviderId { get; set; }

    /// <summary>首选模型（Model.ModelId）</summary>
    [MaxLength(128)]
    public string? PreferredModelId { get; set; }

    /// <summary>记忆专用 LLM Endpoint（独立于主聊天模型）。null 时使用主模型。</summary>
    [MaxLength(512)]
    public string? MemoryLlmEndpoint { get; set; }

    /// <summary>记忆专用 LLM ApiKey 或 KeyVault 引用。</summary>
    [MaxLength(512)]
    public string? MemoryLlmApiKey { get; set; }

    /// <summary>记忆专用 LLM ModelId（建议使用轻量模型）。</summary>
    [MaxLength(128)]
    public string? MemoryLlmModelId { get; set; }

    /// <summary>记忆搜索模式：off | instant | deep</summary>
    [MaxLength(16)]
    public string MemorySearchMode { get; set; } = "deep";

    /// <summary>推理深度："low" | "medium" | "high"，null 表示跟随模型默认</summary>
    [MaxLength(16)]
    public string? ReasoningEffort { get; set; }

    /// <summary>Agent Loop 最大轮次</summary>
    public int MaxRounds { get; set; } = 200;

    /// <summary>Agent Loop 最大总耗时（秒）</summary>
    public int MaxElapsedSeconds { get; set; } = 1200;

    /// <summary>Agent Loop 最大工具调用次数</summary>
    public int MaxToolCallsTotal { get; set; } = 100;

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
