using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>Delegation policy decision result.</summary>
public sealed record TaskDelegationDecision
{
    /// <summary>Whether the requested delegation action is allowed.</summary>
    public bool Allowed { get; init; }

    /// <summary>Machine-friendly denial reason; empty when allowed.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>当前节点深度。</summary>
    public int CurrentDepth { get; init; }

    /// <summary>计划配置的最大深度。</summary>
    public int MaxDepth { get; init; }
}

/// <summary>Policy boundary checks for depth, assignment, and team creation.</summary>
public interface ITaskDelegationPolicy
{
    /// <summary>Can this node be split further?</summary>
    Task<TaskDelegationDecision> CanSplitAsync(TaskNode node, TaskPlanRun plan, CancellationToken ct = default);

    /// <summary>Can this node be assigned to the requested target kind?</summary>
    Task<TaskDelegationDecision> CanAssignAsync(TaskNode node, TaskPlanRun plan, TaskAssignmentKinds assignmentKind, CancellationToken ct = default);

    /// <summary>Can current node / plan create team agents?</summary>
    Task<TaskDelegationDecision> CanCreateTeamAgentAsync(TaskPlanRun plan, TaskNode? currentNode, CancellationToken ct = default);
}
