using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-04 只读决策快照：用于 SDK/插件/UI 展示，不提供任意写 GoalPhase 的入口。
// 快照中的结论来自既有状态机/结算的权威事实；展示层不得补写"已验收"或推导下一状态。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalDecisionCriterion(string CriterionId, string Outcome, ImmutableArray<string> EvidenceRefs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalDecisionSnapshot(string DecisionId, string Phase, string ReasonCode,
    ImmutableArray<GoalDecisionCriterion> UnmetCriteria, string? NextAction, ImmutableArray<string> EvidenceRefs,
    string ObjectiveVersion, string AcceptanceContractVersion, string RootBudgetId, long BudgetRevision,
    long ActivationEpoch, int IterationNo, DateTimeOffset DecidedAt);

public interface IGoalDecisionApi
{
    Task<GoalDecisionSnapshot?> GetAsync(string goalRunId, CancellationToken ct = default);
}

public sealed class GoalDecisionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
