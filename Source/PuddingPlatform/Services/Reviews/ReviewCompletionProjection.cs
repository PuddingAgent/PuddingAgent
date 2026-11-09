using System.Text.Json;
using PuddingCode.Platform;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

/// <summary>A read-model projection of the latest committed review completion on one conversation.</summary>
public sealed record ReviewOutcomeProjection(string ReviewId, string JobId, string Finding, string Level,
    long Revision, string ArtifactSetHash, string? AcceptanceContractVersion, long Sequence);

public sealed record ReviewCompletionProjectionResult(ReviewOutcomeProjection? Latest, int Scanned, int Malformed);

/// <summary>
/// PA-03/PA-04/PA-08 read side: scans canonical <c>review.completed</c> events on a conversation (bounded) and
/// returns the latest well-formed fact by sequence. Malformed events are counted and skipped — they never become a
/// review result and never advance the latest projection. Duplicate deliveries are deduplicated by EventId.
/// </summary>
public sealed class ReviewCompletionProjection(IConversationEventStore events) : IReviewCompletionReader
{
    public const string EventType = ConversationReviewCompletionSink.EventType;
    private const int MaxEvents = 2_000;

    public async Task<ReviewCompletionProjectionResult> GetLatestAsync(string conversationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ReviewException(ReviewErrorCodes.RequestInvalid, "A bounded conversation id is required.");
        var scanned = 0;
        var malformed = 0;
        ReviewOutcomeProjection? latest = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long from = 0;
        while (scanned < MaxEvents)
        {
            var page = await events.ReadForwardAsync(conversationId, from, null, 200, ct).ConfigureAwait(false);
            foreach (var evt in page.Events)
            {
                scanned++;
                if (!string.Equals(evt.Type, EventType, StringComparison.Ordinal)) continue;
                if (!seen.Add(evt.EventId)) continue;
                var projected = Project(evt);
                if (projected is null) { malformed++; continue; }
                if (latest is null || projected.Sequence > latest.Sequence) latest = projected;
            }
            if (!page.HasMore || page.Events.Count == 0) break;
            from = page.NextCursor ?? (page.Events[^1].Sequence + 1);
        }
        return new(latest, scanned, malformed);
    }

    private static ReviewOutcomeProjection? Project(ConversationEvent evt)
    {
        try
        {
            var payload = evt.Payload;
            if (payload.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) => payload.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            long? Number(string name) => payload.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
            var reviewId = Text("reviewId");
            var jobId = Text("jobId");
            var finding = Text("finding");
            var level = Text("level");
            var artifactSetHash = Text("artifactSetHash");
            var revision = Number("revision");
            if (string.IsNullOrWhiteSpace(reviewId) || string.IsNullOrWhiteSpace(jobId)
                || string.IsNullOrWhiteSpace(finding) || string.IsNullOrWhiteSpace(level)
                || revision is null) return null;
            return new(reviewId, jobId, finding, level, revision.Value, artifactSetHash ?? "", Text("acceptanceContractVersion"),
                evt.Sequence);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
