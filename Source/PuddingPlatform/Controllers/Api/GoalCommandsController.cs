using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Goals;
using PuddingCode.Platform;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// ADR-074 §10: Goal 结构化 Control Plane API。
/// 与 /goal slash 文本共用同一 IGoalCommandService、同一幂等语义，产生相同事件。
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/conversations")]
public sealed class GoalCommandsController(IGoalCommandService goalCommandService) : ControllerBase
{
    [HttpPost("{conversationId}/goals/commands")]
    public async Task<IActionResult> ExecuteGoalCommand(
        [FromRoute] string conversationId,
        [FromBody] GoalCommandHttpRequest request,
        [FromHeader(Name = "X-Workspace-Id")] string workspaceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return ValidationProblem("Missing X-Workspace-Id header.");

        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return Problem(statusCode: 401, title: "unauthorized", detail: "User id claim is required.");

        if (!TryParseAction(request.Action, out var kind))
        {
            return Problem(
                statusCode: 422,
                title: "invalid_goal_action",
                detail: "Action must be one of: status, set, edit, replace, pause, resume, cancel, clear.");
        }

        if (kind is GoalCommandKind.Set or GoalCommandKind.Edit or GoalCommandKind.Replace)
        {
            if (string.IsNullOrWhiteSpace(request.Objective))
                return Problem(statusCode: 422, title: "invalid_objective", detail: "Objective is required.");

            var trimmed = request.Objective.Trim();
            if (trimmed.Length is < 1 or > GoalLimits.ObjectiveMaxLength)
                return Problem(
                    statusCode: 422,
                    title: "invalid_objective",
                    detail: $"Objective must be 1-{GoalLimits.ObjectiveMaxLength} characters.");
        }

        if (request.Rounds is not null && !GoalLimits.IsValidIterationBudget(request.Rounds.Value))
        {
            return Problem(
                statusCode: 422,
                title: "invalid_rounds",
                detail: $"Rounds must be between {GoalLimits.MinIterations} and {GoalLimits.MaxIterationsHardLimit}.");
        }

        var goalRequest = new GoalCommandRequest(
            workspaceId,
            conversationId,
            request.AgentId,
            userId,
            request.ClientRequestId,
            new GoalCommand
            {
                Kind = kind,
                Objective = request.Objective?.Trim(),
                Rounds = request.Rounds,
                Reason = request.Reason,
            },
            SourceChannel: "web",
            ExpectedVersion: request.ExpectedVersion);

        var result = await goalCommandService.ExecuteAsync(goalRequest, ct);

        return Ok(new GoalCommandHttpResponse(
            result.Success,
            result.ErrorCode,
            result.Message,
            ToDto(result.Snapshot)));
    }

    private static bool TryParseAction(string? action, out GoalCommandKind kind)
    {
        kind = action?.Trim().ToLowerInvariant() switch
        {
            "status" => GoalCommandKind.Status,
            "set" => GoalCommandKind.Set,
            "edit" => GoalCommandKind.Edit,
            "replace" => GoalCommandKind.Replace,
            "pause" => GoalCommandKind.Pause,
            "resume" => GoalCommandKind.Resume,
            "cancel" => GoalCommandKind.Cancel,
            "clear" => GoalCommandKind.Clear,
            _ => GoalCommandKind.Status,
        };
        return !string.IsNullOrWhiteSpace(action);
    }

    internal static GoalSnapshotDto? ToDto(GoalSnapshot? snapshot)
        => snapshot is null ? null : new GoalSnapshotDto(
            snapshot.GoalRunId,
            snapshot.ConversationId,
            snapshot.AgentInstanceId,
            snapshot.Objective,
            snapshot.ObjectiveVersion,
            snapshot.Phase.ToString().ToLowerInvariant(),
            snapshot.BlockedCode,
            snapshot.StatusReason,
            snapshot.MaxIterations,
            snapshot.IterationsStarted,
            snapshot.IterationsSettled,
            snapshot.ActivationEpoch,
            snapshot.AggregateVersion,
            snapshot.LastNextAction,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.TerminalAtUtc);

    public sealed record GoalCommandHttpRequest(
        string AgentId,
        string ClientRequestId,
        string? Action,
        string? Objective,
        int? Rounds,
        string? Reason,
        int? ExpectedVersion = null);

    public sealed record GoalCommandHttpResponse(
        bool Success,
        string? ErrorCode,
        string Message,
        GoalSnapshotDto? Goal);

    public sealed record GoalSnapshotDto(
        string GoalRunId,
        string ConversationId,
        string AgentInstanceId,
        string Objective,
        int ObjectiveVersion,
        string Phase,
        string? BlockedCode,
        string? StatusReason,
        int MaxIterations,
        int IterationsStarted,
        int IterationsSettled,
        int ActivationEpoch,
        int AggregateVersion,
        string? LastNextAction,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? TerminalAtUtc);
}
