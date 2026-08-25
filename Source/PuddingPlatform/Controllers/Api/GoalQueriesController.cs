using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Goals;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// ADR-074 §10: Goal 只读查询 API。任意入口（Web/Desktop/Connector）共用此投影。
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1")]
public sealed class GoalQueriesController(IGoalQueryService goalQueryService) : ControllerBase
{
    [HttpGet("conversations/{conversationId}/goal")]
    public async Task<IActionResult> GetConversationGoal(
        [FromRoute] string conversationId,
        [FromHeader(Name = "X-Workspace-Id")] string workspaceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return ValidationProblem("Missing X-Workspace-Id header.");

        // 优先返回活动 Goal；无活动 Goal 时返回最近一个（含终态），供 Banner 显示回执。
        var snapshot = await goalQueryService.GetActiveAsync(workspaceId, conversationId, ct)
            ?? await goalQueryService.GetLatestAsync(workspaceId, conversationId, ct);

        return Ok(new { goal = GoalCommandsController.ToDto(snapshot) });
    }

    [HttpGet("goals/{goalId}")]
    public async Task<IActionResult> GetGoal(
        [FromRoute] string goalId,
        CancellationToken ct)
    {
        var snapshot = await goalQueryService.GetAsync(goalId, ct);
        return snapshot is null
            ? Problem(statusCode: 404, title: "goal_not_found", detail: $"Goal '{goalId}' does not exist.")
            : Ok(new { goal = GoalCommandsController.ToDto(snapshot) });
    }

    [HttpGet("goals/{goalId}/iterations")]
    public async Task<IActionResult> GetGoalIterations(
        [FromRoute] string goalId,
        CancellationToken ct)
    {
        var snapshot = await goalQueryService.GetAsync(goalId, ct);
        if (snapshot is null)
            return Problem(statusCode: 404, title: "goal_not_found", detail: $"Goal '{goalId}' does not exist.");

        // G1：iteration 明细恒为空；G2 durable outbox 续行起产生真实条目。
        var iterations = await goalQueryService.GetIterationsAsync(goalId, ct);
        return Ok(new
        {
            goalRunId = goalId,
            iterations = iterations.Select(i => new
            {
                i.IterationNo,
                i.ActivationEpoch,
                status = i.Status,
                commandId = i.CommandId,
                turnId = i.TurnId,
                startedAtUtc = i.StartedAtUtc,
                settledAtUtc = i.SettledAtUtc,
            }),
        });
    }
}
