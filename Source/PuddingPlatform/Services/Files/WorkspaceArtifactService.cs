using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Eventing;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatform.Services.Files;

public sealed record WorkspaceArtifactRecord(string WorkspaceId, string ArtifactId, string Version, string ContentRef,
    string PayloadHash, DateTimeOffset CreatedAt);

/// <summary>
/// Versioned workspace artifact service: the artifact row and the <c>workspace.artifact.created</c> outbox event commit
/// in one transaction. A failure before commit leaves neither the artifact nor the event; the event carries the artifact
/// reference so subscribers can resolve the exact version.
/// </summary>
public sealed class WorkspaceArtifactService(IDbContextFactory<PlatformDbContext> dbFactory,
    EventOutboxUnitOfWork outbox, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    /// <summary>Test seam: runs after both the row and event are staged, before the transaction commits.</summary>
    internal Action? BeforeCommitForTests { get; set; }

    public async Task<WorkspaceArtifactRecord> CommitAsync(string workspaceId, string artifactId, string version,
        string contentRef, string payloadHash, CancellationToken ct = default)
    {
        Validate(workspaceId, artifactId, version, contentRef, payloadHash);
        var now = _clock.GetUtcNow();
        var eventId = Guid.NewGuid().ToString("N");
        await outbox.ExecuteAsync(async (db, view, token) =>
        {
            db.WorkspaceArtifacts.Add(new WorkspaceArtifactEntity
            {
                ArtifactVersionId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                ArtifactId = artifactId,
                Version = version,
                ContentRef = contentRef,
                PayloadHash = payloadHash,
                CreatedAt = now,
            });
            await view.AppendAsync(new OutboxFact(eventId, workspaceId, PluginEvents.WorkspaceArtifactCreated, 1,
                workspaceId, contentRef, payloadHash, "workspace-artifact", now,
                BusinessRef: artifactId + "@" + version), token);
            BeforeCommitForTests?.Invoke();
            return true;
        }, ct).ConfigureAwait(false);
        return new(workspaceId, artifactId, version, contentRef, payloadHash, now);
    }

    public async Task<WorkspaceArtifactRecord?> GetAsync(string workspaceId, string artifactId, string version,
        CancellationToken ct = default)
    {
        Validate(workspaceId, artifactId, version, "ref", "hash");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.WorkspaceArtifacts.AsNoTracking().SingleOrDefaultAsync(r =>
            r.WorkspaceId == workspaceId && r.ArtifactId == artifactId && r.Version == version, ct)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new(row.WorkspaceId, row.ArtifactId, row.Version, row.ContentRef, row.PayloadHash, row.CreatedAt);
    }

    private static void Validate(string workspaceId, string artifactId, string version, string contentRef,
        string payloadHash)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128
            || string.IsNullOrWhiteSpace(artifactId) || artifactId.Length > 128
            || string.IsNullOrWhiteSpace(version) || version.Length > 128
            || string.IsNullOrWhiteSpace(contentRef) || contentRef.Length > 512
            || string.IsNullOrWhiteSpace(payloadHash) || payloadHash.Length > 128)
            throw new EventingException(EventingErrorCodes.RequestInvalid,
                "A bounded workspace artifact identity and version are required.");
    }
}
