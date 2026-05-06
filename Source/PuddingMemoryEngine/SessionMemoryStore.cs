using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine;

/// <summary>
/// Session 级记忆存储——生命周期与 Session 绑定，Session 结束后清除。
/// 线程安全，使用 ConcurrentDictionary&lt;sessionId, List&lt;MemoryEntry&gt;&gt; 结构。
/// </summary>
public sealed class SessionMemoryStore
{
    private const string SessionScope = "session";

    // key = sessionId
    private readonly ConcurrentDictionary<string, List<MemoryEntry>> _fallbackStore = new();
    private readonly IDbContextFactory<MemoryDbContext>? _dbContextFactory;

    /// <summary>
    /// 创建 Session 记忆存储。
    /// 当 <paramref name="dbContextFactory"/> 为空时，自动回退为原有纯内存实现。
    /// </summary>
    public SessionMemoryStore(IDbContextFactory<MemoryDbContext>? dbContextFactory = null)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// 写入一条 Session 记忆。
    /// 同一 Session 内最多保留 <see cref="MaxEntriesPerSession"/> 条，超出后淘汰最旧的。
    /// </summary>
    public void Write(string sessionId, MemoryEntry entry)
    {
        if (_dbContextFactory is null)
        {
            var list = _fallbackStore.GetOrAdd(sessionId, _ => []);
            lock (list)
            {
                list.Add(entry);
                if (list.Count > MaxEntriesPerSession)
                {
                    list.RemoveAt(0);
                }
            }

            return;
        }

        using var db = _dbContextFactory.CreateDbContext();
        using var tx = db.Database.BeginTransaction();

        try
        {
            db.Memories.Add(ToMemoryEntity(entry, sessionId));
            db.SaveChanges();

            var scopedQuery = db.Memories.Where(x => x.Scope == SessionScope && x.SessionId == sessionId);
            var count = scopedQuery.Count();

            if (count > MaxEntriesPerSession)
            {
                var overflowCount = count - MaxEntriesPerSession;
                var overflowEntities = scopedQuery
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.MemoryId)
                    .Take(overflowCount)
                    .ToList();

                db.Memories.RemoveRange(overflowEntities);
                db.SaveChanges();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 读取指定 Session 集合的记忆（支持当前 Session + 父 Session 的弱隔离召回）。
    /// 若传入 <paramref name="agentId"/>，则按 Agent 强隔离过滤。
    /// </summary>
    public IReadOnlyList<MemoryEntry> Recall(IEnumerable<string> sessionIds, string? agentId)
    {
        var normalizedSessionIds = sessionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedSessionIds.Length == 0)
        {
            return [];
        }

        if (_dbContextFactory is null)
        {
            var merged = new List<MemoryEntry>();
            foreach (var sessionId in normalizedSessionIds)
            {
                if (!_fallbackStore.TryGetValue(sessionId, out var list))
                {
                    continue;
                }

                lock (list)
                {
                    merged.AddRange(list);
                }
            }

            if (!string.IsNullOrWhiteSpace(agentId))
            {
                merged = merged
                    .Where(x => string.Equals(x.AgentId, agentId, StringComparison.Ordinal))
                    .ToList();
            }

            return merged
                .OrderBy(x => x.CreatedAt)
                .ToArray();
        }

        using var db = _dbContextFactory.CreateDbContext();

        var query = db.Memories
            .AsNoTracking()
            .Where(x => x.Scope == SessionScope && x.SessionId != null && normalizedSessionIds.Contains(x.SessionId));

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            query = query.Where(x => x.AgentId == agentId);
        }

        var rows = query
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxEntriesPerSession)
            .ToList();

        rows.Reverse();
        return rows.Select(ToMemoryEntry).ToArray();
    }

    /// <summary>读取单个 Session 的所有记忆（兼容旧调用）。</summary>
    public IReadOnlyList<MemoryEntry> Recall(string sessionId)
        => Recall([sessionId], agentId: null);

    /// <summary>清除整个 Session 的记忆（Session 结束时调用）。</summary>
    public void Clear(string sessionId)
    {
        if (_dbContextFactory is null)
        {
            _fallbackStore.TryRemove(sessionId, out _);
            return;
        }

        using var db = _dbContextFactory.CreateDbContext();
        var rows = db.Memories.Where(x => x.Scope == SessionScope && x.SessionId == sessionId).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        db.Memories.RemoveRange(rows);
        db.SaveChanges();
    }

    /// <summary>每 Session 最大条目数，超过时滑动淘汰最旧条目。</summary>
    public int MaxEntriesPerSession { get; init; } = 200;

    private static MemoryEntity ToMemoryEntity(MemoryEntry entry, string sessionId)
    {
        var effectiveAgentId = string.IsNullOrWhiteSpace(entry.AgentId)
            ? (string.IsNullOrWhiteSpace(entry.Source) ? null : entry.Source)
            : entry.AgentId;

        return new MemoryEntity
        {
            MemoryId = string.IsNullOrWhiteSpace(entry.EntryId) ? Guid.NewGuid().ToString("N") : entry.EntryId,
            Scope = SessionScope,
            SessionId = sessionId,
            ParentSessionId = entry.ParentSessionId,
            WorkspaceId = entry.WorkspaceId,
            AgentId = effectiveAgentId,
            Tag = string.IsNullOrWhiteSpace(entry.Tag) ? "general" : entry.Tag,
            Content = entry.Content,
            CreatedAt = entry.CreatedAt.ToUnixTimeMilliseconds(),
            Metadata = string.IsNullOrWhiteSpace(entry.Source) ? null : entry.Source,
        };
    }

    private static MemoryEntry ToMemoryEntry(MemoryEntity entity)
    {
        return new MemoryEntry
        {
            EntryId = entity.MemoryId,
            SessionId = entity.SessionId,
            ParentSessionId = entity.ParentSessionId,
            WorkspaceId = entity.WorkspaceId,
            AgentId = entity.AgentId,
            Tag = entity.Tag,
            Content = entity.Content,
            Source = entity.Metadata ?? entity.AgentId ?? "memory-db",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAt),
            Scope = MemoryScope.Session,
        };
    }
}
