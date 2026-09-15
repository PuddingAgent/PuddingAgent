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

    /// <summary>ADR-092 §4/§5（G92-1）：本次裁决依据的验收条件快照（空集合表示尚无验收合同）。</summary>
    public IReadOnlyList<GoalCriterion> Criteria { get; init; } = [];

    /// <summary>ADR-092 §5.3（G92-1）：受控检查的实际报告；verifier 只读它，不执行工具。</summary>
    public IReadOnlyList<GoalCheckReport> CheckReports { get; init; } = [];

    /// <summary>ADR-092 §5.3（G92-1）：本次裁决依据的版本化检查定义；未声明的报告不得计入通过。</summary>
    public IReadOnlyList<GoalCheckSpec> Checks { get; init; } = [];
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

    /// <summary>
    /// ADR-092 §4（G92-0）：本单元/目标的验收条件快照。空集合表示“尚无验收合同”，
    /// 不得因此判定完成（禁止 vacuous pass）。
    /// </summary>
    public IReadOnlyList<GoalCriterion> Criteria { get; init; } = [];

    /// <summary>ADR-092 §4（G92-0）：逐项实际检查结果。空集合表示尚未有已验证验收。</summary>
    public IReadOnlyList<GoalCriterionResult> CriterionResults { get; init; } = [];
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

/// <summary>ADR-092 §4：验收条件（最小合同，G92-0 引入；实际执行由 G92-1 的 CheckRunner 负责）。</summary>
public sealed record GoalCriterion
{
    public required string Id { get; init; }

    /// <summary>条件版本；条件内容变化时必须递增，旧结果随之失效。</summary>
    public int Revision { get; init; } = 1;

    public required string Requirement { get; init; }

    /// <summary>是否必需。只有必需条件全部通过才能推进/完成。</summary>
    public bool Required { get; init; } = true;

    /// <summary>检查类型，取值见 <see cref="GoalVerificationSpecKinds"/>。</summary>
    public string Kind { get; init; } = GoalVerificationSpecKinds.Semantic;

    /// <summary>版本化检查定义引用（不是自由 shell 字符串）。</summary>
    public string? DefinitionRef { get; init; }

    public string? DefinitionHash { get; init; }

    public IReadOnlyList<string> InputRefs { get; init; } = [];

    /// <summary>执行者角色：core / external_controller / human。</summary>
    public string ExecutorRole { get; init; } = "core";

    public string FreshnessPolicy { get; init; } = "input-fingerprint";

    public IReadOnlyList<string> DependencyIds { get; init; } = [];
}

/// <summary>ADR-092 §4：检查类型（显式字符串，避免与既有 wire 值冲突）。</summary>
public static class GoalVerificationSpecKinds
{
    public const string Build = "build";
    public const string Test = "test";
    public const string Postcondition = "postcondition";
    public const string Artifact = "artifact";
    public const string Semantic = "semantic";
    public const string External = "external";
}

/// <summary>ADR-092 §4：逐项条件结果状态。</summary>
public static class GoalCriterionResultStatuses
{
    public const string Pending = "pending";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Waiting = "waiting";
    public const string Invalidated = "invalidated";
}

public sealed record GoalCriterionResult
{
    public required string CriterionId { get; init; }
    public int CriterionRevision { get; init; } = 1;

    /// <summary>取值见 <see cref="GoalCriterionResultStatuses"/>。</summary>
    public required string Status { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    /// <summary>实际检查输入的指纹；变了就必须重跑，不复用旧绿灯。</summary>
    public string? InputFingerprint { get; init; }

    public string? FailureCode { get; init; }
}

/// <summary>ADR-092 §4/§6.1：结算处置取值。</summary>
public static class GoalSettlementDispositions
{
    public const string ContinueCurrent = "continue_current";
    public const string Repair = "repair";
    public const string Advance = "advance";
    public const string VerifyGoal = "verify_goal";
    public const string Complete = "complete";
    public const string Wait = "wait";
    public const string NeedsUser = "needs_user";
    public const string Stop = "stop";
}

/// <summary>
/// ADR-092 §6.1：纯结算决策——只根据条件/结果/verdict 计算处置与是否已验证验收，
/// 不读写任何状态、不执行工具。Store 与 Verifier 共用它，避免“Turn 结束即完成”的分支散落。
/// </summary>
public static class GoalSettlementDecisionCalculator
{
    /// <summary>依赖等待族：可恢复的等待，既不是失败，也不是人工决定。</summary>
    private static readonly string[] WaitBlockerCodes =
    [
        "dependency_wait",
        "evidence_pending",
        "approval_review_profile_not_configured",
        "approval_review_service_unavailable",
        "approval_review_timeout",
        "approval_review_call_failed",
        "check_results_pending",
        "acceptance_contract_missing",
    ];

    /// <summary>只有不可恢复的阻塞码允许终止 Goal。</summary>
    private static readonly string[] UnrecoverableBlockerCodes =
    [
        "task_plan_state_invalid",
        "unsafe",
        "cancelled",
    ];

    /// <summary>
    /// 全部必需条件都有一条同版本、状态为 passed 的结果才为真。
    /// 空合同一律返回 false：采集完整、Turn 结束、Task Completed 都不是验收证据。
    /// </summary>
    public static bool AllRequiredCriteriaPassed(GoalVerificationDecision decision)
    {
        var required = decision.Criteria.Where(criterion => criterion.Required).ToList();
        if (required.Count == 0)
            return false;

        foreach (var criterion in required)
        {
            var result = decision.CriterionResults.FirstOrDefault(
                candidate => string.Equals(candidate.CriterionId, criterion.Id, StringComparison.Ordinal)
                             && candidate.CriterionRevision == criterion.Revision);

            if (result is null
                || !string.Equals(result.Status, GoalCriterionResultStatuses.Passed, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>当前单元是否已有可用的通过验收（推进/完成的唯一依据）。</summary>
    public static bool HasVerifiedAcceptance(GoalVerificationDecision decision)
        => AllRequiredCriteriaPassed(decision);

    public static bool HasFailedCriteria(GoalVerificationDecision decision)
        => decision.CriterionResults.Any(
            result => string.Equals(result.Status, GoalCriterionResultStatuses.Failed, StringComparison.Ordinal));

    public static bool IsWaiting(GoalVerificationDecision decision)
        => string.Equals(ComputeDisposition(decision), GoalSettlementDispositions.Wait, StringComparison.Ordinal);

    /// <summary>ADR-092 §6.1：由 verdict + 条件结果推导处置；未知一律保守为 repair，不得默认完成。</summary>
    public static string ComputeDisposition(GoalVerificationDecision decision)
    {
        if (decision.Verdict == GoalVerificationVerdict.Unsafe)
            return GoalSettlementDispositions.Stop;

        if (decision.Verdict == GoalVerificationVerdict.NeedsUser)
            return GoalSettlementDispositions.NeedsUser;

        if (decision.Verdict == GoalVerificationVerdict.Blocked)
        {
            var blocker = decision.BlockerCode ?? string.Empty;
            if (WaitBlockerCodes.Contains(blocker, StringComparer.Ordinal))
                return GoalSettlementDispositions.Wait;

            return UnrecoverableBlockerCodes.Contains(blocker, StringComparer.Ordinal)
                ? GoalSettlementDispositions.Stop
                : GoalSettlementDispositions.Repair;
        }

        // Continue/Complete 但没有任何已验证必需条件：Turn 结束不等于单元完成。
        if (!HasVerifiedAcceptance(decision))
        {
            return HasFailedCriteria(decision)
                ? GoalSettlementDispositions.Repair
                : GoalSettlementDispositions.ContinueCurrent;
        }

        return decision.Verdict == GoalVerificationVerdict.Complete
            ? GoalSettlementDispositions.Complete
            : GoalSettlementDispositions.Advance;
    }
}
