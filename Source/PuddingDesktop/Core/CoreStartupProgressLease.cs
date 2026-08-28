namespace PuddingDesktop.Core;

/// <summary>
/// Tracks monotonically increasing Core startup progress. Duplicate or stale
/// messages cannot extend the startup lease.
/// </summary>
internal sealed class CoreStartupProgressLease(DateTimeOffset startedAt)
{
    private readonly object _gate = new();
    private long _lastSequence;
    private DateTimeOffset _lastProgressAt = startedAt;

    public DateTimeOffset LastProgressAt
    {
        get
        {
            lock (_gate)
                return _lastProgressAt;
        }
    }

    public bool TryRenew(long sequence, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            if (sequence <= _lastSequence)
                return false;

            _lastSequence = sequence;
            _lastProgressAt = observedAt;
            return true;
        }
    }
}
