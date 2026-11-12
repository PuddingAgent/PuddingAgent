using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// PA-08 A authorized read-only endpoint: one execution-review snapshot for the given review id. The snapshot is a
/// projection of authoritative budget/review state (unknown amounts stay unknown, stale data is flagged); the
/// endpoint never exposes control mutations or a fabricated empty view.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1")]
public sealed class ExecutionReviewController(ExecutionReviewQueryService queries) : ControllerBase
{
    [HttpGet("goals/{goalId}/execution-review")]
    public async Task<IActionResult> GetLatestForGoal(
        [FromRoute] string goalId,
        [FromQuery] bool stale,
        CancellationToken ct)
    {
        var result = await queries.GetLatestForGoalAsync(goalId, stale, ct);
        if (result.Snapshot is not null) return Ok(new { review = result.Snapshot });
        return result.MissingReason switch
        {
            ReviewErrorCodes.RequestInvalid => ValidationProblem("A bounded goal id is required."),
            _ => Problem(statusCode: 404, title: "execution_review_not_found",
                detail: $"No execution review exists for goal '{goalId}'."),
        };
    }
    [HttpGet("execution-reviews/{reviewId}")]
    public async Task<IActionResult> Get(
        [FromRoute] string reviewId,
        [FromQuery] bool stale,
        CancellationToken ct)
    {
        var result = await queries.GetAsync(reviewId, stale, ct);
        if (result.Snapshot is not null) return Ok(new { review = result.Snapshot });
        return result.MissingReason switch
        {
            ReviewErrorCodes.RequestInvalid => ValidationProblem("A bounded review id is required."),
            ReviewErrorCodes.NotFound => Problem(statusCode: 404, title: "review_not_found",
                detail: $"Review '{reviewId}' does not exist."),
            _ => Problem(statusCode: 503, title: "review_unavailable",
                detail: "The review snapshot is temporarily unavailable."),
        };
    }
}
