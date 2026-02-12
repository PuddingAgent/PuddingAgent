using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingCode.Abstractions;

/// <summary>
/// Unified agent firewall. All safety checks — mode, session, capability,
/// authorization, sandbox, workspace, resource, and state — are evaluated
/// through a single entry point instead of being scattered across tool code.
/// </summary>
public interface IAgentFirewall
{
    /// <summary>
    /// Evaluate all active gates in sequence and return a single pass / deny decision.
    /// </summary>
    Task<FirewallDecision> EvaluateAsync(FirewallContext ctx, CancellationToken ct);
}

/// <summary>
/// Context passed into the firewall for a single evaluation.
/// All eight gates consume this same context object.
/// </summary>
public sealed record FirewallContext
{
    // ── Identity ──
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? UserId { get; init; }
    public string? AgentTemplateId { get; init; }

    // ── Operation ──
    public required string ToolId { get; init; }
    public string? ArgumentsJson { get; init; }
    public CapabilityPolicy? Policy { get; init; }

    // ── Runtime state ──
    public RuntimeExecutionMode RuntimeMode { get; init; }
    public string? AgentStatus { get; init; }
    public DateTime? LastCompletedAt { get; init; }

    // ── Scenario markers (control which gates activate) ──
    public bool IsHeartbeat { get; init; }
    public bool IsAgentToAgent { get; init; }

    /// <summary>
    /// Convenience factory from the standard <see cref="ToolExecutionContext"/>.
    /// </summary>
    public static FirewallContext FromExecutionContext(
        ToolExecutionContext context,
        CapabilityPolicy? policy = null,
        RuntimeExecutionMode mode = RuntimeExecutionMode.Normal,
        string? argumentsJson = null,
        string? toolId = null,
        bool isHeartbeat = false,
        bool isAgentToAgent = false) => new()
    {
        WorkspaceId = context.WorkspaceId,
        SessionId = context.SessionId,
        AgentInstanceId = context.AgentInstanceId,
        AgentTemplateId = context.AgentTemplateId,
        UserId = context.Trace?.UserId,
        ArgumentsJson = argumentsJson,
        ToolId = toolId ?? string.Empty,
        Policy = policy,
        RuntimeMode = mode,
        IsHeartbeat = isHeartbeat,
        IsAgentToAgent = isAgentToAgent,
    };
}

/// <summary>
/// Result of a firewall evaluation.
/// </summary>
public sealed record FirewallDecision
{
    public bool Allowed { get; init; }
    public string? DenyReason { get; init; }
    public FirewallGate DeniedAtGate { get; init; }

    public static FirewallDecision Allow() => new() { Allowed = true };
    public static FirewallDecision Deny(string reason, FirewallGate gate) => new()
    {
        Allowed = false,
        DenyReason = reason,
        DeniedAtGate = gate,
    };
}

/// <summary>
/// Ordered gate identifiers. A denial reports which gate blocked the request.
/// </summary>
public enum FirewallGate
{
    None,
    Mode,          // Gate 1 — runtime execution mode (YOLO / Safe / EStop)
    Session,       // Gate 2 — session lifecycle (Faulted / Stopped)
    Capability,    // Gate 3 — tool allowed by capability policy
    Authorization, // Gate 4 — runtime / implicit authorization
    Sandbox,       // Gate 5 — sandbox policy
    Workspace,     // Gate 6 — host file workspace boundary
    Resource,      // Gate 7 — resource permissions (shell / file-write / network)
    State,         // Gate 8 — agent state (cooldown / concurrency)
}
