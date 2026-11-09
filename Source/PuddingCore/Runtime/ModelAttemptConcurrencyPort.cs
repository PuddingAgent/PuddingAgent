namespace PuddingCode.Runtime;

/// <summary>
/// CORE-I1-06: per-attempt model concurrency slot. The host owns the real pool/generation; the transport only acquires a
/// slot immediately before a real send and releases it after the attempt has actually finished (settled, unknown or
/// cancelled). It must not acquire a second slot of the same responsibility elsewhere.
/// </summary>
public interface IModelAttemptConcurrencyPort
{
    ValueTask<IModelAttemptConcurrencyLease> AcquireAsync(string modelId, CancellationToken ct = default);
}

/// <summary>A held model concurrency slot. Disposal releases the slot exactly once.</summary>
public interface IModelAttemptConcurrencyLease : IAsyncDisposable
{
}
