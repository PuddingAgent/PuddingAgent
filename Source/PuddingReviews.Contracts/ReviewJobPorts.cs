using System.Collections.Immutable;

namespace PuddingReviews.Contracts;

// PA-03 持久验收任务端口（BCL-only）。Job 状态属于评审任务自身，不映射成 GoalPhase。
// 结论是建议；模型返回的 Accept 不得越过证据/版本校验，也不得写 Goal 终态。

public enum ReviewJobState { Pending, Running, Waiting, Completed, Failed, Unknown, Cancelled }

public enum ReviewFindingValue { Satisfied, NeedsWork, NeedsUser, Inconclusive }

public enum ReviewEvidenceLevel { ModelAssessed, Observed, CheckVerified }

public enum CriterionOutcomeValue { Unknown, Supported, NotSupported }

public enum EvidenceKind { CanonicalOutput, CheckReport, RegisteredArtifact, CallerSupplied }

/// <summary>The logical unique key. Version changes create a new review; an old result never settles a newer version.</summary>
public sealed record ReviewLogicalKey(string WorkspaceId, string? ParentWorkId, string? GoalRunId, long ActivationEpoch,
    int IterationNo, string ObjectiveVersion, string AcceptanceContractVersion, string ArtifactSetHash,
    string ReviewPolicyVersion)
{
    public bool IsValid => Valid(WorkspaceId) && (Valid(ParentWorkId) || Valid(GoalRunId)) && ActivationEpoch >= 0
        && IterationNo >= 0 && Valid(ObjectiveVersion) && Valid(AcceptanceContractVersion) && Valid(ArtifactSetHash)
        && Valid(ReviewPolicyVersion);

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record ReviewJobRecord(string JobId, string ReviewId, ReviewLogicalKey Key, bool Required,
    string BudgetAccountId, DateTimeOffset Deadline, DateTimeOffset AcceptedAt, long AcceptedSequence,
    string? ActorId = null)
{
    public bool IsValid => Valid(JobId) && Valid(ReviewId) && Key is { IsValid: true } && Valid(BudgetAccountId)
        && Deadline > AcceptedAt && AcceptedSequence >= 0;

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record ReviewEvidenceItem(string EvidenceId, EvidenceKind Kind, string Source, string Reference,
    string ContentHash, DateTimeOffset CollectedAt, string Producer, bool Truncated, string? CheckExitCode,
    string? ContentExcerpt = null, bool ContentTruncated = false, string? ContentUnavailableReason = null)
{
    /// <summary>Caller-supplied strings and arbitrary paths can never be upgraded to a verified evidence level.</summary>
    public bool CallerSupplied => Kind == EvidenceKind.CallerSupplied;
}

public sealed record ReviewCriterion(string CriterionId, string Text);

public sealed record ReviewInputSnapshot(string ReviewId, ReviewLogicalKey Key, string Objective,
    ImmutableArray<ReviewCriterion> Criteria, ImmutableArray<ReviewEvidenceItem> Evidence, bool EvidenceComplete,
    ImmutableArray<string> MissingReasons, string PromptVersion, string ModelPolicyVersion, string BudgetAccountId,
    DateTimeOffset Deadline, string? ActorId = null)
{
    public bool IsValid => Valid(ReviewId) && Key is { IsValid: true } && !string.IsNullOrWhiteSpace(Objective)
        && !Criteria.IsDefault && !Evidence.IsDefault && !MissingReasons.IsDefault && Valid(PromptVersion)
        && Valid(ModelPolicyVersion) && Valid(BudgetAccountId);

    public ReviewEvidenceItem? FindEvidence(string evidenceId) =>
        Evidence.FirstOrDefault(e => string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record ReviewCriterionOutcome(string CriterionId, CriterionOutcomeValue Outcome,
    ImmutableArray<string> EvidenceIds);

public sealed record ReviewModelResult(ReviewFindingValue Finding, ImmutableArray<ReviewCriterionOutcome> Criteria,
    double? Confidence, string Reason, string? ReasonCode, string ObjectiveVersion, string AcceptanceContractVersion,
    string ArtifactSetHash, string PromptVersion, string ModelId, string ModelRevision, string ResponseFingerprint);

public sealed record ReviewOutcome(ReviewFindingValue Finding, ReviewEvidenceLevel Level,
    ImmutableArray<ReviewCriterionOutcome> Criteria, string? ReasonCode);

public sealed record ReviewJobView(string JobId, string ReviewId, ReviewLogicalKey Key, bool Required,
    string BudgetAccountId, DateTimeOffset Deadline, ReviewJobState State, long Revision, long OwnerEpoch,
    long AcceptedSequence, ReviewOutcome? Outcome, string? ReasonCode, DateTimeOffset UpdatedAt,
    string? ActorId = null);

/// <summary>Idempotent submit by logical key; an OperationId reused with different content must conflict.</summary>
public sealed record ReviewCreateResult(ReviewJobView Job, bool Created);

public interface IReviewJobStore
{
    ValueTask<ReviewCreateResult> CreateOrGetAsync(ReviewJobRecord record, string operationId, CancellationToken ct = default);
    ValueTask<ReviewJobView?> GetAsync(string jobId, CancellationToken ct = default);
    ValueTask<ReviewJobView?> GetByReviewAsync(string reviewId, CancellationToken ct = default);
    /// <summary>Owner-epoch lease claim with expected revision; an old worker's claim must be rejected.</summary>
    ValueTask<ReviewJobView> ClaimAsync(string jobId, long ownerEpoch, DateTimeOffset leaseUntil, long expectedRevision,
        CancellationToken ct = default);
    /// <summary>
    /// The only completion entry: the terminal job state and its immutable <c>review.completed</c> fact commit in the
    /// same store transaction. Replaying the same completion returns the same fact and never creates a second event.
    /// </summary>
    ValueTask<ReviewJobView> CompleteAsync(string jobId, long ownerEpoch, long expectedRevision, ReviewOutcome outcome,
        ReviewCompletedEvent completed, CancellationToken ct = default);
    ValueTask<ReviewJobView> FailAsync(string jobId, long ownerEpoch, long expectedRevision, ReviewJobState state,
        string reasonCode, CancellationToken ct = default);
    ValueTask<ReviewJobView> CancelAsync(string jobId, long expectedRevision, string reasonCode,
        CancellationToken ct = default);
}

public sealed record ReviewEvidenceReadResult(ReviewInputSnapshot? Snapshot, string? ReasonCode);

/// <summary>Reads only committed canonical outputs, controlled check reports and registered artifacts; never a transcript or arbitrary path.</summary>
public interface IReviewEvidenceReader
{
    ValueTask<ReviewEvidenceReadResult> ReadAsync(ReviewJobView job, CancellationToken ct = default);
}

/// <summary>Bound model review execution. The host supplies the Job-owned budget scope; this port never receives host services.</summary>
public interface IReviewExecutor
{
    ValueTask<ReviewModelResult> ReviewAsync(ReviewInputSnapshot snapshot, string attemptId, CancellationToken ct = default);
}

public sealed record ReviewCompletedEvent(string EventId, string ReviewId, string JobId, ReviewLogicalKey Key,
    ReviewFindingValue Finding, ReviewEvidenceLevel Level, long Revision, DateTimeOffset OccurredAt);

/// <summary>Completed review facts are published through the durable outbox (07A at S5); delivery is at least once.</summary>
public interface IReviewEventOutbox
{
    ValueTask PublishCompletedAsync(ReviewCompletedEvent completed, CancellationToken ct = default);
}

public static class ReviewErrorCodes
{
    public const string RequestInvalid = "review.request_invalid";
    public const string Conflict = "review.conflict";
    public const string NotFound = "review.not_found";
    public const string RevisionConflict = "review.revision_conflict";
    public const string OwnerStale = "review.owner_stale";
    public const string ActorUnavailable = "review.actor_unavailable";
    public const string StateInvalid = "review.state_invalid";
    public const string SnapshotRequired = "review.snapshot_required";
    public const string EvidenceUnavailable = "review.evidence_unavailable";
    public const string CriteriaMissing = "review.criteria_missing";
    public const string EvidenceIncomplete = "review.evidence_incomplete";
    public const string VersionMismatch = "review.version_mismatch";
    public const string UnknownCriterion = "review.unknown_criterion";
    public const string UnknownEvidence = "review.unknown_evidence";
    public const string ExecutorFailed = "review.executor_failed";
    public const string ExecutorUnknown = "review.executor_unknown";
    public const string Cancelled = "review.cancelled";
}

public sealed class ReviewException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
