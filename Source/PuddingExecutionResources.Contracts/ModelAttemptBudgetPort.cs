using System.Collections.Immutable;

namespace PuddingExecutionResources.Contracts;

// PA-01 P2：模型调用边界的预算门禁端口。Runtime 在 provider 调用前 Begin、成功后 Settle、失败/未知时
// MarkUnknown；Platform 适配持久账本 + 可信报价。Legacy 根返回 null（不门禁），Enforced/Shadow 返回 grant。

public sealed record ModelAttemptBudgetClaim(string AccountId, ImmutableArray<string> Ancestors, BudgetIdentity Identity,
    string AttemptId, string ModelId, string ModelRevision, string PriceVersion, long MaxInputTokens,
    long MaxOutputTokens, string CostUnit, DateTimeOffset DispatchAt)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(AccountId) && AccountId.Length <= 128
        && Identity is { IsValid: true } && !string.IsNullOrWhiteSpace(AttemptId) && AttemptId.Length <= 128
        && !string.IsNullOrWhiteSpace(ModelId) && ModelId.Length <= 128
        && !string.IsNullOrWhiteSpace(ModelRevision) && ModelRevision.Length <= 128
        && !string.IsNullOrWhiteSpace(PriceVersion) && PriceVersion.Length <= 128
        && MaxInputTokens >= 0 && MaxOutputTokens >= 0
        && !string.IsNullOrWhiteSpace(CostUnit) && CostUnit.Length <= 32;
}

/// <summary>Frozen price basis and reservation identity for one model attempt; prices are per million tokens.</summary>
public sealed record ModelAttemptBudgetGrant(string ReservationId, string AttemptId, bool Shadow, long OwnerEpoch,
    long CancellationRevision, decimal InputPerMillion, decimal CacheHitPerMillion, decimal OutputPerMillion,
    long BaseUnit, string CostUnit)
{
    public decimal CostOf(ModelUsage usage)
    {
        if (BaseUnit <= 0) return 0m;
        var cache = Math.Clamp(usage.CacheHitTokens, 0, usage.InputTokens);
        var normal = usage.InputTokens - cache;
        return (normal * InputPerMillion + cache * CacheHitPerMillion + usage.OutputTokens * OutputPerMillion) / BaseUnit;
    }
}

public interface IModelAttemptBudgetPort
{
    /// <summary>Null means a Legacy root (no gate). Enforced/Shadow must return a grant or throw.</summary>
    ValueTask<ModelAttemptBudgetGrant?> BeginAsync(ModelAttemptBudgetClaim claim, CancellationToken ct = default);
    ValueTask SettleAsync(ModelAttemptBudgetGrant grant, ModelUsage usage, string providerReceipt,
        CancellationToken ct = default);
    ValueTask MarkUnknownAsync(ModelAttemptBudgetGrant grant, string reasonCode, CancellationToken ct = default);
}

public sealed class ModelAttemptBudgetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
