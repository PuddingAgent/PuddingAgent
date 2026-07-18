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
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public IReadOnlyList<LlmToolDefinition>? ToolDefinitions { get; init; }
    public IReadOnlyList<SkillPackageInfo>? SkillPackages { get; init; }
    public string CapabilitySource { get; init; } = "none";
    public int CapabilityCount { get; init; }
}
