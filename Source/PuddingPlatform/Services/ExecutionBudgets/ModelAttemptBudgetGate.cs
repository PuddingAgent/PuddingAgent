using System.Collections.Immutable;
using PuddingExecutionResources.Contracts;
using ModelUsage = PuddingExecutionResources.Contracts.ModelUsage;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.ExecutionBudgets;

public sealed record ModelAttemptBudgetRequest(BudgetAccountId AccountId, ImmutableArray<BudgetAccountId> Ancestors,
    BudgetIdentity Identity, string AttemptId, string ModelId, string ModelRevision, string PriceVersion,
    long MaxInputTokens, long MaxOutputTokens, string AccountCurrency, DateTimeOffset DispatchAt)
{
    public bool IsValid => AccountId is { IsValid: true } && Identity is { IsValid: true }
        && !string.IsNullOrWhiteSpace(AttemptId) && AttemptId.Length <= 128
        && !string.IsNullOrWhiteSpace(ModelId) && ModelId.Length <= 128
        && !string.IsNullOrWhiteSpace(ModelRevision) && ModelRevision.Length <= 128
        && !string.IsNullOrWhiteSpace(PriceVersion) && PriceVersion.Length <= 128
        && MaxInputTokens >= 0 && MaxOutputTokens >= 0 && !string.IsNullOrWhiteSpace(AccountCurrency)
        && AccountCurrency.Length <= 16;
}

/// <summary>A gated model attempt. <see cref="Gated"/> is false only for Legacy roots, which are diagnostic.</summary>
public sealed record ModelAttemptBudgetPermit(string ReservationId, string AttemptId, bool Gated, bool Shadow,
    long OwnerEpoch, long CancellationRevision, ModelQuoteSnapshot? Quote, decimal ReservedCost);

/// <summary>
/// PA-01 P2 model-attempt gate: quote at dispatch time → reserve on every ancestor → dispatch confirmation → return a
/// permit; settle or mark unknown afterwards. Enforced roots always reserve (an unavailable ledger fails closed);
/// Legacy roots never reserve; Shadow reserves but reports itself as shadow so the caller records without gating.
/// </summary>
public sealed class ModelAttemptBudgetGate(ExecutionBudgetStore store, IModelQuoteSource quotes,
    TimeProvider? clock = null)
{
    public const long DefaultOwnerEpoch = 1;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Test seam: runs after the pending reservation exists and before dispatch is recorded.</summary>
    internal Action? AfterReservationForTests { get; set; }

    public async ValueTask<ModelAttemptBudgetPermit> BeginAsync(ModelAttemptBudgetRequest request,
        CancellationToken ct = default)
    {
        if (request is null || !request.IsValid)
            throw Error(BudgetErrorCodes.RequestInvalid, "A bounded model attempt budget request is required.");
        var mode = await store.GetEnforcementModeAsync(request.AccountId, ct).ConfigureAwait(false);
        if (mode == BudgetEnforcementMode.Legacy)
            return new(string.Empty, request.AttemptId, Gated: false, Shadow: false, 0, 0, null, 0m);

        var quote = await quotes.QuoteAsync(new(request.ModelId, request.ModelRevision, request.DispatchAt,
            request.AccountCurrency), ct).ConfigureAwait(false)
            ?? throw new ModelException(ModelErrorCodes.PriceUnknown, "No trustworthy quote is available for dispatch.");
        // CORE-I1-06 T5: the frozen attempt may only be billed at the price/revision/window it was resolved for.
        if (!string.Equals(quote.ModelId, request.ModelId, StringComparison.Ordinal)
            || !string.Equals(quote.Revision, request.ModelRevision, StringComparison.Ordinal))
            throw new ModelException(ModelErrorCodes.PriceUnknown,
                "The quote does not describe the requested model revision.");
        if (!string.Equals(quote.Currency, request.AccountCurrency, StringComparison.Ordinal))
            throw new ModelException(ModelErrorCodes.CurrencyMismatch,
                "The quote currency differs from the account currency.");
        if (!string.Equals(quote.PricingRuleVersion, request.PriceVersion, StringComparison.Ordinal))
            throw new ModelException(ModelErrorCodes.PriceUnknown,
                "The quote pricing rule differs from the frozen price version.");
        if (!quote.IsUsableAt(request.DispatchAt))
            throw new ModelException(ModelErrorCodes.QuoteWindowMissed,
                "The quote is not valid at the actual dispatch instant.");
        var reservedCost = QuoteWindowAdjuster.Cost(quote, request.MaxInputTokens, request.MaxOutputTokens, 0);
        var path = BudgetAccountPaths.RootToLeaf(request.AccountId, request.Ancestors);
        var reservation = await store.ReserveAsync(new ReserveBudget("op:" + request.AttemptId, request.AttemptId,
            request.AccountId, path, request.Identity, request.ModelId, request.ModelRevision, request.PriceVersion,
            request.MaxInputTokens, request.MaxOutputTokens, reservedCost, request.AccountCurrency,
            request.DispatchAt.AddHours(1)), ct).ConfigureAwait(false);
        DispatchPermit permit;
        try
        {
            // Reserve and dispatch are two independent commits: a cancel/deadline between them must release the pending
            // reservation instead of leaving a reserved attempt that never reaches the provider.
            AfterReservationForTests?.Invoke();
            ct.ThrowIfCancellationRequested();
            permit = await store.MarkDispatchedAsync(new DispatchBudget(reservation.ReservationId, request.AttemptId,
                DefaultOwnerEpoch, reservation.CancellationRevision), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ReleasePendingBestEffortAsync(reservation.ReservationId, request.AttemptId).ConfigureAwait(false);
            throw;
        }
        return new(reservation.ReservationId, request.AttemptId, Gated: true, Shadow: mode == BudgetEnforcementMode.Shadow,
            permit.OwnerEpoch, permit.CancellationRevision, quote, reservedCost);
    }

    private async ValueTask ReleasePendingBestEffortAsync(string reservationId, string attemptId)
    {
        try
        {
            await store.ReleaseAsync(new ReleaseBudget(reservationId, attemptId), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ExecutionResourceException ex) when (ex.Code == BudgetErrorCodes.DispatchInvalid)
        {
            // Dispatch may have committed despite the failure; keep it visible as unknown instead of reserving forever.
            try
            {
                await store.MarkUnknownAsync(new MarkUnknownBudget(reservationId, attemptId, DefaultOwnerEpoch,
                    "llm.dispatch_cancelled"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (ExecutionResourceException)
            {
            }
        }
    }

    /// <summary>Settles the actual receipt. A shadow permit settles the same way but the caller must not treat it as an enforced stop.</summary>
    public async ValueTask<SettlementReceipt> SettleAsync(ModelAttemptBudgetPermit permit, ModelUsage usage,
        decimal actualCost, string costUnit, string providerReceipt, CancellationToken ct = default)
    {
        if (permit is null || !permit.Gated || string.IsNullOrWhiteSpace(permit.ReservationId)
            || string.IsNullOrWhiteSpace(costUnit))
            throw Error(BudgetErrorCodes.SettlementInvalid, "Only a gated permit with a cost unit can be settled.");
        return await store.SettleAsync(new SettleBudget(permit.ReservationId, permit.AttemptId, permit.OwnerEpoch,
            usage, actualCost, costUnit, providerReceipt, _clock.GetUtcNow()), ct).ConfigureAwait(false);
    }

    public async ValueTask<BudgetSnapshot> MarkUnknownAsync(ModelAttemptBudgetPermit permit, string reasonCode,
        CancellationToken ct = default)
    {
        if (permit is null || !permit.Gated || string.IsNullOrWhiteSpace(permit.ReservationId))
            throw Error(BudgetErrorCodes.UnknownStale, "Only a gated permit can be marked unknown.");
        return await store.MarkUnknownAsync(new MarkUnknownBudget(permit.ReservationId, permit.AttemptId,
            permit.OwnerEpoch, reasonCode), ct).ConfigureAwait(false);
    }

    private static ExecutionResourceException Error(string code, string message) => new(code, message);
}
