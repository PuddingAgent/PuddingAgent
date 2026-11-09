using System.Collections.Immutable;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>
/// PA-01 P2 adapter: maps the runtime-facing model attempt claim onto <see cref="ModelAttemptBudgetGate"/> and returns a
/// price-frozen grant. Legacy roots return null (runtime is not gated); Enforced/Shadow must reserve or fail.
/// </summary>
public sealed class PlatformModelAttemptBudgetPort(ModelAttemptBudgetGate gate) : IModelAttemptBudgetPort
{
    public async ValueTask<ModelAttemptBudgetGrant?> BeginAsync(ModelAttemptBudgetClaim claim,
        CancellationToken ct = default)
    {
        if (claim is null || !claim.IsValid)
            throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid, "A bounded model attempt claim is required.");
        try
        {
            var ancestors = claim.Ancestors.IsDefault
                ? []
                : claim.Ancestors.Select(a => new BudgetAccountId(a)).ToImmutableArray();
            var permit = await gate.BeginAsync(new ModelAttemptBudgetRequest(new(claim.AccountId), ancestors,
                claim.Identity, claim.AttemptId, claim.ModelId, claim.ModelRevision, claim.PriceVersion,
                claim.MaxInputTokens, claim.MaxOutputTokens, claim.CostUnit, claim.DispatchAt), ct).ConfigureAwait(false);
            if (!permit.Gated) return null;
            var quote = permit.Quote!;
            return new(permit.ReservationId, permit.AttemptId, permit.Shadow, permit.OwnerEpoch,
                permit.CancellationRevision, quote.InputPerMillion ?? 0m, quote.CacheHitPerMillion ?? 0m,
                quote.OutputPerMillion ?? 0m, quote.BaseUnit, quote.Currency);
        }
        catch (ExecutionResourceException ex)
        {
            throw new ModelAttemptBudgetException(ex.Code, ex.Message);
        }
        catch (ModelException ex)
        {
            throw new ModelAttemptBudgetException(ex.Code, ex.Message);
        }
    }

    public async ValueTask SettleAsync(ModelAttemptBudgetGrant grant, ModelUsage usage, string providerReceipt,
        CancellationToken ct = default)
    {
        if (grant is null || string.IsNullOrWhiteSpace(grant.ReservationId))
            throw new ModelAttemptBudgetException(BudgetErrorCodes.SettlementInvalid, "A grant is required to settle.");
        try
        {
            await gate.SettleAsync(new ModelAttemptBudgetPermit(grant.ReservationId, grant.AttemptId, Gated: true,
                grant.Shadow, grant.OwnerEpoch, grant.CancellationRevision, null, 0m), usage,
                grant.CostOf(usage), grant.CostUnit, providerReceipt, ct).ConfigureAwait(false);
        }
        catch (ExecutionResourceException ex)
        {
            throw new ModelAttemptBudgetException(ex.Code, ex.Message);
        }
    }

    public async ValueTask MarkUnknownAsync(ModelAttemptBudgetGrant grant, string reasonCode,
        CancellationToken ct = default)
    {
        if (grant is null || string.IsNullOrWhiteSpace(grant.ReservationId))
            throw new ModelAttemptBudgetException(BudgetErrorCodes.UnknownStale, "A grant is required to mark unknown.");
        try
        {
            await gate.MarkUnknownAsync(new ModelAttemptBudgetPermit(grant.ReservationId, grant.AttemptId, Gated: true,
                grant.Shadow, grant.OwnerEpoch, grant.CancellationRevision, null, 0m), reasonCode, ct)
                .ConfigureAwait(false);
        }
        catch (ExecutionResourceException ex)
        {
            throw new ModelAttemptBudgetException(ex.Code, ex.Message);
        }
    }
}
