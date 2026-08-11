using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Implements <see cref="IBackfillChatMessageSource"/> and <see cref="IChatMessageBackfillSource"/>
/// by querying the Platform database ChatMessages table with keyset pagination.
/// </summary>
public sealed class BackfillChatMessageSource(
    IDbContextFactory<PlatformDbContext> dbFactory)
    : IBackfillChatMessageSource, IChatMessageBackfillSource
{
    public async Task<IReadOnlyList<BackfillChatMessageRow>> GetBatchAfterIdAsync(
        long afterId,
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var batch = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.Id > afterId)
            .OrderBy(m => m.Id)
            .Take(limit)
            .Select(m => new BackfillChatMessageRow
            {
                Id = m.Id,
                MessageId = m.MessageId,
                WorkspaceId = m.WorkspaceId,
                SessionId = m.SessionId,
                Role = m.Role,
                Content = m.Content,
            })
            .ToListAsync(ct);

        return batch;
    }
}
