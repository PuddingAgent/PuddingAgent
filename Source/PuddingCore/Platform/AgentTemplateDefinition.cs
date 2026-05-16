namespace PuddingCode.Platform;

/// <summary>Agent 模板定义，描述可被 Runtime 实例化的 Agent 蓝图。</summary>
public sealed record AgentTemplateDefinition
{
    public required string TemplateId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required AgentTemplateType TemplateType { get; init; }

    /// <summary>能力策略。</summary>
    public CapabilityPolicy? Capability { get; init; }

    /// <summary>运行画像。</summary>
    public RuntimeProfile? Runtime { get; init; }

    /// <summary>记忆策略。</summary>
    public MemoryPolicy? Memory { get; init; }

    /// <summary>心跳策略。</summary>
    public HeartbeatPolicy? Heartbeat { get; init; }

    /// <summary>系统提示词模板路径或内联文本。</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>展示名称（可选，默认回退 Name）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>Agent 展示用 Emoji（如 🤖🧠🔧）。</summary>
    public string? AvatarEmoji { get; init; }

    /// <summary>人设与语气提示词（SOUL）。定义 Agent 的性格、边界、回复风格。</summary>
    public string? PersonaPrompt { get; init; }

    /// <summary>工具使用约定描述（TOOLS）。解释用户自定义工具的用途和用法。</summary>
    public string? ToolsDescription { get; init; }

    /// <summary>首次对话引导模板（BOOTSTRAP）。新会话首轮使用的问答模板。</summary>
    public string? BootstrapTemplate { get; init; }

    /// <summary>声明的 Skill ID 列表。</summary>
    public IReadOnlyList<string> SkillIds { get; init; } = [];

    /// <summary>允许的工具 SkillId 白名单。为空表示全部允许。</summary>
    public List<string>? AllowedSkillIds { get; set; }

    /// <summary>推理深度："low" | "medium" | "high"，null 表示跟随模型默认</summary>
    public string? ReasoningEffort { get; init; }
}

public enum AgentTemplateType
{
    Service,
    Task,
    Audit
}

/// <summary>Agent 能力策略。</summary>
public sealed record CapabilityPolicy
{
    public bool AllowShellExecution { get; init; }
    public bool AllowFileWrite { get; init; }
    public bool AllowNetworkAccess { get; init; }

    /// <summary>
    /// [V1 遗留] 全局白名单 — 若不为空，只允许此列表中的工具执行。
    /// V2 中由 DefaultToolNames + RequiresGrantToolNames 联合决定。
    /// SkillRuntime 读取时优先使用新字段。
    /// </summary>
    public IReadOnlyList<string> AllowedToolNames { get; init; } = [];

    /// <summary>
    /// V2: 默认工具 — 始终可用（只读、记忆、子代理等低风险能力）。
    /// 在管理界面"默认能力"区域展示。
    /// </summary>
    public IReadOnlyList<string> DefaultToolNames { get; init; } = [];

    /// <summary>
    /// V2: 需要显式授权的高权限工具（Shell、文件写入、Python 等）。
    /// Agent 不可自动获取，需在模板配置中显式勾选。
    /// 在管理界面"高权限能力"区域展示。
    /// </summary>
    public IReadOnlyList<string> RequiresGrantToolNames { get; init; } = [];

    /// <summary>
    /// V2 便利方法：合并两列表得出全部可用工具名。
    /// </summary>
    public HashSet<string> GetAllEffectiveToolNames() =>
        DefaultToolNames.Concat(RequiresGrantToolNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Agent 运行画像。</summary>
public sealed record RuntimeProfile
{
    public string? PreferredModel { get; init; }
    public int? MaxContextTokens { get; init; }
    public int MaxTurnsPerSession { get; init; } = 100;
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>Agent 记忆策略。</summary>
public sealed record MemoryPolicy
{
    public bool EnableSessionMemory { get; init; } = true;
    public bool EnableWorkspaceMemory { get; init; }
    public bool AllowPublicSourceWrite { get; init; }
}

/// <summary>Agent 心跳策略。</summary>
public sealed record HeartbeatPolicy
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);
    public int MissedThreshold { get; init; } = 3;
}
