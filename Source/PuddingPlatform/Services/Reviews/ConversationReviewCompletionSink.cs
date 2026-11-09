using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5 product sink: maps a committed review-completed fact to a canonical conversation event
/// (<c>review.completed</c>) on the Goal's current conversation. The fact keeps its stable EventId, so consumers may
/// deduplicate an at-least-once redelivery. A review whose Goal/conversation cannot be resolved is a typed failure
/// (the outbox keeps the row unpublished and retries); it is never silently dropped.
/// </summary>
public sealed class ConversationReviewCompletionSink(
    IConversationEventStore events,
    IDbContextFactory<PlatformDbContext> dbFactory) : IReviewCompletionSink
{
    public const string EventType = "review.completed";
    public const string ProducerComponent = "reviews";

    public async ValueTask PublishAsync(ReviewCompletedEvent completed, CancellationToken ct = default)
    {
        if (completed.Key.GoalRunId is not { } goalRunId)
            throw new ReviewException(ReviewErrorCodes.ExecutorFailed,
                "The completed review has no Goal run to publish to.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var conversationId = await db.GoalRuns.AsNoTracking()
            .Where(g => g.GoalRunId == goalRunId).Select(g => g.CurrentConversationId).SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ReviewException(ReviewErrorCodes.ExecutorFailed,
                "The completed review's Goal has no current conversation.");

        var payload = JsonSerializer.SerializeToElement(new
        {
            reviewId = completed.ReviewId,
            jobId = completed.JobId,
            finding = completed.Finding.ToString(),
            level = completed.Level.ToString(),
            revision = completed.Revision,
            occurredAt = completed.OccurredAt,
            goalRunId,
            workspaceId = completed.Key.WorkspaceId,
            parentWorkId = completed.Key.ParentWorkId,
            activationEpoch = completed.Key.ActivationEpoch,
            iterationNo = completed.Key.IterationNo,
            objectiveVersion = completed.Key.ObjectiveVersion,
            acceptanceContractVersion = completed.Key.AcceptanceContractVersion,
            artifactSetHash = completed.Key.ArtifactSetHash,
            reviewPolicyVersion = completed.Key.ReviewPolicyVersion,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var evt = new NewConversationEvent(completed.EventId, EventType, 1, completed.Key.WorkspaceId, null, null,
            null, null, completed.Key.GoalRunId, null, completed.EventId, payload,
            SourceKind: ConversationEventSourceKind.Goal, ProducerComponent: ProducerComponent);
        await events.AppendAsync(conversationId, -1, [evt], EventWriteCondition.ForRun($"review:{completed.JobId}", 0), ct)
            .ConfigureAwait(false);
    }
}
