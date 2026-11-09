using System.Collections.Immutable;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;
using PuddingModels;
using ModelUsage = PuddingExecutionResources.Contracts.ModelUsage;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-01 P2 "unique reservation" at the actual model call boundary: instead of the in-memory resource scope, every
/// provider attempt goes through <see cref="ModelAttemptBudgetGate"/> (quote → ancestor reserve → persisted dispatch →
/// provider → settle/unknown). The scope remains only for identity/deadline/cancellation and is not double-charged.
/// </summary>
public sealed class DurableModelResourcePool(ModelAttemptBudgetGate gate, string accountId,
    ImmutableArray<BudgetAccountId> ancestors, BudgetIdentity identity, string costUnit, string priceVersion,
    string purpose) : IModelResourcePool
{
    public async Task<ModelResourceReceipt> InvokeAsync(IExecutionResourceScope parent, ILeasedModel model,
        ModelResourceRequest request, CancellationToken ct = default)
    {
        if (parent is null || model is null || request is null || string.IsNullOrWhiteSpace(request.CallId)
            || request.TimeoutMilliseconds is < 1 or > 60000)
            throw new ExecutionResourceException("request.invalid", "A bounded model call is required.");
        var descriptor = model.Descriptor;
        var permit = await gate.BeginAsync(new ModelAttemptBudgetRequest(new(accountId), ancestors, identity,
            request.CallId, descriptor.ModelId, descriptor.Revision, priceVersion, descriptor.MaxInputTokens,
            request.MaxOutputTokens, costUnit, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        ModelCompletion completion;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Cancellation, ct);
        linked.CancelAfter(request.TimeoutMilliseconds);
        try
        {
            completion = await model.InvokeAsync(request, () => { }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryUnknownAsync(permit, "model.cancelled").ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            if (parent.Cancellation.IsCancellationRequested)
                throw new ExecutionResourceException("execution.revoked",
                    "The review execution scope has ended; the dispatched attempt remains unknown.");
            throw new ExecutionResourceException("model.timeout",
                "The review model call timed out; the dispatched attempt remains unknown.");
        }
        catch (ExecutionResourceException)
        {
            await TryUnknownAsync(permit, "model.failed").ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await TryUnknownAsync(permit, "model.failed").ConfigureAwait(false);
            throw new ExecutionResourceException("model.failed", "Model invocation failed; no retry was performed.");
        }

        var usage = completion.Usage ?? new ModelUsage(0, 0);
        decimal cost;
        if (permit.Gated && permit.Quote is { } quote)
        {
            cost = QuoteWindowAdjuster.Cost(quote, usage.InputTokens, usage.OutputTokens, usage.CacheHitTokens);
            if (completion.Usage is null)
                await TryUnknownAsync(permit, "model.usage_unknown").ConfigureAwait(false);
            else
                await gate.SettleAsync(permit, usage, cost, costUnit, request.CallId, ct).ConfigureAwait(false);
        }
        else
        {
            cost = ((usage.InputTokens - usage.CacheHitTokens) * descriptor.Price.InputPerMillion
                    + usage.CacheHitTokens * descriptor.Price.CacheHitPerMillion
                    + usage.OutputTokens * descriptor.Price.OutputPerMillion) / 1_000_000m;
        }
        return new(permit.ReservationId, request.CallId, parent.Identity, descriptor.ModelId, descriptor.Revision,
            usage, cost, costUnit, completion.Text, completion.FinishReason);
    }

    private async ValueTask TryUnknownAsync(ModelAttemptBudgetPermit permit, string reasonCode)
    {
        if (!permit.Gated) return;
        try { await gate.MarkUnknownAsync(permit, reasonCode).ConfigureAwait(false); }
        catch (ExecutionResourceException) { }
    }
}
