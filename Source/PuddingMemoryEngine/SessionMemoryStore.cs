using System.Collections.Concurrent;

namespace PuddingMemoryEngine;

/// <summary>
/// Session 级记忆存储——生命周期与 Session 绑定，Session 结束后清除。
/// 线程安全，使用 ConcurrentDictionary&lt;sessionId, List&lt;MemoryEntry&gt;&gt; 结构。
/// </summary>
public sealed class SessionMemoryStore
{
    // key = sessionId
    private readonly ConcurrentDictionary<string, List<MemoryEntry>> _store = new();

    /// <summary>
    /// 写入一条 Session 记忆。
    /// 同一 Session 内最多保留 <see cref="MaxEntriesPerSession"/> 条，超出后淘汰最旧的。
    /// </summary>
    public void Write(string sessionId, MemoryEntry entry)
    {
        var list = _store.GetOrAdd(sessionId, _ => []);
        lock (list)
        {
            list.Add(entry);
            if (list.Count > MaxEntriesPerSession)
                list.RemoveAt(0);
        }
    }

    /// <summary>读取指定 Session 的所有记忆（快照副本，顺序为写入顺序）。</summary>
    public IReadOnlyList<MemoryEntry> Recall(string sessionId)
    {
        if (!_store.TryGetValue(sessionId, out var list)) return [];
        lock (list) { return list.ToArray(); }
    }

    /// <summary>清除整个 Session 的记忆（Session 结束时调用）。</summary>
    public void Clear(string sessionId)
    {
        _store.TryRemove(sessionId, out _);
    }

    /// <summary>每 Session 最大条目数，超过时滑动淘汰最旧条目。</summary>
    public int MaxEntriesPerSession { get; init; } = 200;
}
