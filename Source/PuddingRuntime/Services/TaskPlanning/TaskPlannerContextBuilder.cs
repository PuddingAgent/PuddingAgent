using System.Text;
using Microsoft.Extensions.Options;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services.TaskPlanning;

public sealed class TaskPlannerContextBuilder
{
    private readonly TaskPlanningOptions _options;

    public TaskPlannerContextBuilder(IOptions<TaskPlanningOptions> options)
    {
        _options = options.Value;
    }

    public Task<string> BuildAsync(ContextRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.TaskPlanId) || string.IsNullOrWhiteSpace(request.TaskNodeId))
            return Task.FromResult(string.Empty);

        ct.ThrowIfCancellationRequested();

        var delegationDepth = Math.Max(0, request.DelegationDepth ?? 0);
        var maxDelegationDepth = ResolveMaxDelegationDepth(request.MaxDelegationDepth);
        var allowSubDelegation = request.AllowSubDelegation ?? _options.DefaultAllowSubDelegation;
        var allowedToDelegate = allowSubDelegation && delegationDepth < maxDelegationDepth;
        var allowedToCreateAgents = request.AllowAgentCreation ?? false;

        var sb = new StringBuilder();
        sb.AppendLine("--- TASK PLANNING CONTEXT ---");
        sb.AppendLine($"plan_id: {request.TaskPlanId}");
        sb.AppendLine($"task_node_id: {request.TaskNodeId}");
        sb.AppendLine($"parent_task_node_id: {NormalizeOptional(request.ParentTaskNodeId)}");
        sb.AppendLine($"delegation_depth: {delegationDepth}");
        sb.AppendLine($"max_delegation_depth: {maxDelegationDepth}");
        sb.AppendLine($"role_in_plan: {NormalizeOptional(request.RoleInPlan)}");
        sb.AppendLine($"allowed_to_delegate: {FormatBool(allowedToDelegate)}");
        sb.AppendLine($"allowed_to_create_agents: {FormatBool(allowedToCreateAgents)}");
        sb.AppendLine("assigned_objective:");
        sb.AppendLine(NormalizeBlock(request.AssignedObjective));
        sb.AppendLine("expected_output:");
        sb.AppendLine(NormalizeBlock(request.ExpectedOutputContract));
        sb.AppendLine("constraints:");
        sb.AppendLine("- do not exceed max delegation depth");
        sb.AppendLine("- do not create agents unless allowed");
        sb.AppendLine("- report completion through report_task_result");

        if (!allowedToDelegate)
            sb.AppendLine("- complete this task yourself or report blockage instead of creating child tasks");

        return Task.FromResult(sb.ToString());
    }

    private int ResolveMaxDelegationDepth(int? value)
    {
        if (value is > 0)
            return value.Value;

        return _options.MaxDelegationDepth > 0 ? _options.MaxDelegationDepth : 2;
    }

    private static string NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string NormalizeBlock(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string FormatBool(bool value)
        => value ? "true" : "false";
}
