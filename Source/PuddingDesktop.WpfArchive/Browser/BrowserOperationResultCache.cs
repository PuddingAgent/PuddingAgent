using System.Collections.Concurrent;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// Caches terminal command results for idempotency.
/// Keeps at most 512 entries or 10 minutes, whichever comes first.
/// Duplicate operationId returns cached result without re-execution.
/// </summary>
public sealed class BrowserOperationResultCache
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<Guid> _insertionOrder = new();

    public bool TryGet(Guid operationId, out BrowserBridgeCommandResult? result)
    {
        if (_cache.TryGetValue(operationId, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.CachedAt <= MaxAge)
            {
                result = entry.Result;
                return true;
            }
            // Expired
            _cache.TryRemove(operationId, out _);
        }
        result = null;
        return false;
    }

    public void Add(Guid operationId, BrowserBridgeCommandResult result)
    {
        var entry = new CacheEntry(result, DateTimeOffset.UtcNow);
        _cache[operationId] = entry;
        _insertionOrder.Enqueue(operationId);
        Evict();
    }

    private void Evict()
    {
        var now = DateTimeOffset.UtcNow;

        // Evict by age
        foreach (var kvp in _cache)
        {
            if (now - kvp.Value.CachedAt > MaxAge)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }

        // Evict by count
        while (_cache.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        while (_insertionOrder.TryDequeue(out _)) { }
    }

    private sealed record CacheEntry(BrowserBridgeCommandResult Result, DateTimeOffset CachedAt);
}
