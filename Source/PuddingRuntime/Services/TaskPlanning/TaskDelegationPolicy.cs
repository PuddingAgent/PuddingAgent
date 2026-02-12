using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;

namespace PuddingRuntime.Services.TaskPlanning;

public sealed class TaskDelegationPolicy : ITaskDelegationPolicy
{
    private readonly ITaskPlanStore _taskPlanStore;
    private readonly TaskPlanningOptions _defaults;

    private static readonly HashSet<TaskNodeStatuses> TerminalStatuses =
    [
        TaskNodeStatuses.Completed,
        TaskNodeStatuses.Failed,
        TaskNodeStatuses.Cancelled,
        TaskNodeStatuses.Superseded,
    ];

    public TaskDelegationPolicy(
        ITaskPlanStore taskPlanStore,
        IOptions<TaskPlanningOptions> options)
    {
        _taskPlanStore = taskPlanStore;
        _defaults = options.Value;
    }

    public async Task<TaskDelegationDecision> CanSplitAsync(
        TaskNode node,
        TaskPlanRun plan,
        CancellationToken ct = default)
    {
        var maxDepth = ResolveMaxDelegationDepth(plan);
        if (node.Depth >= maxDepth)
            return Deny(node.Depth, maxDepth, "delegation_denied:max_depth_reached");

        if (!node.AllowSubDelegation)
            return Deny(node.Depth, maxDepth, "delegation_denied:sub_delegation_disabled");

        var activeCount = await GetActiveNodeCountAsync(plan, ct);
        var maxActive = ResolveMaxActiveNodeLimit(plan);
        if (activeCount >= maxActive)
            return Deny(node.Depth, maxDepth, "delegation_denied:active_node_limit_reached");

        return Allow(node.Depth, maxDepth);
    }

    public async Task<TaskDelegationDecision> CanAssignAsync(
        TaskNode node,
        TaskPlanRun plan,
        TaskAssignmentKinds assignmentKind,
        CancellationToken ct = default)
    {
        var maxDepth = ResolveMaxDelegationDepth(plan);
        var activeCount = await GetActiveNodeCountAsync(plan, ct);
        var maxActive = ResolveMaxActiveNodeLimit(plan);
        if (activeCount >= maxActive)
            return Deny(node.Depth, maxDepth, "delegation_denied:active_node_limit_reached");

        if (assignmentKind is TaskAssignmentKinds.SubAgent && node.Depth >= maxDepth)
            return Deny(node.Depth, maxDepth, "delegation_denied:max_depth_reached");

        return Allow(node.Depth, maxDepth);
    }

    public Task<TaskDelegationDecision> CanCreateTeamAgentAsync(
        TaskPlanRun plan,
        TaskNode? currentNode,
        CancellationToken ct = default)
    {
        var maxDepth = ResolveMaxDelegationDepth(plan);
        var currentDepth = currentNode?.Depth ?? 0;

        if (!ResolveAllowAgentCreation(plan))
            return Task.FromResult(Deny(currentDepth, maxDepth, "delegation_denied:agent_creation_disabled"));

        if (currentNode is not null && !currentNode.AllowAgentCreation)
            return Task.FromResult(Deny(currentDepth, maxDepth, "delegation_denied:agent_creation_disabled"));

        if (currentNode is not null && currentNode.Depth >= maxDepth)
            return Task.FromResult(Deny(currentDepth, maxDepth, "delegation_denied:max_depth_reached"));

        return Task.FromResult(Allow(currentDepth, maxDepth));
    }

    private Task<int> GetActiveNodeCountAsync(TaskPlanRun plan, CancellationToken ct)
    {
        var maxActive = ResolveMaxActiveNodeLimit(plan);
        return GetActiveNodeCountAsync(plan.PlanId, maxActive, ct);
    }

    private async Task<int> GetActiveNodeCountAsync(
        string planId,
        int maxActiveLimit,
        CancellationToken ct)
    {
        var limit = Math.Max(maxActiveLimit + 1, 1);
        var nodes = await _taskPlanStore.QueryNodesAsync(new TaskNodeQuery
        {
            PlanId = planId,
            Limit = limit,
        }, ct);

        return nodes.Count(node => !TerminalStatuses.Contains(node.Status));
    }

    private int ResolveMaxDelegationDepth(TaskPlanRun plan) =>
        plan.MaxDelegationDepth > 0
            ? plan.MaxDelegationDepth
            : _defaults.MaxDelegationDepth;

    private int ResolveMaxActiveNodeLimit(TaskPlanRun plan) =>
        plan.MaxActiveTaskNodesPerPlan > 0
            ? plan.MaxActiveTaskNodesPerPlan
            : _defaults.MaxActiveTaskNodesPerPlan;

    private bool ResolveAllowAgentCreation(TaskPlanRun plan) =>
        plan.AllowAgentCreationByLeader && _defaults.AllowAgentCreationByLeader;

    private static TaskDelegationDecision Allow(int currentDepth, int maxDepth) => new()
    {
        Allowed = true,
        Reason = "allowed",
        CurrentDepth = currentDepth,
        MaxDepth = maxDepth,
    };

    private static TaskDelegationDecision Deny(int currentDepth, int maxDepth, string reason) => new()
    {
        Allowed = false,
        Reason = reason,
        CurrentDepth = currentDepth,
        MaxDepth = maxDepth,
    };
}
