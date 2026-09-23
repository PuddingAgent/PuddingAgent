using System.Threading.Channels;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// Bounded, in-process hand-off between the file-system watcher callbacks (producers) and the
/// coalescer (consumer).
/// <para>
/// Producers may only call <see cref="TryPublish"/>, which never blocks: a watcher callback must not
/// wait for a consumer. A rejected observation is reported by returning <c>false</c> — the queue does
/// not know about <see cref="CodeIndexScopeState"/>, so it is the <b>caller</b> that records the
/// overflow and flags the scope as needing reconciliation.
/// </para>
/// </summary>
public sealed class CodeIndexChangeQueue
{
    /// <summary>Default queue capacity mandated by ADR-089 §2 (U3-A).</summary>
    public const int DefaultCapacity = 8192;

    private readonly Channel<IndexChange> _channel;
    private long _droppedCount;

    /// <summary>Creates a bounded queue.</summary>
    /// <param name="capacity">Maximum number of buffered observations. Must be positive.</param>
    public CodeIndexChangeQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Queue capacity must be positive.");

        Capacity = capacity;
        _channel = Channel.CreateBounded<IndexChange>(new BoundedChannelOptions(capacity)
        {
            // FullMode.Wait is required by the contract: the channel must never silently discard an
            // item. Rejection is decided by TryPublish returning false, so the caller (never the
            // channel) decides how to record the loss.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Configured capacity.</summary>
    public int Capacity { get; }

    /// <summary>Reader side, consumed by <see cref="CodeIndexChangeCoalescer"/>.</summary>
    public ChannelReader<IndexChange> Reader => _channel.Reader;

    /// <summary>Number of observations currently buffered.</summary>
    public int Depth => _channel.Reader.Count;

    /// <summary>Number of publish attempts rejected because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Non-blocking publish. Returns <c>false</c> when the queue is full (the observation is dropped and
    /// <see cref="DroppedCount"/> is incremented); the caller must then flag the scope.
    /// </summary>
    public bool TryPublish(IndexChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (_channel.Writer.TryWrite(change))
            return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <summary>Non-blocking read of one buffered observation.</summary>
    public bool TryRead(out IndexChange? change) => _channel.Reader.TryRead(out change);

    /// <summary>Completes the writer so waiting readers observe completion.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
