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
