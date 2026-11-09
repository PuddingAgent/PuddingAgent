using PuddingCode.Abstractions;
using PuddingCode.Runtime;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Models;

/// <summary>
/// CORE-I1-06 host adapter: a real concurrency pool backed by <see cref="ModelLeaseRegistry"/> generations. One host
/// configuration revision creates one generation; a busy model queues (with parent cancellation) instead of failing the
/// send, and a revoked generation rejects new dispatch.
/// </summary>
public sealed class ModelLeaseConcurrencyPort(
    ModelLeaseRegistry registry,
    ILlmConfigService configs,
    TimeProvider? clock = null) : IModelAttemptConcurrencyPort
{
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(25);
    private const int DefaultConcurrency = 8;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _generationGate = new();
    private long _generation;

    public async ValueTask<IModelAttemptConcurrencyLease> AcquireAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var generation = EnsureGeneration();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var lease = await registry.AcquireAsync(modelId, generation, _clock.GetUtcNow(), LeaseTtl, ct)
                    .ConfigureAwait(false);
                return new Slot(registry, lease.LeaseId);
            }
            catch (ModelException ex) when (ex.Code == ModelErrorCodes.LeaseBusy)
            {
                await Task.Delay(BusyPoll, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates the host generation once from the authorized model catalog; a config reload creates a new one.</summary>
    public long EnsureGeneration()
    {
        lock (_generationGate)
        {
            if (_generation > 0)
                return _generation;
            var limits = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var model in configs.GetAllModels())
            {
                if (string.IsNullOrWhiteSpace(model.ModelId) || limits.ContainsKey(model.ModelId))
                    continue;
                var limit = model.MaxConcurrentRequests
                            ?? configs.GetProviderStrategy(model.ProviderId)?.MaxConcurrentRequests
                            ?? DefaultConcurrency;
                limits[model.ModelId] = Math.Clamp(limit, 1, 64);
            }
            if (limits.Count == 0)
                throw new ModelException(ModelErrorCodes.Unavailable,
                    "The host has no authorized model concurrency policy for this generation.");
            _generation = registry.CreateGeneration(limits, configRevision: 1).Generation;
            return _generation;
        }
    }

    private sealed class Slot(ModelLeaseRegistry registry, string leaseId) : IModelAttemptConcurrencyLease
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            try
            {
                await registry.ReleaseAsync(leaseId).ConfigureAwait(false);
            }
            catch (ModelException ex) when (ex.Code == ModelErrorCodes.LeaseReleaseRejected)
            {
                // Already released by reconciliation; releasing twice must never create a second slot.
            }
        }
    }
}
