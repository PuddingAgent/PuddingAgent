namespace PuddingFinance.Biying;

/// <summary>
/// 进程内有界响应缓存。键是**脱敏后的 URL**（不含证书），所以缓存键不会把凭据写进内存镜像/日志。
/// 有界是为了防止"查了 200 个端点 × 每端点几百 KB"把内存吃穿——单只股票资产负债表就有 229 KB。
/// </summary>
public sealed class BiyingResponseCache(int capacity = 128, IBiyingClock? clock = null)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly IBiyingClock _clock = clock ?? new SystemBiyingClock();
    private readonly int _capacity = Math.Max(1, capacity);

    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    public bool TryGet(string key, out string body)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                body = string.Empty;
                return false;
            }

            if (entry.ExpiresAtUtc <= _clock.UtcNow)
            {
                Remove(key);
                body = string.Empty;
                return false;
            }

            // 触摸：最近使用移到队尾
            _order.Remove(entry.Node);
            _order.AddLast(entry.Node);
            body = entry.Body;
            return true;
        }
    }

    public void Set(string key, string body, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return;

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
                Remove(key);

            var node = _order.AddLast(key);
            _entries[key] = new Entry(body, _clock.UtcNow + ttl, node);

            while (_entries.Count > _capacity && _order.First is { } oldest)
                Remove(oldest.Value);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    private void Remove(string key)
    {
        if (_entries.Remove(key, out var entry))
            _order.Remove(entry.Node);
    }

    private sealed record Entry(string Body, DateTimeOffset ExpiresAtUtc, LinkedListNode<string> Node);
}
