using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-08 A: 执行评审面板的后端只读投影。所有字段由主代理映射；组件只渲染，不推导控制结论。
// Unknown 金额必须 known=false/amount=null，禁止用 0 代替未知；缺证据/缺权限以 missingReason 表达。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionReviewSnapshot(string WorkId, string GoalId, string TurnId, string? ReviewId,
    string RunState, string ReviewState, string? StopReasonCode, string? StopReasonSummary, string? ContinueReasonCode,
    string? ContinueReasonSummary, ImmutableArray<ExecutionUnmetCriterion> UnmetCriteria,
    ExecutionReviewBudgetView Budget, ExecutionReviewPolicyView ModelPolicy, ExecutionReviewCancelView Cancel,
    ExecutionReviewFreshnessView Freshness, string? ModelSuggestionFinding, string? ModelSuggestionLevel,
    ImmutableArray<ExecutionFailedCheck> FailedChecks, long SnapshotRevision, long LastSequence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionUnmetCriterion(string CriterionId, string Text, string Outcome,
    ImmutableArray<ExecutionEvidenceRef> EvidenceRefs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionEvidenceRef(string EvidenceId, string Kind, string Label, string Reference,
    string ContentHash, string Availability);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionBudgetAmount(decimal? Amount, string Unit, bool Known);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionReviewBudgetView(long? MaxOutputTokens, decimal? MaxCost,
    ExecutionBudgetAmount Actual, ExecutionBudgetAmount Reserved, ExecutionBudgetAmount Unknown);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionReviewPolicyView(string ModelId, string ModelRevision, string PolicyVersion,
    string PromptVersion, bool InheritedFromPrimary);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionReviewCancelView(bool Requested, string? RequestedAt, bool Draining, int PendingCalls,
    string? ReasonCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionReviewFreshnessView(bool Stale, string DataAsOf, long LastSequence, long SnapshotRevision,
    bool GapDetected);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionFailedCheck(string CheckId, int ExitCode, string Summary);
