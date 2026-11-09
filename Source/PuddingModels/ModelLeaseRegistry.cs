using PuddingModels.Contracts;

namespace PuddingModels;

public sealed record ModelLease(string LeaseId, string ModelId, long Generation, DateTimeOffset ExpiresAt);

public sealed record ModelGeneration(long Generation, long ConfigRevision, int MaxConcurrentPerModel);

/// <summary>
/// PA-06C dynamic lease generations: a configuration change creates a new generation, blocks new dispatch on
/// revoked generations and applies the new concurrency to new admission only. Existing leases keep their frozen
/// snapshot and their slot until the real attempt completes; revoking never frees an active slot early and each
/// generation cannot create an independent provider limiter that bypasses the global limit.
/// </summary>
public sealed class ModelLeaseRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LeaseState> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _limits = new(StringComparer.Ordinal);
    private readonly HashSet<long> _revoked = [];
    private long _generation;

    public ModelGeneration CreateGeneration(IReadOnlyDictionary<string, int> modelLimits, long configRevision)
    {
        ArgumentNullException.ThrowIfNull(modelLimits);
        if (configRevision < 0) throw Error(ModelErrorCodes.RequestInvalid, "The configuration revision is invalid.");
        lock (_gate)
        {
            foreach (var (modelId, limit) in modelLimits)
            {
                if (!Valid(modelId) || limit is < 1 or > 64)
                    throw Error(ModelErrorCodes.RequestInvalid, "A bounded model concurrency is required.");
            }
            if (_generation > 0) _revoked.Add(_generation);
            _limits.Clear();
            foreach (var (modelId, limit) in modelLimits) _limits[modelId] = limit;
            _generation++;
            return new(_generation, configRevision, modelLimits.Values.DefaultIfEmpty(0).Min());
        }
    }

    public long CurrentGeneration
    {
        get { lock (_gate) return _generation; }
    }

    public long ActiveGenerationLeaseCount(long generation)
    {
        lock (_gate) return _leases.Values.Count(l => l.Generation == generation && !l.Released);
    }

    public int ActiveCount(string modelId)
    {
        lock (_gate) return _leases.Values.Count(l => !l.Released && string.Equals(l.ModelId, modelId, StringComparison.Ordinal));
    }

    public ValueTask<ModelLease> AcquireAsync(string modelId, long generation, DateTimeOffset now, TimeSpan ttl,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Valid(modelId) || generation < 1)
            throw Error(ModelErrorCodes.RequestInvalid, "A bounded model id and generation are required.");
        if (ttl < TimeSpan.FromSeconds(1) || ttl > TimeSpan.FromHours(1))
            throw Error(ModelErrorCodes.RequestInvalid, "The lease lifetime is out of bounds.");
        lock (_gate)
        {
            if (generation != _generation || _revoked.Contains(generation))
                throw Error(ModelErrorCodes.LeaseGenerationRevoked, "New dispatch is blocked for a revoked generation.");
            if (!_limits.TryGetValue(modelId, out var limit))
                throw Error(ModelErrorCodes.Unavailable, "The model is not part of the current policy generation.");
            var active = _leases.Values.Count(l => !l.Released && string.Equals(l.ModelId, modelId, StringComparison.Ordinal));
            if (active >= limit) throw Error(ModelErrorCodes.LeaseBusy, "The model concurrency is occupied.");
            var lease = new LeaseState(Guid.NewGuid().ToString("N"), modelId, generation, now.Add(ttl), Released: false);
            _leases[lease.LeaseId] = lease;
            return ValueTask.FromResult(new ModelLease(lease.LeaseId, modelId, generation, lease.ExpiresAt));
        }
    }

    public ValueTask ReleaseAsync(string leaseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Valid(leaseId)) throw Error(ModelErrorCodes.LeaseInvalid, "A bounded lease id is required.");
        lock (_gate)
        {
            if (!_leases.TryGetValue(leaseId, out var lease) || lease.Released)
                throw Error(ModelErrorCodes.LeaseReleaseRejected, "The lease is unknown or already released.");
            _leases[leaseId] = lease with { Released = true };
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<int> RevokeGenerationAsync(long generation)
    {
        lock (_gate)
        {
            if (generation < 1) throw Error(ModelErrorCodes.RequestInvalid, "A generation is required.");
            _revoked.Add(generation);
            var active = _leases.Values.Count(l => l.Generation == generation && !l.Released);
            return ValueTask.FromResult(active);
        }
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static ModelException Error(string code, string message) => new(code, message);

    private sealed record LeaseState(string LeaseId, string ModelId, long Generation, DateTimeOffset ExpiresAt, bool Released);
}
