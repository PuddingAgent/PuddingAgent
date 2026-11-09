using Microsoft.EntityFrameworkCore;
using Pudding.Api.Contracts;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingReviews.Contracts;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatform.Services.Reviews;

public interface IReviewCompletionReader
{
    Task<ReviewCompletionProjectionResult> GetLatestAsync(string conversationId, CancellationToken ct = default);
}

/// <summary>Host-owned goal → current conversation mapping. A missing map is null, never a guessed conversation.</summary>
public interface IReviewGoalConversationResolver
{
    Task<string?> ResolveAsync(string goalRunId, CancellationToken ct = default);
}

/// <summary>Latest review job for a Goal run; used by the read-only panel endpoint and PA-04 linkage.</summary>
public interface IReviewJobByGoalReader
{
    Task<ReviewJobView?> GetLatestByGoalAsync(string goalRunId, CancellationToken ct = default);
}

public sealed class ReviewGoalConversationResolver(IDbContextFactory<PlatformDbContext> dbFactory)
    : IReviewGoalConversationResolver
{
    public async Task<string?> ResolveAsync(string goalRunId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.GoalRuns.AsNoTracking().Where(g => g.GoalRunId == goalRunId)
            .Select(g => g.CurrentConversationId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }
}

public sealed record ExecutionReviewQueryResult(ExecutionReviewSnapshot? Snapshot, string? MissingReason);

/// <summary>
/// PA-08 A query service: resolves one review job by review id (or the latest job by Goal run), its durable budget
/// account and the latest committed completion projection, then maps them through the execution-review assembler.
/// Missing job/budget returns an explicit missing reason — never a fabricated empty snapshot.
/// </summary>
public sealed class ExecutionReviewQueryService(IReviewJobStore jobs, IExecutionBudgetApi budgets,
    IReviewCompletionReader projection, IReviewGoalConversationResolver conversations,
    IReviewJobByGoalReader? byGoal = null, TimeProvider? clock = null)
{
    public async Task<ExecutionReviewQueryResult> GetAsync(string reviewId, bool stale = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reviewId) || reviewId.Length > 128)
            return new(null, ReviewErrorCodes.RequestInvalid);
        var job = await jobs.GetByReviewAsync(reviewId, ct).ConfigureAwait(false);
        return job is null ? new(null, ReviewErrorCodes.NotFound) : await BuildAsync(job, stale, ct).ConfigureAwait(false);
    }

    public async Task<ExecutionReviewQueryResult> GetLatestForGoalAsync(string goalId, bool stale = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goalId) || goalId.Length > 128)
            return new(null, ReviewErrorCodes.RequestInvalid);
        if (byGoal is null) return new(null, ReviewErrorCodes.NotFound);
        var job = await byGoal.GetLatestByGoalAsync(goalId, ct).ConfigureAwait(false);
        return job is null ? new(null, ReviewErrorCodes.NotFound) : await BuildAsync(job, stale, ct).ConfigureAwait(false);
    }

    private async Task<ExecutionReviewQueryResult> BuildAsync(ReviewJobView job, bool stale, CancellationToken ct)
    {
        var budget = await budgets.GetAsync(job.BudgetAccountId, ct).ConfigureAwait(false);
        if (budget is null) return new(null, BudgetErrorCodes.AccountMissing);
        var goalRunId = job.Key.GoalRunId;
        var conversationId = goalRunId is null ? null : await conversations.ResolveAsync(goalRunId, ct)
            .ConfigureAwait(false);
        var latest = conversationId is null
            ? null
            : (await projection.GetLatestAsync(conversationId, ct).ConfigureAwait(false)).Latest;
        var snapshot = ExecutionReviewAssembler.Assemble(new ExecutionReviewAssemblyInput(
            WorkId: job.Key.ParentWorkId ?? goalRunId ?? "", GoalId: goalRunId ?? "", TurnId: job.JobId,
            budget, job, latest, stale, GapDetected: latest is null && stale,
            DataAsOf: (clock ?? TimeProvider.System).GetUtcNow().ToString("O"), ModelId: "", ModelRevision: "",
            PolicyVersion: job.Key.ReviewPolicyVersion, PromptVersion: "", Evidence: [], FailedChecks: []));
        return new(snapshot, null);
    }
}
