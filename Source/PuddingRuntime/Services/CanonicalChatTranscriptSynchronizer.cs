using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingRuntime.Services;

/// <summary>
/// Incrementally mirrors the canonical platform <c>ChatMessages</c> transcript into the
/// Runtime memory database. Compaction and cold-history hydration must use this same path;
/// otherwise a Core restart can hydrate an older intent while newer accepted turns only exist
/// in the platform database.
/// </summary>
internal static class CanonicalChatTranscriptSynchronizer
{
    internal const int PageSize = 256;
    private const string HighWatermarkMetadataKey = "canonicalChatTranscriptHighWatermark";

    internal sealed record SyncResult(int RowsRead, int Imported, long HighWatermark);

    internal static async Task<SyncResult> SynchronizeAsync(
        MemoryDbContext memoryDb,
        ICompactionChatMessageStore messageStore,
        string sessionId,
        string? fallbackWorkspaceId,
        string? fallbackAgentId,
        CancellationToken ct)
    {
        var transcriptMessagePrefix = BuildTranscriptMessagePrefix(sessionId);
        var session = await memoryDb.Sessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        var lastImportedMessageId = await memoryDb.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId
                && m.Source == "chat_transcript"
                && m.MessageId.StartsWith(transcriptMessagePrefix))
            .OrderByDescending(m => m.Sequence)
            .Select(m => m.MessageId)
            .FirstOrDefaultAsync(ct);
        var messageDerivedHighWatermark = TryParseTranscriptPlatformId(
            lastImportedMessageId,
            transcriptMessagePrefix,
            out var parsedPlatformId)
            ? parsedPlatformId
            : 0L;
        var afterPlatformId = Math.Max(
            messageDerivedHighWatermark,
            ReadPersistedHighWatermark(session?.Metadata));
        var nextSequence = await memoryDb.Messages
            .Where(m => m.SessionId == sessionId)
            .Select(m => (long?)m.Sequence)
            .MaxAsync(ct) ?? 0;

        var totalRowsRead = 0;
        var totalImported = 0;
        while (true)
        {
            var transcriptRows = await messageStore.GetForSessionAfterIdAsync(
                sessionId,
                afterPlatformId,
                PageSize,
                ct);
            if (transcriptRows.Count == 0)
                break;

            totalRowsRead += transcriptRows.Count;
            var firstRow = transcriptRows[0];
            var lastActivityAt = transcriptRows.Max(m => m.CreatedAt);
            var workspaceId = string.IsNullOrWhiteSpace(fallbackWorkspaceId)
                ? firstRow.WorkspaceId
                : fallbackWorkspaceId;
            var agentId = string.IsNullOrWhiteSpace(fallbackAgentId)
                ? firstRow.AgentInstanceId
                : fallbackAgentId;

            if (session is null)
            {
                session = new SessionEntity
                {
                    SessionId = sessionId,
                    WorkspaceId = workspaceId ?? string.Empty,
                    AgentId = agentId ?? string.Empty,
                    Status = "active",
                    CreatedAt = firstRow.CreatedAt,
                    LastActivityAt = lastActivityAt,
                };
                memoryDb.Sessions.Add(session);
            }
            else
            {
                session.WorkspaceId = string.IsNullOrWhiteSpace(session.WorkspaceId)
                    ? workspaceId ?? string.Empty
                    : session.WorkspaceId;
                session.AgentId = string.IsNullOrWhiteSpace(session.AgentId)
                    ? agentId ?? string.Empty
                    : session.AgentId;
                session.LastActivityAt = Math.Max(session.LastActivityAt, lastActivityAt);
            }

            var pageMessageIds = transcriptRows
                .Select(row => BuildTranscriptMessageId(row.Id, sessionId))
                .ToArray();
            var existing = (await memoryDb.Messages
                    .AsNoTracking()
                    .Where(m => pageMessageIds.Contains(m.MessageId))
                    .Select(m => m.MessageId)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var importedThisPage = 0;
            foreach (var row in transcriptRows)
            {
                if (string.IsNullOrWhiteSpace(row.Content)
                    && string.IsNullOrWhiteSpace(row.ContentPartsJson))
                {
                    continue;
                }

                var messageId = BuildTranscriptMessageId(row.Id, sessionId);
                if (existing.Contains(messageId))
                    continue;

                memoryDb.Messages.Add(CreateTranscriptMessage(
                    row,
                    sessionId,
                    messageId,
                    ++nextSequence,
                    agentId));
                importedThisPage++;
            }

            session.MessageCount += importedThisPage;
            afterPlatformId = transcriptRows[^1].Id;
            session.Metadata = WritePersistedHighWatermark(session.Metadata, afterPlatformId);
            await memoryDb.SaveChangesAsync(ct);
            totalImported += importedThisPage;

            if (transcriptRows.Count < PageSize)
                break;
        }

        return new SyncResult(totalRowsRead, totalImported, afterPlatformId);
    }

    private static MessageEntity CreateTranscriptMessage(
        ChatMessageRow row,
        string sessionId,
        string messageId,
        long sequence,
        string? fallbackAgentId) => new()
    {
        MessageId = messageId,
        SessionId = sessionId,
        Sequence = sequence,
        Role = string.IsNullOrWhiteSpace(row.Role) ? "user" : row.Role,
        ContentType = "text",
        Content = row.Content,
        ThinkingJson = row.ThinkingJson,
        UsageJson = row.UsageJson,
        AgentId = string.IsNullOrWhiteSpace(row.AgentInstanceId) ? fallbackAgentId : row.AgentInstanceId,
        Source = "chat_transcript",
        CreatedAt = row.CreatedAt,
        CanonicalContentHash = CompositionSnapshot.Sha256Hex(
            $"{row.Content ?? string.Empty}\n{row.ContentPartsJson ?? string.Empty}"),
        AttachmentsJson = row.ContentPartsJson,
        // Runtime hydration uses this stable identity to exclude the accepted current turn.
        // The current user message is mirrored for durability, but it must enter provider input
        // only through BuildCurrentUserChatMessage, where the current-turn fence is applied.
        Metadata = BuildCanonicalIdentity(row),
    };

    internal static string BuildCanonicalIdentity(ChatMessageRow row) =>
        $"{row.TurnId ?? string.Empty}\n{row.MessageId}";

    private static long ReadPersistedHighWatermark(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return 0;

        try
        {
            var value = (JsonNode.Parse(metadata) as JsonObject)?[HighWatermarkMetadataKey];
            return value?.GetValue<long>() is { } highWatermark && highWatermark >= 0
                ? highWatermark
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string WritePersistedHighWatermark(string? metadata, long highWatermark)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(metadata ?? string.Empty) as JsonObject ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root[HighWatermarkMetadataKey] = highWatermark;
        return root.ToJsonString();
    }

    private static string BuildTranscriptMessagePrefix(string sessionId) =>
        $"chat-{sessionId[..Math.Min(8, sessionId.Length)]}-";

    private static string BuildTranscriptMessageId(long transcriptId, string sessionId) =>
        $"{BuildTranscriptMessagePrefix(sessionId)}{transcriptId}";

    private static bool TryParseTranscriptPlatformId(
        string? messageId,
        string expectedPrefix,
        out long platformId)
    {
        platformId = 0;
        return !string.IsNullOrWhiteSpace(messageId)
            && messageId.StartsWith(expectedPrefix, StringComparison.Ordinal)
            && long.TryParse(messageId.AsSpan(expectedPrefix.Length), out platformId)
            && platformId >= 0;
    }
}
