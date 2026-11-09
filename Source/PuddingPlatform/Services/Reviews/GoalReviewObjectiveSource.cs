using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Goals;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5 objective source: the frozen objective text comes from the goal run record and the criteria come from
/// the versioned acceptance contract — never from chat history. A missing contract, malformed/absent version or
/// empty criteria means "not ready" (null), which the evidence reader turns into SnapshotRequired.
/// </summary>
public sealed class GoalReviewObjectiveSource(GoalAcceptanceContractStore contracts,
    IDbContextFactory<PlatformDbContext> dbFactory) : IReviewObjectiveSource
{
    public async ValueTask<(string Objective, ImmutableArray<ReviewCriterion> Criteria)?> LoadAsync(
        ReviewLogicalKey key, CancellationToken ct = default)
    {
        if (key.GoalRunId is not { } goalRunId || !int.TryParse(key.ObjectiveVersion, out var objectiveVersion))
            return null;
        var contract = await contracts.LoadAsync(goalRunId, (int)key.ActivationEpoch, objectiveVersion, ct)
            .ConfigureAwait(false);
        if (contract is null) return null;
        List<GoalCriterion>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<GoalCriterion>>(contract.CriteriaJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException) { return null; }
        if (parsed is null || parsed.Count == 0 || parsed.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id)
                || string.IsNullOrWhiteSpace(c.Requirement)))
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var objective = await db.GoalRuns.AsNoTracking()
            .Where(g => g.GoalRunId == goalRunId).Select(g => g.Objective).SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(objective)) return null;
        return (objective, [.. parsed.Select(c => new ReviewCriterion(c.Id, c.Requirement))]);
    }
}
