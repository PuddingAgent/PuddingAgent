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

    /// <summary>声明的 Skill ID 列表。</summary>
    public IReadOnlyList<string> SkillIds { get; init; } = [];
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
    public IReadOnlyList<string> AllowedToolNames { get; init; } = [];
}

/// <summary>Agent 运行画像。</summary>
public sealed record RuntimeProfile
{
    public string? PreferredModel { get; init; }
    public int MaxContextTokens { get; init; } = 8192;
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
