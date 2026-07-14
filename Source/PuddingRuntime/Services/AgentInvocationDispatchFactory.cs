using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Builds execution requests for LLM-backed agent invocations.
/// </summary>
/// <remarks>
/// The runtime has more than one way to invoke an agent: user chat, durable
/// message delivery, heartbeat prompts, sub-agent delegation, and future
/// tool-like calls into named agents. Those callers should describe the
/// invocation intent, not rebuild template routing, session binding, capability
/// policy, or task-planning metadata by hand.
///
/// This factory is the first stable boundary for that rule. It currently
/// covers workspace-agent invocations, because those require the strongest
/// ownership split: workspace agents own identity and main-session binding,
/// while source templates own LLM routing, tools, capabilities, and Skill
/// packages.
/// </remarks>
public interface IAgentInvocationDispatchFactory
{
    Task<AgentInvocationDispatch> CreateForWorkspaceAgentAsync(
        WorkspaceAgentInvocation invocation,
        CancellationToken ct = default);
}

public sealed record WorkspaceAgentInvocation
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string MessageId { get; init; }
    public required string MessageText { get; init; }
    public string? EventSessionId { get; init; }
    public string? UserId { get; init; }
    public PermissionSnapshot? PermissionSnapshot { get; init; }
    public MessageAddress? From { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record AgentInvocationDispatch
{
    public required RuntimeDispatchRequest Request { get; init; }
    public required bool UsesStreamDispatch { get; init; }
}

public sealed class AgentInvocationDispatchFactory(
    IAgentRuntimeProfileResolver profileResolver,
    ILogger<AgentInvocationDispatchFactory> logger) : IAgentInvocationDispatchFactory
{
    public async Task<AgentInvocationDispatch> CreateForWorkspaceAgentAsync(
        WorkspaceAgentInvocation invocation,
        CancellationToken ct = default)
    {
        var profile = await profileResolver.ResolveAsync(invocation.WorkspaceId, invocation.AgentId, ct);
        var usesStreamDispatch = ShouldUseStreamDispatch(invocation.Metadata);

        var sessionId = usesStreamDispatch
            ? invocation.EventSessionId ?? $"msg-{invocation.MessageId}"
            : profile.MainSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException($"Agent '{invocation.AgentId}' does not have a bound main session.");

        var templateId = string.IsNullOrWhiteSpace(profile.SourceTemplateId)
            ? invocation.AgentId
            : profile.SourceTemplateId!;

        logger.LogInformation(
            "[AgentInvocation] resolved workspace-agent dispatch workspace={WorkspaceId} agent={AgentId} session={SessionId} template={TemplateId} stream={Stream} hasLlmConfig={HasLlmConfig} provider={ProviderId} model={ModelId} toolCount={ToolCount} skillCount={SkillCount}",
            invocation.WorkspaceId,
            invocation.AgentId,
            sessionId,
            templateId,
            usesStreamDispatch,
            profile.LlmConfig is not null,
            profile.PreferredProviderId ?? "(none)",
            profile.LlmConfig?.ModelId ?? profile.PreferredModelId ?? "(none)",
            profile.ToolDefinitions?.Count ?? 0,
            profile.SkillPackages?.Count ?? 0);

        return new AgentInvocationDispatch
        {
            UsesStreamDispatch = usesStreamDispatch,
            Request = new RuntimeDispatchRequest
            {
                SessionId = sessionId!,
                WorkspaceId = invocation.WorkspaceId,
                AgentTemplateId = templateId,
                AgentInstanceId = invocation.AgentId,
                MessageText = invocation.MessageText,
                MessageId = invocation.MessageId,
                UserId = invocation.UserId,
                PermissionSnapshot = invocation.PermissionSnapshot,
                LlmConfig = profile.LlmConfig,
                CapabilityPolicy = profile.CapabilityPolicy,
                ToolDefinitions = profile.ToolDefinitions,
                SkillPackages = profile.SkillPackages,
                Origin = usesStreamDispatch ? null : BuildOrigin(invocation),
                TaskPlanId = GetMetadataValue(invocation.Metadata, "task_plan_id", "taskPlanId", "TaskPlanId"),
                TaskNodeId = GetMetadataValue(invocation.Metadata, "task_node_id", "taskNodeId", "TaskNodeId"),
                ParentTaskNodeId = GetMetadataValue(invocation.Metadata, "parent_task_node_id", "parentTaskNodeId", "ParentTaskNodeId"),
                DelegationDepth = GetMetadataInt(invocation.Metadata, "delegation_depth", "delegationDepth", "DelegationDepth"),
                MaxDelegationDepth = GetMetadataInt(invocation.Metadata, "max_delegation_depth", "maxDelegationDepth", "MaxDelegationDepth"),
                RoleInPlan = GetMetadataValue(invocation.Metadata, "role_in_plan", "roleInPlan", "RoleInPlan"),
                AllowSubDelegation = GetMetadataBool(invocation.Metadata, "allow_sub_delegation", "allowSubDelegation", "AllowSubDelegation"),
                AllowAgentCreation = GetMetadataBool(invocation.Metadata, "allow_agent_creation", "allowAgentCreation", "AllowAgentCreation"),
                AssignedObjective = GetMetadataValue(invocation.Metadata, "assigned_objective", "assignedObjective", "AssignedObjective"),
                ExpectedOutputContract = GetMetadataValue(invocation.Metadata, "expected_output_contract", "expectedOutputContract", "ExpectedOutputContract"),
            },
        };
    }

    private static MessageOrigin? BuildOrigin(WorkspaceAgentInvocation invocation)
    {
        if (invocation.From is null)
            return null;

        return new MessageOrigin
        {
            FromKind = invocation.From.Kind,
            FromId = invocation.From.Id,
            FromDisplayName = invocation.From.DisplayName,
            CorrelationId = invocation.CorrelationId,
            CausationId = invocation.CausationId,
            MessageType = ResolveMessageType(invocation.Metadata),
        };
    }

    private static bool ShouldUseStreamDispatch(IReadOnlyDictionary<string, string>? metadata)
    {
        var source = GetMetadataValue(metadata, "source", "Source");
        var intent = GetMetadataValue(metadata, "intent", "Intent");

        return string.Equals(source, "subagent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intent, "subagent_result", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMessageType(IReadOnlyDictionary<string, string>? metadata)
    {
        var explicitType = GetMetadataValue(metadata, "message_type", "messageType", "MessageType");
        if (!string.IsNullOrWhiteSpace(explicitType))
            return explicitType!;

        var intent = GetMetadataValue(metadata, "intent", "Intent");
        return string.Equals(intent, "subagent_result", StringComparison.OrdinalIgnoreCase)
            ? "subagent_result"
            : "agent_message";
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
    {
        if (metadata is null)
            return null;

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? GetMetadataInt(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
        => int.TryParse(GetMetadataValue(metadata, keys), out var value) ? value : null;

    private static bool? GetMetadataBool(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
        => bool.TryParse(GetMetadataValue(metadata, keys), out var value) ? value : null;
}
