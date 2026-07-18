using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// Resolves the complete runtime profile for a workspace agent instance.
/// </summary>
/// <remarks>
/// Agent execution has several ingress paths: Web chat, message delivery,
/// heartbeat, connector ingress, and future automation. Those paths must not
/// independently read agent manifests, template manifests, model providers, or
/// capability policy. The profile resolver is the application-service boundary
/// that turns configuration files and runtime indexes into one execution-ready
/// snapshot.
///
/// The key design constraint is ownership: workspace agents own identity,
/// avatar, enablement, and main-session binding; source templates own model
/// routing, capability policy, and Skill selection. Keeping that rule here
/// prevents controllers and queue consumers from re-implementing configuration
/// fallbacks whenever the storage layout changes.
/// </remarks>
public interface IAgentRuntimeProfileResolver
{
    Task<AgentRuntimeProfile> ResolveAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default);
}

/// <summary>
/// Agent 实例配置无法形成可执行 Profile。
/// 该异常属于可预期的配置终态，不应被归类为 Worker/数据库等基础设施故障。
/// </summary>
public sealed class AgentConfigurationException : Exception
{
    public AgentConfigurationException(string agentId, string message)
        : base(message)
    {
        AgentId = agentId;
    }

    public string AgentId { get; }
    public string ErrorCode => TerminalErrorCodes.AgentConfigurationInvalid;
}

public sealed record AgentRuntimeProfile
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public string? MainSessionId { get; init; }
    public string? SourceTemplateId { get; init; }
    public string? ConsciousProfileId { get; init; }
    public string? PreferredProviderId { get; init; }
    public string? PreferredModelId { get; init; }
    public LlmConfig? LlmConfig { get; init; }
    public string? SystemPrompt { get; init; }
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public IReadOnlyList<LlmToolDefinition>? ToolDefinitions { get; init; }
    public IReadOnlyList<SkillPackageInfo>? SkillPackages { get; init; }
    public int? MaxRounds { get; init; }
    public int? MaxElapsedSeconds { get; init; }
    public int? MaxContextTokens { get; init; }
    public string CapabilitySource { get; init; } = "none";
    public int CapabilityCount { get; init; }
}
