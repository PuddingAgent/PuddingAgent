using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;

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
    ITaskDependencyStore dependencyStore) : ControllerBase
{
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
}

public sealed record AddTaskDependencyDto(
    string PredecessorTaskId,
    string SuccessorTaskId);

public sealed record TaskDependenciesDto(
    IReadOnlyList<TaskDependency> Items,
    TaskDependencyEvaluation Evaluation);
