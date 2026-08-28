using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Read-only Backlog refinement gate. It only examines Tasks that explicitly
/// opted into automatic dispatch and never mutates Task state.
/// </summary>
public sealed class TaskBacklogRefinementEvaluator(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IWorkspaceAgentCatalog agentCatalog,
    IOptions<TaskAutoDispatchOptions> options) : ITaskBacklogRefinementEvaluator
{
    private readonly TaskAutoDispatchOptions _options = options.Value;

    public async Task<IReadOnlyList<TaskBacklogRefinementDecision>> EvaluateAsync(
        string workspaceId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tasks = await db.WorkspaceTasks
            .Where(task => task.WorkspaceId == workspaceId
                && task.Status == WorkspaceTaskStatus.Backlog
                && task.AutoDispatchEnabled)
            .OrderBy(task => task.Priority)
            .ThenBy(task => task.SortOrder)
            .ThenBy(task => task.TaskId)
            .AsNoTracking()
            .Take(limit)
            .ToListAsync(ct);
        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        var decisions = new List<TaskBacklogRefinementDecision>(tasks.Count);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Description))
            {
                decisions.Add(Needs(task, "description_required"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(task.AcceptanceCriteria))
            {
                decisions.Add(Needs(task, "acceptance_criteria_required"));
                continue;
            }
            if (string.Equals(task.TaskType, "general", StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(Needs(task, "task_type_unclassified"));
                continue;
            }

            _options.TaskTypeRoutes.TryGetValue(task.TaskType, out var typeRoute);
            var route = agents
                .Select(agent => (Agent: agent, Route: TaskAgentRouteMatcher.Evaluate(task, agent, typeRoute)))
                .Where(item => item.Route.Compatible)
                .OrderByDescending(item => string.Equals(
                    task.PreferredAgentId, item.Agent.AgentId, StringComparison.Ordinal))
                .ThenBy(item => item.Agent.AgentId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (route.Agent is null)
            {
                decisions.Add(Needs(task, "no_compatible_agent"));
                continue;
            }

            decisions.Add(new TaskBacklogRefinementDecision
            {
                WorkspaceId = task.WorkspaceId,
                TaskId = task.TaskId,
                TaskVersion = task.Version,
                TaskType = task.TaskType,
                Verdict = TaskBacklogRefinementVerdict.ReadyCandidate,
                Code = "ready_for_auto_dispatch",
                CompatibleAgentId = route.Agent.AgentId,
                AgentRoutingFingerprint = route.Route.Fingerprint,
            });
        }
        return decisions;
    }

    private static TaskBacklogRefinementDecision Needs(
        Data.Entities.WorkspaceTaskEntity task,
        string code) => new()
    {
        WorkspaceId = task.WorkspaceId,
        TaskId = task.TaskId,
        TaskVersion = task.Version,
        TaskType = task.TaskType,
        Verdict = TaskBacklogRefinementVerdict.NeedsRefinement,
        Code = code,
    };
}
