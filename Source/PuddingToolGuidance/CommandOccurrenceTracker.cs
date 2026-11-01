using System.Collections.Concurrent;

namespace PuddingToolGuidance;

/// <summary>
/// 窗口内「同一条命令出现了几次」的计数器。
/// <para>
/// 与 <see cref="ConsecutiveFailureTracker"/> 的分工：那个数**连续失败**（成功即清零，用于判定低效循环）；
/// 这个数**出现次数**（不因成功而清零，用于判定「同样的开销被重复支付」，
/// 例如第二次 <c>dotnet test</c> 本可以带 <c>--no-build</c> 跳过构建）。
/// </para>
/// <para>
/// 边界与前者一致：键含 sessionId、超窗视为 0、容量上限 + 惰性淘汰。
/// </para>
/// </summary>
public sealed class CommandOccurrenceTracker
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly TimeProvider _time;

    public CommandOccurrenceTracker(
        TimeSpan? window = null, int capacity = 512, TimeProvider? timeProvider = null)
    {
        _window = window ?? TimeSpan.FromMinutes(30);
        _capacity = Math.Max(16, capacity);
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Window => _window;

    /// <summary>记录一次出现并返回**含本次在内**的窗口计数。</summary>
    public int Register(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _time.GetUtcNow();

        if (_entries.Count >= _capacity && !_entries.ContainsKey(key))
            EvictExpired(now);

        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry(1, now),
            (_, existing) => now - existing.LastAtUtc > _window
                ? new Entry(1, now)
                : new Entry(existing.Count + 1, now));

        return entry.Count;
    }

    /// <summary>只读查询，不计数。</summary>
    public int Peek(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            return 0;

        return _time.GetUtcNow() - entry.LastAtUtc > _window ? 0 : entry.Count;
    }

    public int Count => _entries.Count;

    private void EvictExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (now - pair.Value.LastAtUtc > _window)
                _entries.TryRemove(pair.Key, out _);
        }
    }

    private readonly record struct Entry(int Count, DateTimeOffset LastAtUtc);
}
