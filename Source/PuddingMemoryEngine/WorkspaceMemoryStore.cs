using System.Collections.Concurrent;

namespace PuddingMemoryEngine;

/// <summary>
/// Workspace 级记忆存储——跨 Session 共享，生命周期与 Workspace 绑定。
/// 线程安全，支持按 tag 过滤检索。
/// </summary>
public sealed class WorkspaceMemoryStore
{
    // key = workspaceId
    private readonly ConcurrentDictionary<string, List<MemoryEntry>> _store = new();

    /// <summary>写入或更新一条 Workspace 记忆。每个 Workspace 最多保留 <see cref="MaxEntriesPerWorkspace"/> 条。</summary>
    public void Write(string workspaceId, MemoryEntry entry)
    {
        var list = _store.GetOrAdd(workspaceId, _ => []);
        lock (list)
        {
            list.Add(entry);
            if (list.Count > MaxEntriesPerWorkspace)
                list.RemoveAt(0);
        }
    }

    /// <summary>
    /// 读取指定 Workspace 的记忆，可按 tag 过滤。
    /// 返回快照副本，最新写入的排在最前面（逆序）。
    /// </summary>
    public IReadOnlyList<MemoryEntry> Recall(string workspaceId, string? tag = null)
    {
        if (!_store.TryGetValue(workspaceId, out var list)) return [];

        lock (list)
        {
            var snapshot = list.AsEnumerable().Reverse();
            if (!string.IsNullOrEmpty(tag))
                snapshot = snapshot.Where(e => e.Tag == tag);
            return snapshot.ToArray();
        }
    }

    /// <summary>清除整个 Workspace 的记忆。</summary>
    public void Clear(string workspaceId) => _store.TryRemove(workspaceId, out _);

    /// <summary>每 Workspace 最大条目数，超过时滑动淘汰最旧条目。</summary>
    public int MaxEntriesPerWorkspace { get; init; } = 500;
}
