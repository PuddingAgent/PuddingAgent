namespace PuddingCode.Platform;

/// <summary>Workspace 配置定义，由 Controller 加载并分发。</summary>
public sealed record WorkspaceDefinition
{
    public required string WorkspaceId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsFrozen { get; init; }

    /// <summary>渠道绑定列表。</summary>
    public IReadOnlyList<ChannelBindingDefinition> ChannelBindings { get; init; } = [];

    /// <summary>Agent 模板 ID 列表。</summary>
    public IReadOnlyList<string> AgentTemplateIds { get; init; } = [];

    /// <summary>审计 Agent 模板 ID（至少 1 个）。</summary>
    public IReadOnlyList<string> AuditAgentTemplateIds { get; init; } = [];

    /// <summary>权限策略。</summary>
    public PermissionPolicyDefinition? PermissionPolicy { get; init; }

    /// <summary>知识库配置。</summary>
    public KnowledgeBaseDefinition? KnowledgeBase { get; init; }

    /// <summary>统一存储绑定。</summary>
    public StorageBindingDefinition? StorageBinding { get; init; }

    /// <summary>知识图谱配置。</summary>
    public KnowledgeGraphDefinition? KnowledgeGraph { get; init; }

    /// <summary>工作流绑定列表：workflow id 到渠道/触发条件的映射。</summary>
    public IReadOnlyList<WorkflowBindingDefinition> WorkflowBindings { get; init; } = [];
}

/// <summary>渠道绑定：将某个 ChannelType 绑定到 Workspace。</summary>
public sealed record ChannelBindingDefinition
{
    public required string ChannelId { get; init; }
    public required string ChannelType { get; init; }
    public string? DefaultAgentTemplateId { get; init; }
    public IReadOnlyList<string> AllowedAgentTemplateIds { get; init; } = [];
    public Dictionary<string, string> Properties { get; init; } = [];
}

/// <summary>权限策略定义。</summary>
public sealed record PermissionPolicyDefinition
{
    public bool DefaultDeny { get; init; } = true;
    public IReadOnlyList<string> AllowedRoles { get; init; } = [];
}

/// <summary>知识库定义。</summary>
public sealed record KnowledgeBaseDefinition
{
    public bool Enabled { get; init; }
    public IReadOnlyList<string> SourceDirectories { get; init; } = [];
}

/// <summary>统一存储绑定定义。</summary>
public sealed record StorageBindingDefinition
{
    public string? ObjectStorageEndpoint { get; init; }
    public string? NfsMountPath { get; init; }
}

/// <summary>知识图谱定义。</summary>
public sealed record KnowledgeGraphDefinition
{
    public bool Enabled { get; init; }
    public string? ConnectionString { get; init; }
}

/// <summary>工作流绑定：将某个 WorkflowId 绑定到 Workspace（可指定触发渠道）。</summary>
public sealed record WorkflowBindingDefinition
{
    public required string WorkflowId { get; init; }
    public string? Name { get; init; }

    /// <summary>为空表示所有渠道均可触发；不为空则仅限指定渠道。</summary>
    public IReadOnlyList<string> TriggerChannelIds { get; init; } = [];

    /// <summary>是否自动在 Workspace 创建/初始化时触发。</summary>
    public bool AutoTriggerOnInit { get; init; }
}
