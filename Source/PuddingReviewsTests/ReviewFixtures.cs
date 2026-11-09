using System.Collections.Immutable;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingReviewsTests;

internal static class ReviewFixtures
{
    public const string ObjectiveVersion = "objective-v1";
    public const string AcceptanceVersion = "acceptance-v1";
    public const string ArtifactHash = "artifact-hash-1";
    public const string PolicyVersion = "policy-v1";
    public const string PromptVersion = "prompt-v1";

    public static ReviewLogicalKey Key(string artifactHash = ArtifactHash, string objectiveVersion = ObjectiveVersion,
        string acceptanceVersion = AcceptanceVersion)
        => new("ws-1", "work-1", null, ActivationEpoch: 3, IterationNo: 2, objectiveVersion, acceptanceVersion,
            artifactHash, PolicyVersion);

    public static ReviewJobRecord Record(ReviewLogicalKey? key = null, string reviewId = "review-1",
        string jobId = "job-1", bool required = true)
        => new(jobId, reviewId, key ?? Key(), required, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10),
            DateTimeOffset.UtcNow, AcceptedSequence: 0);

    public static ReviewCriterion[] DefaultCriteria() =>
        [new("criterion-1", "必须包含概述。"), new("criterion-2", "必须列出文件。")];

    public static ReviewEvidenceItem[] DefaultEvidence() =>
    [
        new("evidence-1", EvidenceKind.CanonicalOutput, "canonical-turn", "turn://1", "hash-evidence-1",
            DateTimeOffset.UtcNow, "agent", Truncated: false, CheckExitCode: null),
        new("evidence-2", EvidenceKind.CheckReport, "controlled-check", "check://build", "hash-evidence-2",
            DateTimeOffset.UtcNow, "check-runner", Truncated: false, CheckExitCode: "0"),
    ];

    public static ReviewInputSnapshot Snapshot(ReviewJobView job, bool evidenceComplete = true,
        ImmutableArray<ReviewCriterion>? criteria = null, ImmutableArray<ReviewEvidenceItem>? evidence = null,
        ImmutableArray<string>? missingReasons = null, string promptVersion = PromptVersion)
        => new(job.ReviewId, job.Key, "交付物已生成。", criteria ?? [.. DefaultCriteria()],
            evidence ?? [.. DefaultEvidence()], evidenceComplete, missingReasons ?? [],
            promptVersion, "model-policy-v1", job.BudgetAccountId, job.Deadline);

    public static ReviewCriterionOutcome[] AllSupported() =>
    [
        new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-1"]),
        new("criterion-2", CriterionOutcomeValue.Supported, ["evidence-2"]),
    ];

    public static ReviewModelResult Result(ReviewInputSnapshot snapshot, ReviewFindingValue finding = ReviewFindingValue.Satisfied,
        ImmutableArray<ReviewCriterionOutcome>? criteria = null, string? objectiveVersion = null,
        string? acceptanceVersion = null, string? artifactHash = null, string? promptVersion = null)
        => new(finding, criteria ?? [.. AllSupported()], 0.9, "模型给出验收建议。", null,
            objectiveVersion ?? snapshot.Key.ObjectiveVersion,
            acceptanceVersion ?? snapshot.Key.AcceptanceContractVersion,
            artifactHash ?? snapshot.Key.ArtifactSetHash,
            promptVersion ?? snapshot.PromptVersion, "model-1", "revision-1", "response-sha256");

    public static (ReviewTestHost Host, ReviewJobCoordinator Coordinator) New()
    {
        var host = new ReviewTestHost();
        return (host, new ReviewJobCoordinator(host, host, host));
    }

    public static async Task<ReviewJobView> SubmitAsync(ReviewTestHost host, ReviewJobCoordinator coordinator,
        string operationId = "op-1", ReviewJobRecord? record = null)
    {
        var result = await coordinator.SubmitAsync(record ?? Record(), operationId);
        return result.Job;
    }

    public static ReviewJobView Completed(ReviewJobView job, ReviewOutcome outcome)
        => job with { State = ReviewJobState.Completed, Outcome = outcome };

    /// <summary>Assert helper for ValueTask-returning coordinator methods.</summary>
    public static async Task<ReviewException> FailsAsync<T>(Func<ValueTask<T>> action)
        => await Assert.ThrowsExactlyAsync<ReviewException>(async () => await action());
}
