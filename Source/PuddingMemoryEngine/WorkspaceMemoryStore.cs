using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine;

/// <summary>
/// Workspace 级记忆存储——跨 Session 共享，生命周期与 Workspace 绑定。
/// 线程安全，支持按 tag 过滤检索。
/// </summary>
public sealed class WorkspaceMemoryStore
{
    private const string WorkspaceScope = "workspace";

    // key = workspaceId
    private readonly ConcurrentDictionary<string, List<MemoryEntry>> _fallbackStore = new();
    private readonly IDbContextFactory<MemoryDbContext>? _dbContextFactory;

    /// <summary>
    /// 创建 Workspace 记忆存储。
    /// 当 <paramref name="dbContextFactory"/> 为空时，自动回退为原有纯内存实现。
    /// </summary>
    public WorkspaceMemoryStore(IDbContextFactory<MemoryDbContext>? dbContextFactory = null)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>写入或更新一条 Workspace 记忆。每个 Workspace 最多保留 <see cref="MaxEntriesPerWorkspace"/> 条。</summary>
    public void Write(string workspaceId, MemoryEntry entry)
    {
        if (_dbContextFactory is null)
        {
            var list = _fallbackStore.GetOrAdd(workspaceId, _ => []);
            lock (list)
            {
                list.Add(entry);
                if (list.Count > MaxEntriesPerWorkspace)
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
            db.Memories.Add(ToMemoryEntity(entry, workspaceId));
            db.SaveChanges();

            var scopedQuery = db.Memories.Where(x => x.Scope == WorkspaceScope && x.WorkspaceId == workspaceId);
            var count = scopedQuery.Count();

            if (count > MaxEntriesPerWorkspace)
            {
                var overflowCount = count - MaxEntriesPerWorkspace;
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
    /// 读取指定 Workspace 的记忆，可按 tag 过滤。
    /// 返回快照副本，最新写入的排在最前面（逆序）。
    /// </summary>
    public IReadOnlyList<MemoryEntry> Recall(string? workspaceId, string? agentId, string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return [];
        }

        var normalizedWorkspaceId = workspaceId.Trim();

        if (_dbContextFactory is null)
        {
            if (!_fallbackStore.TryGetValue(normalizedWorkspaceId, out var list)) return [];

            lock (list)
            {
                var snapshot = list.AsEnumerable().Reverse();
                if (!string.IsNullOrWhiteSpace(agentId))
                {
                    snapshot = snapshot.Where(e => string.Equals(e.AgentId, agentId, StringComparison.Ordinal));
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    snapshot = snapshot.Where(e => e.Tag == tag);
                }

                return snapshot.ToArray();
            }
        }

        using var db = _dbContextFactory.CreateDbContext();

        var query = db.Memories
            .AsNoTracking()
            .Where(x => x.Scope == WorkspaceScope && x.WorkspaceId == normalizedWorkspaceId);

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            query = query.Where(x => x.AgentId == agentId);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(x => x.Tag == tag);
        }

        var rows = query
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxEntriesPerWorkspace)
            .ToList();

        return rows.Select(ToMemoryEntry).ToArray();
    }

    /// <summary>读取指定 Workspace 的记忆（兼容旧调用）。</summary>
    public IReadOnlyList<MemoryEntry> Recall(string workspaceId, string? tag = null)
        => Recall(workspaceId, agentId: null, tag);

    /// <summary>清除整个 Workspace 的记忆。</summary>
    public void Clear(string workspaceId)
    {
        if (_dbContextFactory is null)
        {
            _fallbackStore.TryRemove(workspaceId, out _);
            return;
        }

        using var db = _dbContextFactory.CreateDbContext();
        var rows = db.Memories.Where(x => x.Scope == WorkspaceScope && x.WorkspaceId == workspaceId).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        db.Memories.RemoveRange(rows);
        db.SaveChanges();
    }

    /// <summary>每 Workspace 最大条目数，超过时滑动淘汰最旧条目。</summary>
    public int MaxEntriesPerWorkspace { get; init; } = 500;

    private static MemoryEntity ToMemoryEntity(MemoryEntry entry, string workspaceId)
    {
        var effectiveAgentId = string.IsNullOrWhiteSpace(entry.AgentId)
            ? (string.IsNullOrWhiteSpace(entry.Source) ? null : entry.Source)
            : entry.AgentId;

        return new MemoryEntity
        {
            MemoryId = string.IsNullOrWhiteSpace(entry.EntryId) ? Guid.NewGuid().ToString("N") : entry.EntryId,
            Scope = WorkspaceScope,
            SessionId = entry.SessionId,
            ParentSessionId = entry.ParentSessionId,
            WorkspaceId = workspaceId,
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
            Scope = MemoryScope.Workspace,
        };
    }
}
