using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-01 01D 公共只读预算入口：只查询受权账户快照，不能设置根身份、补款或撤销取消。
// Unknown 金额/Token 保持 unknown，不允许视图层把它显示为零。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BudgetLimitsView(long? MaxOutputTokens, decimal? MaxCost, int? MaxCalls,
    int MaxConcurrentAttempts, long? MaxInputTokensPerRequest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BudgetActualView(long InputTokens, long OutputTokens, long CacheHitTokens, decimal Cost,
    string CostUnit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BudgetReservedView(long OutputTokens, decimal Cost);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BudgetUnknownView(long OutputTokens, decimal Cost);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionBudgetSnapshot(string AccountId, long Revision, BudgetLimitsView Limits,
    BudgetActualView Actual, BudgetReservedView Reserved, BudgetUnknownView Unknown, int ActiveAttempts,
    bool Cancelled, long CancellationRevision, string? ReasonCode);

public interface IExecutionBudgetApi
{
    Task<ExecutionBudgetSnapshot?> GetAsync(string accountId, CancellationToken ct = default);
}

public sealed class ExecutionBudgetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
