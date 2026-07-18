using System.Collections.Concurrent;
using System.Diagnostics;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// Process-local single-writer gate for Runtime session state.
/// Durable Conversation fencing remains the cross-process authority; this gate
/// protects the shared in-memory history and session state used by every local
/// invocation path.
/// </summary>
public sealed class SessionExecutionGate(
    ILogger<SessionExecutionGate> logger) : ISessionExecutionGate
{
    private readonly ConcurrentDictionary<string, GateEntry> _entries =
        new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> EnterAsync(
        string sessionId,
        string executionSource,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionSource);

        GateEntry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(sessionId, static _ => new GateEntry());
            lock (entry.Sync)
            {
                if (entry.Retired)
                    continue;

                entry.ReferenceCount++;
                break;
            }
        }

        var wait = Stopwatch.StartNew();
        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            ReleaseReference(sessionId, entry, releaseSemaphore: false);
            throw;
        }

        wait.Stop();
        if (wait.ElapsedMilliseconds >= 25)
        {
            logger.LogInformation(
                "[SessionExecutionGate] Acquired after wait session={SessionId} source={Source} waitMs={WaitMs}",
                sessionId,
                executionSource,
                wait.ElapsedMilliseconds);
        }

        return new Lease(this, sessionId, entry);
    }

    private void Release(string sessionId, GateEntry entry)
        => ReleaseReference(sessionId, entry, releaseSemaphore: true);

    private void ReleaseReference(string sessionId, GateEntry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
            entry.Semaphore.Release();

        lock (entry.Sync)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount != 0)
                return;

            entry.Retired = true;
            _entries.TryRemove(new KeyValuePair<string, GateEntry>(sessionId, entry));
        }
    }

    private sealed class GateEntry
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class Lease(
        SessionExecutionGate owner,
        string sessionId,
        GateEntry entry) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(sessionId, entry);

            return ValueTask.CompletedTask;
        }
    }
}
