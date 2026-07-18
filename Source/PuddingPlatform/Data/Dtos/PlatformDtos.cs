namespace PuddingPlatform.Data.Dtos;

// ════════════════════════════════════════════════════════════════
// Resource DTOs — KeyVault, KnowledgeBase, Workflow, Skill, Channel。
// LLM/Voice → PlatformDtos.Llm.cs
// Agent/Template → PlatformDtos.Agent.cs
// Identity/Team/Workspace → PlatformDtos.Identity.cs
// Chat Proxy → PlatformDtos.Chat.cs
// ════════════════════════════════════════════════════════════════

// ── KeyVault 密钥管理 ─────────────────────────────────────────

/// <summary>密钥列表项（不含明文 Value）。</summary>
public record KeyVaultSecretDto(
    long Id,
    string KeyVaultId,
    string Name,
    string? Description,
    string Category,
    List<string> Tags,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>密钥详情（含明文 Value，仅管理后台可见）。</summary>
public record KeyVaultSecretDetailDto(
    long Id,
    string KeyVaultId,
    string Name,
    string? Description,
    string Category,
    List<string> Tags,
    string? Value,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateKeyVaultSecretRequest(
    string Name,
    string Value,
    string? Description,
    string Category,
    List<string>? Tags
);

public record UpdateKeyVaultSecretRequest(
    string Name,
    string? Value,
    string? Description,
    string Category,
    List<string>? Tags
);

/// <summary>KeyVault 文本注入请求（将 {{vault:key}} 占位符替换为实际密钥值）。</summary>
public record KeyVaultTextTransformRequest(string Text);

/// <summary>KeyVault 文本注入响应（占位符已替换）。</summary>
public record KeyVaultTextTransformResponse(string Text);

// ── Workflow 工作流 ────────────────────────────────────────────

public record WorkflowDto(
    string WorkflowId,
    string Name,
    string? Description,
    string? DefinitionJson,
    string Status,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertWorkflowRequest(
    string Name,
    string? Description,
    string? DefinitionJson,
    string Status,
    bool IsEnabled
);

// ── KnowledgeBase 知识库 ───────────────────────────────────────

public record KnowledgeBaseDto(
    string KbId,
    string Name,
    string? Description,
    string KbType,
    int DocumentCount,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertKnowledgeBaseRequest(
    string Name,
    string? Description,
    string KbType,
    bool IsEnabled
);

// ── WorkspaceSkill 工作区 Skill ────────────────────────────────

public record WorkspaceSkillDto(
    string SkillId,
    string Name,
    string? Description,
    string SkillType,
    string? ConfigJson,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertWorkspaceSkillRequest(
    string Name,
    string? Description,
    string SkillType,
    string? ConfigJson,
    bool IsEnabled
);

// ── WorkspaceChannel 工作区渠道 ────────────────────────────────

public record WorkspaceChannelDto(
    string ChannelId,
    string Name,
    string? Description,
    string ChannelType,
    string? DefaultAgentId,
    string? ConfigJson,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record UpsertWorkspaceChannelRequest(
    string Name,
    string? Description,
    string ChannelType,
    string? DefaultAgentId,
    string? ConfigJson,
    bool IsEnabled
);
