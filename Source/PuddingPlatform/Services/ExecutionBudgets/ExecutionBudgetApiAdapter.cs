using Pudding.Api.Contracts;
using PuddingExecutionResources.Contracts;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>
/// PA-01 01D host adapter: exposes the durable ledger as the read-only public budget API. Unknown amounts stay
/// unknown (no zero substitution), the account id is host-owned, and a missing account is null rather than a fake
/// empty budget.
/// </summary>
public sealed class ExecutionBudgetApiAdapter(ExecutionBudgetStore store) : IExecutionBudgetApi
{
    public async Task<ExecutionBudgetSnapshot?> GetAsync(string accountId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 128) return null;
        BudgetSnapshot snapshot;
        try
        {
            snapshot = await store.ReadAsync(new BudgetAccountId(accountId), ct).ConfigureAwait(false);
        }
        catch (ExecutionResourceException ex) when (ex.Code == BudgetErrorCodes.AccountMissing)
        {
            return null;
        }
        var limits = snapshot.Limits;
        return new ExecutionBudgetSnapshot(accountId, snapshot.Revision,
            new BudgetLimitsView(limits.MaxOutputTokens, limits.MaxCost, limits.MaxCalls, limits.MaxConcurrentAttempts,
                limits.MaxInputTokensPerRequest),
            new BudgetActualView(snapshot.InputTokens, snapshot.OutputTokens, snapshot.CacheHitTokens, snapshot.Cost,
                snapshot.CostUnit),
            new BudgetReservedView(snapshot.ReservedOutputTokens, snapshot.ReservedCost),
            new BudgetUnknownView(snapshot.UnknownOutputTokens, snapshot.UnknownCost),
            snapshot.ActiveAttempts, snapshot.Cancelled, snapshot.CancellationRevision, snapshot.ReasonCode);
    }
}
