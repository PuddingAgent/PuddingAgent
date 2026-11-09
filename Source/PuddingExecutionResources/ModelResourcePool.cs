using PuddingExecutionResources.Contracts;

namespace PuddingExecutionResources;

/// <summary>Bounded model slots. Cancellation ends observation, but a non-cooperative provider retains its slot until it actually exits.</summary>
public sealed class ModelResourcePool : IModelResourcePool
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Limit, SemaphoreSlim Gate)> _models = new(StringComparer.Ordinal);
    public async Task<ModelResourceReceipt> InvokeAsync(IExecutionResourceScope parent, ILeasedModel model, ModelResourceRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request is null || string.IsNullOrWhiteSpace(request.CallId) || request.CallId.Length > 128 || request.TimeoutMilliseconds is < 1 or > 60000)
            throw new ExecutionResourceException("request.invalid", "A call ID and bounded timeout are required.");
        var descriptor = model.Descriptor; ExecutionResourceScope.ValidateModel(descriptor);
        var reservation = parent.Reserve(descriptor, request.MaxOutputTokens);
        SemaphoreSlim slot;
        lock (_gate)
        {
            if (!_models.TryGetValue(descriptor.Key, out var entry))
            {
                if (_models.Count >= 128) { reservation.Dispose(); throw new ExecutionResourceException("model.catalog_limit", "Model resource catalog is full."); }
                entry = (descriptor.MaxConcurrentCalls, new(descriptor.MaxConcurrentCalls, descriptor.MaxConcurrentCalls)); _models.Add(descriptor.Key, entry);
            }
            if (entry.Limit != descriptor.MaxConcurrentCalls) { reservation.Dispose(); throw new ExecutionResourceException("model.configuration_changed", "Model concurrency changed; recreate the resource pool after draining."); }
            slot = entry.Gate;
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Cancellation, ct);
        linked.CancelAfter(request.TimeoutMilliseconds);
        var acquired = false; Task<ModelCompletion>? execution = null; var transferred = false;
        try
        {
            await slot.WaitAsync(linked.Token); acquired = true; linked.Token.ThrowIfCancellationRequested();
            execution = model.InvokeAsync(request, () => { linked.Token.ThrowIfCancellationRequested(); reservation.MarkDispatched(); }, linked.Token);
            var result = await execution.WaitAsync(linked.Token);
            // Account a confirmed response even when cancellation races with it, but never return late success.
            if (result.Usage is not null) reservation.Settle(result.Usage);
            linked.Token.ThrowIfCancellationRequested();
            if (result.Usage is null) throw new ExecutionResourceException("model.usage_unknown", "Provider usage is unavailable; reservation is not refunded.");
            return new(reservation.Id, request.CallId, parent.Identity, descriptor.ModelId, descriptor.Revision,
                result.Usage, ExecutionResourceScope.Cost(descriptor.Price, result.Usage), descriptor.Price.Unit, result.Text, result.FinishReason);
        }
        catch (OperationCanceledException)
        {
            reservation.MarkUncertain();
            if (execution is { IsCompleted: false })
            {
                transferred = true;
                _ = DrainAsync(execution, reservation, slot, linked);
            }
            if (ct.IsCancellationRequested) throw;
            throw new ExecutionResourceException(parent.Cancellation.IsCancellationRequested ? "execution.revoked" : "model.timeout",
                "Model observation ended; an outstanding provider retains its resource lease.");
        }
        catch (ExecutionResourceException) { throw; }
        catch (Exception) { throw new ExecutionResourceException("model.failed", "Model invocation failed; no retry was performed."); }
        finally
        {
            if (!transferred) { reservation.Dispose(); if (acquired) slot.Release(); linked.Dispose(); }
        }
    }
    private static async Task DrainAsync(Task<ModelCompletion> task, IModelBudgetReservation reservation, SemaphoreSlim slot, CancellationTokenSource linked)
    {
        try { var result = await task; if (result.Usage is { } usage) reservation.Settle(usage); }
        catch (Exception) { /* Unknown/overrun stays in the parent ledger; never publish a late success. */ }
        finally { reservation.Dispose(); slot.Release(); linked.Dispose(); }
    }
}
