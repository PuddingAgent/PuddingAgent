using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// EF Core backed memory tag tree indexer.
/// </summary>
public sealed class TagTreeIndexer : IMemoryIndexer
{
    private readonly IDbContextFactory<MemoryDbContext> _dbContextFactory;

    /// <summary>
    /// Creates a tag tree indexer over the memory database.
    /// </summary>
    public TagTreeIndexer(IDbContextFactory<MemoryDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryHit>> SearchByTagPrefixAsync(
        string workspaceId,
        string agentId,
        string tagPrefix,
        int topK = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(tagPrefix)
            || topK <= 0)
        {
            return [];
        }

        var normalizedPrefix = NormalizeTag(tagPrefix);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
        {
            return [];
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.Memories
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId
                     && m.AgentId == agentId
                     && m.SupersededBy == null
                     && (m.Tag == normalizedPrefix || m.Tag.StartsWith(normalizedPrefix + "/")))
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.CreatedAt)
            .Take(topK)
            .Select(m => new
            {
                m.MemoryId,
                m.Tag,
                m.Content,
                m.Importance,
            })
            .ToListAsync(ct);

        return rows
            .Select(m => new MemoryHit(m.MemoryId, m.Tag, m.Content, m.Importance))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagTreeNode>> GetTagChildrenAsync(
        string workspaceId,
        string agentId,
        string? parentTag = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
        {
            return [];
        }

        var normalizedParent = NormalizeTag(parentTag);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var tags = await db.Memories
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId
                     && m.AgentId == agentId
                     && m.SupersededBy == null
                     && m.Tag != "")
            .Select(m => m.Tag)
            .ToListAsync(ct);

        var prefix = string.IsNullOrWhiteSpace(normalizedParent) ? string.Empty : normalizedParent + "/";
        var childTags = tags
            .Where(tag => tag.StartsWith(prefix, StringComparison.Ordinal))
            .Select(tag =>
            {
                var remainder = tag[prefix.Length..];
                var slashIndex = remainder.IndexOf('/');
                return slashIndex >= 0 ? remainder[..slashIndex] : remainder;
            })
            .Where(child => !string.IsNullOrWhiteSpace(child))
            .Distinct(StringComparer.Ordinal)
            .Select(child =>
            {
                var fullTag = string.IsNullOrWhiteSpace(normalizedParent) ? child : $"{normalizedParent}/{child}";
                var count = tags.Count(tag => tag == fullTag || tag.StartsWith(fullTag + "/", StringComparison.Ordinal));
                var hasChildren = tags.Any(tag => tag.StartsWith(fullTag + "/", StringComparison.Ordinal));
                return new TagTreeNode(fullTag, child, count, hasChildren);
            })
            .OrderBy(node => node.Tag, StringComparer.Ordinal)
            .ToArray();

        return childTags;
    }

    private static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        return string.Join(
            '/',
            tag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
