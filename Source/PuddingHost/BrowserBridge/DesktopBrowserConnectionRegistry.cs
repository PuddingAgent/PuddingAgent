namespace PuddingHost.BrowserBridge;

public interface IDesktopBrowserConnectionRegistry
{
    DesktopBrowserConnection? Current { get; }
    bool IsDesktopConnected { get; }

    /// <summary>
    /// Gets the next generation counter for a new connection.
    /// Must be on the interface so Endpoint does not cast to concrete type.
    /// </summary>
    long NextGeneration();

    bool TryAttach(DesktopBrowserConnection connection);
    void Detach(Guid connectionId, long generation);
}

/// <summary>
/// Thread-safe registry that tracks the current authenticated Desktop connection.
/// Uses generation counters to prevent stale connections from affecting new ones.
/// 
/// Phase 2A-1 fix: TryAttach rejects if ANY connection exists (including AwaitingHello),
/// preventing zombie Receive loops from replaced connections.
/// </summary>
public sealed class DesktopBrowserConnectionRegistry : IDesktopBrowserConnectionRegistry
{
    private readonly object _lock = new();
    private DesktopBrowserConnection? _current;
    private long _generationCounter;

    public DesktopBrowserConnection? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// True only when a connection exists AND its handshake has been accepted.
    /// </summary>
    public bool IsDesktopConnected
    {
        get
        {
            lock (_lock)
            {
                return _current is { IsHandshakeAccepted: true };
            }
        }
    }

    /// <summary>
    /// Gets the next generation counter. On the interface to avoid concrete cast.
    /// </summary>
    public long NextGeneration()
    {
        lock (_lock)
        {
            return ++_generationCounter;
        }
    }

    /// <summary>
    /// Attempts to attach a new connection.
    /// Rejects if ANY existing connection is present (even AwaitingHello),
    /// preventing zombie Receive loops. The old Endpoint must Detach in its
    /// finally block before a new connection can attach.
    /// </summary>
    public bool TryAttach(DesktopBrowserConnection connection)
    {
        lock (_lock)
        {
            // Reject if any connection exists — no silent replacement
            if (_current is not null)
                return false;

            _current = connection;
            return true;
        }
    }

    /// <summary>
    /// Detaches only if the connectionId AND generation match the current connection.
    /// Prevents a stale finally-block from killing a newer connection.
    /// </summary>
    public void Detach(Guid connectionId, long generation)
    {
        lock (_lock)
        {
            if (_current?.ConnectionId == connectionId && _current.Generation == generation)
            {
                _current = null;
            }
        }
    }
}
