using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Eventing;

public sealed record PluginArchiveRecord(string OperationId, string EventId, string WorkspaceId, string PluginId,
    string ArtifactRef, string PayloadHash);

public sealed record PluginArchiveReceipt(string ArchiveId, string OperationId, bool Replayed);

public sealed record PluginArchiveEntry(string ArchiveId, string OperationId, string EventId, string WorkspaceId,
    string PluginId, string ArtifactRef, string PayloadHash, DateTimeOffset RecordedAt);

/// <summary>
/// Host archive/audit sink for plugin event observers. Recording is idempotent on <c>OperationId</c> (the frozen
/// observer contract uses OperationId=EventId), so a redelivered committed event yields one logical archive record;
/// the receipt reports replay instead of creating a second row.
/// </summary>
public sealed class PluginArchiveStore(IDbContextFactory<PlatformDbContext> dbFactory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<PluginArchiveReceipt> RecordAsync(PluginArchiveRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsValid(record))
            throw new EventingException(EventingErrorCodes.RequestInvalid, "A bounded plugin archive record is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.PluginArchiveRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.OperationId == record.OperationId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.EventId, record.EventId, StringComparison.Ordinal))
                throw new EventingException(EventingErrorCodes.RequestInvalid,
                    "The operation id was already used for a different event.");
            return new(existing.ArchiveId, existing.OperationId, Replayed: true);
        }

        var entity = new PluginArchiveRecordEntity
        {
            ArchiveId = Guid.NewGuid().ToString("N"),
            OperationId = record.OperationId,
            EventId = record.EventId,
            WorkspaceId = record.WorkspaceId,
            PluginId = record.PluginId,
            ArtifactRef = record.ArtifactRef,
            PayloadHash = record.PayloadHash,
            RecordedAt = _clock.GetUtcNow(),
        };
        db.PluginArchiveRecords.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(entity.ArchiveId, entity.OperationId, Replayed: false);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert of the same operation: the unique index wins, replay the committed row.
            existing = await db.PluginArchiveRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.OperationId == record.OperationId, ct).ConfigureAwait(false);
            if (existing is null)
                throw;
            if (!string.Equals(existing.EventId, record.EventId, StringComparison.Ordinal))
                throw new EventingException(EventingErrorCodes.RequestInvalid,
                    "The operation id was already used for a different event.");
            return new(existing.ArchiveId, existing.OperationId, Replayed: true);
        }
    }

    public async ValueTask<IReadOnlyList<PluginArchiveEntry>> ListAsync(string workspaceId, string? eventId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.PluginArchiveRecords.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(eventId))
            query = query.Where(r => r.EventId == eventId);
        var rows = await query.Select(r => new PluginArchiveEntry(r.ArchiveId, r.OperationId, r.EventId,
                r.WorkspaceId, r.PluginId, r.ArtifactRef, r.PayloadHash, r.RecordedAt))
            .ToListAsync(ct).ConfigureAwait(false);
        // SQLite cannot ORDER BY DateTimeOffset; deterministic ordering is applied client-side.
        return rows.OrderBy(r => r.RecordedAt).ThenBy(r => r.ArchiveId, StringComparer.Ordinal).ToList();
    }

    private static bool IsValid(PluginArchiveRecord r) =>
        !string.IsNullOrWhiteSpace(r.OperationId) && r.OperationId.Length <= 128
        && !string.IsNullOrWhiteSpace(r.EventId) && r.EventId.Length <= 128
        && !string.IsNullOrWhiteSpace(r.WorkspaceId) && r.WorkspaceId.Length <= 128
        && !string.IsNullOrWhiteSpace(r.PluginId) && r.PluginId.Length <= 128
        && !string.IsNullOrWhiteSpace(r.ArtifactRef) && r.ArtifactRef.Length <= 512
        && !string.IsNullOrWhiteSpace(r.PayloadHash) && r.PayloadHash.Length <= 128;
}
