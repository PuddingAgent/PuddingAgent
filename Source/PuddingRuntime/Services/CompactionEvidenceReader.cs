using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingRuntime.Services;

/// <summary>
/// Resolves immutable coverage manifests, including earlier checkpoints, without scanning live
/// history or trusting IDs written by a summarizer. Each page has bounded database work.
/// </summary>
public sealed class CompactionEvidenceReader(IDbContextFactory<MemoryDbContext> dbFactory) : ICompactionEvidenceReader
{
    public async Task<CompactionEvidencePage> ReadSourcesAsync(CompactionEvidenceQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (status, manifest, ids) = await LoadManifestAsync(db, query, ct);
        if (status != "ok") return new(status, []);
        var offset = Math.Clamp(query.Offset, 0, ids.Length);
        var pageIds = ids.Skip(offset).Take(Math.Clamp(query.Limit, 1, 100)).ToArray();
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.SessionId == query.SessionId && pageIds.Contains(m.MessageId))
            .Select(m => new MessageEntity { MessageId = m.MessageId, Source = m.Source, Metadata = m.Metadata, Role = m.Role })
            .ToDictionaryAsync(m => m.MessageId, ct);
        // A prior summary remains a navigable node after repeated compaction. No recursive
        // expansion here: the caller pages into the child, so large graphs stay bounded.
        var children = await db.CompactionCoverageManifests.AsNoTracking()
            .Where(m => m.SessionId == query.SessionId && pageIds.Contains(m.FinalSummaryId!)
                && m.OmittedCount == 0 && m.FailureReason == null
                && m.TargetGeneration <= manifest!.SourceGeneration)
            .Select(m => new { m.FinalSummaryId, m.CompactionId })
            .ToListAsync(ct);
        var references = pageIds.Select(id =>
        {
            if (!rows.TryGetValue(id, out var row))
                return new CompactionSourceReference(id, query.SessionId, null, null, null, "missing");
            var child = children.SingleOrDefault(c => c.FinalSummaryId == id)?.CompactionId;
            var turn = ReadCanonicalTurnId(row);
            return new CompactionSourceReference(id, query.SessionId, row.Role, turn, child,
                child is not null ? "checkpoint" : turn is not null ? "turn" : "archived_source");
        }).ToArray();
        var next = offset + pageIds.Length;
        return new(references.Any(r => r.Availability == "missing") ? "partial" : "ok",
            references, ids.Length, next < ids.Length ? next : null);
    }

    public async Task<CompactionSourceWindow> ReadSourceAsync(CompactionEvidenceQuery query, string sourceMessageId,
        int contentOffset = 0, int windowSize = 16384, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (status, _, ids) = await LoadManifestAsync(db, query, ct);
        if (status != "ok") return new(status);
        if (!ids.Contains(sourceMessageId, StringComparer.Ordinal)) return new("not_found");
        var row = await db.Messages.AsNoTracking()
            .SingleOrDefaultAsync(m => m.SessionId == query.SessionId && m.MessageId == sourceMessageId, ct);
        if (row is null) return new("source_missing");
        var content = JsonSerializer.Serialize(new
        {
            row.Role, row.Content, row.ToolCallsJson, row.ToolResultJson, row.AttachmentsJson,
            row.ThinkingJson, turnId = ReadCanonicalTurnId(row),
        });
        var offset = Math.Clamp(contentOffset, 0, content.Length);
        var length = Math.Min(Math.Clamp(windowSize, 1, 32768), content.Length - offset);
        var end = offset + length;
        return new("ok", content.Substring(offset, length), content.Length,
            end < content.Length ? end : null, row.ContentType);
    }

    private static string? ReadCanonicalTurnId(MessageEntity row)
    {
        if (row.Source != "chat_transcript" || row.Metadata is null) return null;
        var separator = row.Metadata.IndexOf('\n');
        return separator > 0 ? row.Metadata[..separator] : null;
    }

    private static async Task<(string Status, CompactionCoverageManifestEntity? Manifest, string[] Ids)> LoadManifestAsync(
        MemoryDbContext db, CompactionEvidenceQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.WorkspaceId) || string.IsNullOrWhiteSpace(query.SessionId)
            || string.IsNullOrWhiteSpace(query.CompactionId)) return ("not_found", null, []);
        var scoped = await db.Sessions.AsNoTracking().AnyAsync(s => s.SessionId == query.SessionId
            && s.WorkspaceId == query.WorkspaceId
            && (string.IsNullOrWhiteSpace(query.AgentInstanceId) || s.AgentId == query.AgentInstanceId), ct);
        if (!scoped) return ("not_found", null, []);
        var manifest = await db.CompactionCoverageManifests.AsNoTracking()
            .Where(m => m.CompactionId == query.CompactionId && m.SessionId == query.SessionId
                && m.OmittedCount == 0 && m.FailureReason == null && m.FinalSummaryId != null)
            .Select(m => new CompactionCoverageManifestEntity
            {
                CompactionId = m.CompactionId, SourceMessageIds = m.SourceMessageIds,
                CoveredCount = m.CoveredCount, SourceGeneration = m.SourceGeneration,
            }).SingleOrDefaultAsync(ct);
        if (manifest is null) return ("not_found", null, []);
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(manifest.SourceMessageIds ?? "null");
            if (ids is null || ids.Length != manifest.CoveredCount || ids.Any(string.IsNullOrWhiteSpace)
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                return ("invalid_manifest", null, []);
            return ("ok", manifest, ids);
        }
        catch (JsonException) { return ("invalid_manifest", null, []); }
    }
}
