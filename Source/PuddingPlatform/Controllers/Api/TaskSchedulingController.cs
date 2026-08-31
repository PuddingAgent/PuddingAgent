using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Read-mostly scheduling diagnostics plus explicit Task dependency editing.
/// It exposes the canonical projection/evaluator; the Admin UI must not infer
/// idle or maintain a second dependency graph client-side.
/// </summary>
[ApiController]
[Route("api/workspaces/{workspaceId}/task-scheduling")]
[Authorize]
public sealed class TaskSchedulingController(
    IAgentAvailabilityProjectionStore availabilityStore,
    ITaskAutoDispatchEvaluator autoDispatchEvaluator,
    ITaskDependencyStore dependencyStore,
    TaskSchedulerControlService schedulerControl) : ControllerBase
{
    [HttpGet("auto-dispatch/status")]
    [Authorize(Roles = "admin")]
    public ActionResult<TaskSchedulerStatusSnapshot> GetAutoDispatchStatus(string workspaceId) =>
        Ok(schedulerControl.GetStatus(workspaceId));

    [HttpPut("auto-dispatch/policy")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<TaskSchedulerStatusSnapshot>> UpdateAutoDispatchPolicy(
        string workspaceId,
        [FromBody] TaskSchedulerPolicyUpdate request,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await schedulerControl.UpdatePolicyAsync(workspaceId, request, ct));
        }
        catch (TaskSchedulerControlException ex)
        {
            return SchedulerProblem(ex);
        }
    }

    [HttpPost("auto-dispatch/actions/{action}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ExecuteAutoDispatchAction(
        string workspaceId,
        string action,
        [FromBody] TaskSchedulerActionRequest? request,
        CancellationToken ct = default)
    {
        try
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "pause":
                    return Ok(await schedulerControl.SetPausedAsync(
                        workspaceId,
                        true,
                        RequireRevision(request),
                        ct));
                case "resume":
                    return Ok(await schedulerControl.SetPausedAsync(
                        workspaceId,
                        false,
                        RequireRevision(request),
                        ct));
                case "scan":
                    return Ok(new
                    {
                        summary = await schedulerControl.RunScanAsync(
                            workspaceId,
                            "admin_manual",
                            allowWhenPaused: true,
                            ct),
                        status = schedulerControl.GetStatus(workspaceId),
                    });
                case "repair":
                    return Ok(new
                    {
                        summary = await schedulerControl.RunRepairAsync(
                            workspaceId,
                            "admin_manual_repair",
                            ct),
                        status = schedulerControl.GetStatus(workspaceId),
                    });
                default:
                    return UnprocessableEntity(new
                    {
                        code = "scheduler_action_invalid",
                        message = "action 必须是 pause、resume、scan 或 repair。",
                    });
            }
        }
        catch (TaskSchedulerControlException ex)
        {
            return SchedulerProblem(ex);
        }
    }

    [HttpGet("agents/{agentId}/availability")]
    public async Task<ActionResult<AgentAvailabilitySnapshot>> GetAvailability(
        string workspaceId,
        string agentId,
        [FromQuery] bool rebuild = false,
        CancellationToken ct = default)
    {
        var snapshot = rebuild
            ? await availabilityStore.RebuildAsync(workspaceId, agentId, ct)
            : await availabilityStore.GetAsync(workspaceId, agentId, ct);
        return Ok(snapshot);
    }

    [HttpGet("auto-dispatch/evaluate")]
    public async Task<ActionResult<IReadOnlyList<TaskAutoDispatchCandidateDecision>>> EvaluateAutoDispatch(
        string workspaceId,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 500)
            return BadRequest(new { code = "invalid_limit", message = "limit 必须在 1-500 之间" });

        return Ok(await autoDispatchEvaluator.EvaluateAsync(workspaceId, limit, ct));
    }

    [HttpGet("tasks/{taskId}/dependencies")]
    public async Task<ActionResult<TaskDependenciesDto>> GetDependencies(
        string workspaceId,
        string taskId,
        CancellationToken ct = default)
    {
        try
        {
            var dependencies = await dependencyStore.ListAsync(workspaceId, taskId, ct);
            var evaluation = await dependencyStore.EvaluateAsync(workspaceId, taskId, ct);
            return Ok(new TaskDependenciesDto(dependencies, evaluation));
        }
        catch (InvalidOperationException ex) when (ex.Message == "task_dependency_task_not_found")
        {
            return NotFound(new { code = ex.Message });
        }
    }

    [HttpPost("dependencies")]
    public async Task<ActionResult<TaskDependency>> AddDependency(
        string workspaceId,
        [FromBody] AddTaskDependencyDto request,
        CancellationToken ct = default)
    {
        try
        {
            var dependency = await dependencyStore.AddAsync(
                workspaceId,
                request.PredecessorTaskId,
                request.SuccessorTaskId,
                ct);
            return Ok(dependency);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { code = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "task_dependency_invalid_argument", message = ex.Message });
        }
    }

    [HttpDelete("dependencies/{dependencyId}")]
    public async Task<IActionResult> RemoveDependency(
        string workspaceId,
        string dependencyId,
        CancellationToken ct = default) =>
        await dependencyStore.RemoveAsync(workspaceId, dependencyId, ct)
            ? NoContent()
            : NotFound(new { code = "task_dependency_not_found" });

    private static int RequireRevision(TaskSchedulerActionRequest? request) =>
        request?.ExpectedRevision
        ?? throw new TaskSchedulerControlException(
            "scheduler_policy_revision_required",
            "pause/resume 必须携带 expectedRevision。");

    private ObjectResult SchedulerProblem(TaskSchedulerControlException ex)
    {
        var status = ex.Code is "scheduler_policy_conflict" or "scheduler_scan_in_progress"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status422UnprocessableEntity;
        return StatusCode(status, new
        {
            code = ex.Code,
            message = ex.Message,
            traceId = HttpContext.TraceIdentifier,
        });
    }
}

public sealed record AddTaskDependencyDto(
    string PredecessorTaskId,
    string SuccessorTaskId);

public sealed record TaskDependenciesDto(
    IReadOnlyList<TaskDependency> Items,
    TaskDependencyEvaluation Evaluation);

public sealed record TaskSchedulerActionRequest(int? ExpectedRevision);
