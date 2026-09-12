using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tasks;

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
    /// <summary>
    /// 续行调用的显式父会话身份（A01-slice-1）。
    /// 调用方已知父会话（如 sub-agent 结果回流）时优先使用；
    /// 未提供时回退到 delivery metadata 中的持久父身份。
    /// </summary>
    public string? ParentConversationId { get; init; }
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

        // 不变式 I1：sub-agent 续行必须接续父会话身份，绝不伪造 msg-* 会话（A01-slice-1）。
        string? sessionId;
        string sessionSource;
        if (usesStreamDispatch)
        {
            (sessionId, sessionSource) = ResolveSubAgentContinuationSession(invocation, profile);
        }
        else
        {
            sessionId = profile.MainSessionId;
            sessionSource = SessionSourceMainSession;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException($"Agent '{invocation.AgentId}' does not have a bound main session.");

        var templateId = string.IsNullOrWhiteSpace(profile.SourceTemplateId)
            ? invocation.AgentId
            : profile.SourceTemplateId!;

        logger.LogInformation(
            "[AgentInvocation] resolved workspace-agent dispatch workspace={WorkspaceId} agent={AgentId} session={SessionId} sessionSource={SessionSource} template={TemplateId} stream={Stream} hasLlmConfig={HasLlmConfig} provider={ProviderId} model={ModelId} toolCount={ToolCount} skillCount={SkillCount}",
            invocation.WorkspaceId,
            invocation.AgentId,
            sessionId,
            sessionSource,
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
                LlmProfile = BuildLlmProfile(profile),
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
                ActiveTask = BuildActiveTask(invocation),
            },
        };
    }

    /// <summary>解析 task metadata → Active Task Runtime Context（复用 task_plan_id 同款模式）。</summary>
    private static ActiveTaskRuntimeContext? BuildActiveTask(WorkspaceAgentInvocation invocation)
    {
        var taskId = GetMetadataValue(invocation.Metadata, "task_id", "taskId", "TaskId");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        var assignmentId = GetMetadataValue(invocation.Metadata, "assignment_id", "assignmentId", "AssignmentId");
        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            return null;
        }

        return new ActiveTaskRuntimeContext
        {
            WorkspaceId = invocation.WorkspaceId,
            TaskId = taskId!,
            AssignmentId = assignmentId!,
            AgentId = invocation.AgentId,
            Origin = GetMetadataValue(invocation.Metadata, "origin", "Origin") ?? string.Empty,
            Priority = GetMetadataValue(invocation.Metadata, "priority", "Priority") ?? string.Empty,
            ExecutionWindow = GetMetadataValue(invocation.Metadata, "execution_window", "executionWindow", "ExecutionWindow") ?? string.Empty,
            ExpectedVersion = GetMetadataInt(invocation.Metadata, "expected_version", "expectedVersion", "ExpectedVersion"),
            PolicyVersion = GetMetadataValue(invocation.Metadata, "policy_version", "policyVersion", "PolicyVersion"),
            DispatchIdempotencyKey = GetMetadataValue(invocation.Metadata, "dispatch_idempotency_key", "dispatchIdempotencyKey", "DispatchIdempotencyKey"),
            ReservationFencingToken = GetMetadataValue(invocation.Metadata, "reservation_fencing_token", "reservationFencingToken", "ReservationFencingToken"),
        };
    }

    private static LlmInvocationProfile BuildLlmProfile(AgentRuntimeProfile profile)
    {
        var providerId = Require(profile.PreferredProviderId, "provider", profile.AgentId);
        var modelId = Require(profile.PreferredModelId ?? profile.LlmConfig?.ModelId, "model", profile.AgentId);
        return new LlmInvocationProfile
        {
            ProviderId = providerId,
            ProfileId = string.IsNullOrWhiteSpace(profile.ConsciousProfileId)
                ? $"agent:{profile.AgentId}:conscious"
                : profile.ConsciousProfileId!,
            ModelId = modelId,
            Role = "conscious",
        };
    }

    private static string Require(string? value, string field, string agentId)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Agent '{agentId}' does not have a resolved LLM {field}.");

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

    private const string SessionSourceParentParam = "parent_param";
    private const string SessionSourceParentIdentity = "parent_identity";
    private const string SessionSourceEventSession = "event_session";
    private const string SessionSourceMainSession = "main_session";

    /// <summary>
    /// 解析 sub-agent 续行的父会话身份（A01-slice-1 不变式 I1）。
    /// </summary>
    /// <remarks>
    /// 严格优先级：显式 ParentConversationId → 持久化父身份 metadata →
    /// 事件 session → 已绑定主会话；全空则抛异常（绝不新建 msg-* 会话）。
    /// </remarks>
    private (string? SessionId, string Source) ResolveSubAgentContinuationSession(
        WorkspaceAgentInvocation invocation,
        AgentRuntimeProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(invocation.ParentConversationId))
        {
            var parentParam = invocation.ParentConversationId!;
            WarnIfEventSessionDiffers(invocation, parentParam, SessionSourceParentParam);
            return (parentParam, SessionSourceParentParam);
        }

        var parentIdentity = GetMetadataValueCaseInsensitive(
            invocation.Metadata,
            "parent_conversation_id",
            "parent_session_id",
            "parent_session",
            "conversation_id");
        if (!string.IsNullOrWhiteSpace(parentIdentity))
        {
            WarnIfEventSessionDiffers(invocation, parentIdentity!, SessionSourceParentIdentity);
            return (parentIdentity, SessionSourceParentIdentity);
        }

        if (!string.IsNullOrWhiteSpace(invocation.EventSessionId))
            return (invocation.EventSessionId, SessionSourceEventSession);

        if (!string.IsNullOrWhiteSpace(profile.MainSessionId))
        {
            logger.LogWarning(
                "[AgentInvocation] sub-agent continuation has no persisted parent identity; falling back to bound main session workspace={WorkspaceId} agent={AgentId} message={MessageId} session={SessionId}. The delivery metadata likely lost parent_session/conversation_id.",
                invocation.WorkspaceId,
                invocation.AgentId,
                invocation.MessageId,
                profile.MainSessionId);
            return (profile.MainSessionId, SessionSourceMainSession);
        }

        return (null, SessionSourceMainSession);
    }

    /// <summary>
    /// 事件携带的 session 与持久父身份不一致时告警；父身份优先，事件 session 仅作回退。
    /// </summary>
    private void WarnIfEventSessionDiffers(
        WorkspaceAgentInvocation invocation,
        string parentSessionId,
        string source)
    {
        if (string.IsNullOrWhiteSpace(invocation.EventSessionId))
            return;

        if (string.Equals(invocation.EventSessionId, parentSessionId, StringComparison.Ordinal))
            return;

        logger.LogWarning(
            "[AgentInvocation] event session differs from persisted parent identity; parent identity wins workspace={WorkspaceId} agent={AgentId} message={MessageId} sessionSource={SessionSource} parentSession={ParentSessionId} eventSession={EventSessionId}",
            invocation.WorkspaceId,
            invocation.AgentId,
            invocation.MessageId,
            source,
            parentSessionId,
            invocation.EventSessionId);
    }

    private static string? GetMetadataValueCaseInsensitive(
        IReadOnlyDictionary<string, string>? metadata,
        params string[] keys)
    {
        if (metadata is null)
            return null;

        foreach (var key in keys)
        {
            foreach (var pair in metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pair.Value))
                    return pair.Value;
            }
        }

        return null;
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
