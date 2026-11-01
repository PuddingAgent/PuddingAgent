using System.Collections.Concurrent;

namespace PuddingToolGuidance;

/// <summary>
/// 「同一调用连续失败」计数器：给「是否陷入低效循环」提供判据。
/// <para>
/// 为什么需要状态：单次失败是正常现象（命令写错、文件不在）；**连续多次同样失败**才是信号 ——
/// 说明模型在同一个局部最优里打转，继续换参数重试只会烧时间。
/// </para>
/// <para>
/// 边界：① 键含 sessionId，跨会话不串；② 成功即清零；③ 超过 <see cref="Window"/> 未再失败也清零
/// （十小时前的失败不该拖累现在）；④ 条目上限防止长会话内存膨胀。
/// </para>
/// </summary>
public sealed class ConsecutiveFailureTracker
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly TimeProvider _time;

    public ConsecutiveFailureTracker(
        TimeSpan? window = null, int capacity = 512, TimeProvider? timeProvider = null)
    {
        _window = window ?? TimeSpan.FromMinutes(10);
        _capacity = Math.Max(16, capacity);
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Window => _window;

    /// <summary>
    /// 记录一次结果并返回**本次之后的连续失败次数**（成功返回 0）。
    /// </summary>
    public int Register(string key, bool succeeded)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _time.GetUtcNow();

        if (succeeded)
        {
            _entries.TryRemove(key, out _);
            return 0;
        }

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

    /// <summary>只读查询，不改变计数。</summary>
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
