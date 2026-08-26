namespace PuddingCode.Goals;

public enum GoalVerificationVerdict
{
    Continue = 0,
    Complete = 1,
    Blocked = 2,
    NeedsUser = 3,
    Unsafe = 4,
}

/// <summary>Verifier 的有界、结构化、只读输入；不携带无限 transcript。</summary>
public sealed record GoalEvidenceCapsule
{
    public required string GoalRunId { get; init; }
    public required int ActivationEpoch { get; init; }
    public required int AggregateVersion { get; init; }
    public required int IterationNo { get; init; }
    public required string Objective { get; init; }
    public required int ObjectiveVersion { get; init; }
    public required int RemainingIterations { get; init; }
    public required string TurnId { get; init; }
    public required string TerminalKind { get; init; }
    public required long TerminalSequence { get; init; }
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
    public string? TaskId { get; init; }
    public string? TaskStatus { get; init; }
    public string? TaskAcceptanceCriteria { get; init; }
    public bool HasPendingExecutionFacts { get; init; }
    public bool EvidenceComplete { get; init; }
}

public sealed record GoalVerificationDecision
{
    public required GoalVerificationVerdict Verdict { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
    public IReadOnlyList<string> UnmetCriteria { get; init; } = [];
    public string? NextAction { get; init; }
    public string? BlockerCode { get; init; }
    public string? BlockerMessage { get; init; }
    public string? ProgressFingerprint { get; init; }
}

/// <summary>
/// 只读 Goal verifier。实现不得写 Goal/Task、执行工具或扩大权限；Coordinator
/// 独占终态提交。默认实现只接受 Task canonical terminal fact，绝不相信自然语言 DONE。
/// </summary>
public interface IGoalIterationVerifier
{
    Task<GoalVerificationDecision> VerifyAsync(
        GoalEvidenceCapsule capsule,
        CancellationToken ct = default);
}
