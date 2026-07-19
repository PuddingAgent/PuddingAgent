using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// ADR-057-B: Conversation Event Store 实现。
/// 提供 conversation 内 sequence 原子分配、事件批量追加和正向/反向查询。
/// </summary>
public sealed class ConversationEventStore(
    IServiceScopeFactory scopeFactory,
    ICommittedEventSignal signal,
    ILogger<ConversationEventStore> logger) : IConversationEventStore
{
    private bool _tableEnsured;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<AppendResult> AppendAsync(
        string conversationId,
        long expectedVersion,
        IReadOnlyList<NewConversationEvent> events,
        EventWriteCondition condition,
        CancellationToken ct)
    {
        if (events.Count == 0)
            return new AppendResult(0, 0, 0);

        await EnsureTableAsync(ct);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        var committedAt = DateTimeOffset.UtcNow.ToString("O");

        // Use BEGIN IMMEDIATE for serialization
        using var beginCmd = conn.CreateCommand();
        beginCmd.CommandText = "BEGIN IMMEDIATE";
        await beginCmd.ExecuteNonQueryAsync(ct);

        try
        {
            // Ensure head row exists
            using (var ensureCmd = conn.CreateCommand())
            {
                ensureCmd.CommandText = "INSERT OR IGNORE INTO conversation_heads (conversation_id, head_sequence) VALUES (@cid, 0)";
                AddParam(ensureCmd, "@cid", conversationId);
                await ensureCmd.ExecuteNonQueryAsync(ct);
            }

            // Read current head
            long currentHead;
            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = "SELECT head_sequence FROM conversation_heads WHERE conversation_id = @cid";
                AddParam(readCmd, "@cid", conversationId);
                var obj = await readCmd.ExecuteScalarAsync(ct);
                currentHead = Convert.ToInt64(obj ?? 0L);
            }

            // Check expectedVersion
            if (expectedVersion >= 0 && currentHead != expectedVersion)
            {
                Rollback(conn);
                logger.LogWarning(
                    "[ConversationEventStore] Version conflict conv={ConvId} expected={Expected} actual={Actual}",
                    conversationId, expectedVersion, currentHead);
                throw new InvalidOperationException(
                    $"Conversation version conflict: expected {expectedVersion}, actual {currentHead}");
            }

            // Check for duplicate event_ids (idempotency)
            foreach (var evt in events)
            {
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT sequence FROM conversation_events WHERE event_id = @eid AND conversation_id = @cid";
                AddParam(checkCmd, "@eid", evt.EventId);
                AddParam(checkCmd, "@cid", conversationId);
                var existingSeq = await checkCmd.ExecuteScalarAsync(ct);
                if (existingSeq is not null and not DBNull)
                {
                    var dupSeq = Convert.ToInt64(existingSeq);
                    Rollback(conn);
                    logger.LogDebug(
                        "[ConversationEventStore] Duplicate event conv={ConvId} eventId={EventId} existingSeq={Seq}",
                        conversationId, evt.EventId, dupSeq);
                    using var headCmd = conn.CreateCommand();
                    headCmd.CommandText = "SELECT head_sequence FROM conversation_heads WHERE conversation_id = @cid";
                    AddParam(headCmd, "@cid", conversationId);
                    var head = Convert.ToInt64(await headCmd.ExecuteScalarAsync(ct) ?? 0L);
                    return new AppendResult(currentHead + 1, head, 0);
                }
            }

            var seq = currentHead + 1;

            // Insert events
            foreach (var evt in events)
            {
                using var insCmd = conn.CreateCommand();
                insCmd.CommandText = @"
                    INSERT INTO conversation_events
                    (conversation_id, sequence, event_id, workspace_id, turn_id, command_id, run_id, message_id,
                     type, schema_version, payload, occurred_at, committed_at, correlation_id, causation_id, producer_event_id)
                    VALUES (@cid, @seq, @eid, @wsid, @tid, @cmdid, @rid, @mid,
                            @type, @sv, @payload, @oat, @cat, @corr, @caus, @peid)";
                AddParam(insCmd, "@cid", conversationId);
                AddParam(insCmd, "@seq", seq);
                AddParam(insCmd, "@eid", evt.EventId);
                AddParam(insCmd, "@wsid", evt.WorkspaceId ?? "");
                AddParam(insCmd, "@tid", evt.TurnId ?? "");
                AddParam(insCmd, "@cmdid", evt.CommandId ?? (object)DBNull.Value);
                AddParam(insCmd, "@rid", evt.RunId ?? (object)DBNull.Value);
                AddParam(insCmd, "@mid", evt.MessageId ?? (object)DBNull.Value);
                AddParam(insCmd, "@type", evt.Type);
                AddParam(insCmd, "@sv", evt.SchemaVersion);
                AddParam(insCmd, "@payload", evt.Payload.GetRawText());
                AddParam(insCmd, "@oat", committedAt);
                AddParam(insCmd, "@cat", committedAt);
                AddParam(insCmd, "@corr", evt.CorrelationId ?? (object)DBNull.Value);
                AddParam(insCmd, "@caus", evt.CausationId ?? (object)DBNull.Value);
                AddParam(insCmd, "@peid", evt.ProducerEventId ?? (object)DBNull.Value);
                await insCmd.ExecuteNonQueryAsync(ct);
                seq++;
            }

            var lastSeq = seq - 1;

            // Advance head
            using (var updCmd = conn.CreateCommand())
            {
                updCmd.CommandText = "UPDATE conversation_heads SET head_sequence = @hs WHERE conversation_id = @cid";
                AddParam(updCmd, "@hs", lastSeq);
                AddParam(updCmd, "@cid", conversationId);
                await updCmd.ExecuteNonQueryAsync(ct);
            }

            // Commit
            using var commitCmd = conn.CreateCommand();
            commitCmd.CommandText = "COMMIT";
            await commitCmd.ExecuteNonQueryAsync(ct);

            logger.LogInformation(
                "[ConversationEventStore] Appended {Count} events conv={ConvId} seq=[{First},{Last}]",
                events.Count, conversationId, currentHead + 1, lastSeq);

            // ADR-057: Signal SSE notifier after commit
            signal.Signal(conversationId, lastSeq);

            return new AppendResult(currentHead + 1, lastSeq, events.Count);
        }
        catch
        {
            try { Rollback(conn); } catch { }
            throw;
        }
    }

    private static void Rollback(System.Data.Common.DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ROLLBACK";
        cmd.ExecuteNonQuery();
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public async Task<EventPage> ReadForwardAsync(
        string conversationId,
        long afterExclusive,
        long? throughInclusive,
        int limit,
        CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();

        if (throughInclusive.HasValue)
        {
            cmd.CommandText = @"
                SELECT * FROM conversation_events
                WHERE conversation_id = @cid AND sequence > @after AND sequence <= @through
                ORDER BY sequence ASC LIMIT @limit";
            var p = cmd.CreateParameter();
            p.ParameterName = "@through";
            p.Value = throughInclusive.Value;
            cmd.Parameters.Add(p);
        }
        else
        {
            cmd.CommandText = @"
                SELECT * FROM conversation_events
                WHERE conversation_id = @cid AND sequence > @after
                ORDER BY sequence ASC LIMIT @limit";
        }

        var pCid = cmd.CreateParameter();
        pCid.ParameterName = "@cid";
        pCid.Value = conversationId;
        cmd.Parameters.Add(pCid);

        var pAfter = cmd.CreateParameter();
        pAfter.ParameterName = "@after";
        pAfter.Value = afterExclusive;
        cmd.Parameters.Add(pAfter);

        var pLimit = cmd.CreateParameter();
        pLimit.ParameterName = "@limit";
        pLimit.Value = limit;
        cmd.Parameters.Add(pLimit);

        var events = new List<ConversationEvent>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(MapFromReader(reader));
        }

        long? nextCursor = events.Count > 0 ? events[^1].Sequence : afterExclusive;
        var hasMore = events.Count >= limit;

        return new EventPage(events, nextCursor, hasMore);
    }

    public async Task<EventPage> ReadBackwardAsync(
        string conversationId,
        long beforeExclusive,
        int limit,
        CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM conversation_events
            WHERE conversation_id = @cid AND sequence < @before
            ORDER BY sequence DESC LIMIT @limit";
        var pCid = cmd.CreateParameter();
        pCid.ParameterName = "@cid";
        pCid.Value = conversationId;
        cmd.Parameters.Add(pCid);
        var pBefore = cmd.CreateParameter();
        pBefore.ParameterName = "@before";
        pBefore.Value = beforeExclusive;
        cmd.Parameters.Add(pBefore);
        var pLimit = cmd.CreateParameter();
        pLimit.ParameterName = "@limit";
        pLimit.Value = limit;
        cmd.Parameters.Add(pLimit);

        var events = new List<ConversationEvent>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(MapFromReader(reader));
        }
        events.Reverse(); // We queried DESC, return ASC

        long? nextCursor = events.Count > 0 ? events[0].Sequence : null;
        var hasMore = events.Count >= limit;

        return new EventPage(events, nextCursor, hasMore);
    }

    public async Task<EventPage> ReadByTypePrefixBackwardAsync(
        string conversationId,
        string typePrefix,
        long beforeExclusive,
        int limit,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typePrefix);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await EnsureTableAsync(ct);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM conversation_events
            WHERE conversation_id = @cid
              AND sequence < @before
              AND type LIKE @typePrefix
            ORDER BY sequence DESC LIMIT @limit";

        var pCid = cmd.CreateParameter();
        pCid.ParameterName = "@cid";
        pCid.Value = conversationId;
        cmd.Parameters.Add(pCid);

        var pBefore = cmd.CreateParameter();
        pBefore.ParameterName = "@before";
        pBefore.Value = beforeExclusive;
        cmd.Parameters.Add(pBefore);

        var pTypePrefix = cmd.CreateParameter();
        pTypePrefix.ParameterName = "@typePrefix";
        pTypePrefix.Value = typePrefix + "%";
        cmd.Parameters.Add(pTypePrefix);

        var pLimit = cmd.CreateParameter();
        pLimit.ParameterName = "@limit";
        pLimit.Value = limit;
        cmd.Parameters.Add(pLimit);

        var events = new List<ConversationEvent>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(MapFromReader(reader));

        events.Reverse();
        long? nextCursor = events.Count > 0 ? events[0].Sequence : null;
        return new EventPage(events, nextCursor, events.Count >= limit);
    }

    public async Task<EventBounds> GetBoundsAsync(
        string conversationId,
        CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MIN(sequence), MAX(sequence) FROM conversation_events
            WHERE conversation_id = @cid";
        var pCid = cmd.CreateParameter();
        pCid.ParameterName = "@cid";
        pCid.Value = conversationId;
        cmd.Parameters.Add(pCid);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var min = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
            var max = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            return new EventBounds(min, max);
        }
        return new EventBounds(null, null);
    }

    // ── Table management ───────────────────────────────────

    public async Task EnsureTablesAsync(CancellationToken ct)
    {
        await EnsureTableAsync(ct);
    }

    private async ValueTask EnsureTableAsync(CancellationToken ct)
    {
        if (_tableEnsured) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS conversation_heads (
                conversation_id TEXT PRIMARY KEY,
                head_sequence INTEGER NOT NULL DEFAULT 0
            )", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS conversation_events (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                event_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                turn_id TEXT NOT NULL,
                command_id TEXT,
                run_id TEXT,
                message_id TEXT,
                type TEXT NOT NULL,
                schema_version INTEGER NOT NULL DEFAULT 1,
                payload TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                correlation_id TEXT,
                causation_id TEXT,
                producer_event_id TEXT
            )", ct);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_ce_seq ON conversation_events(conversation_id, sequence)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_ce_eid ON conversation_events(event_id)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_ce_turn ON conversation_events(turn_id, type)", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS conversation_projection_checkpoints (
                conversation_id TEXT PRIMARY KEY,
                projected_through INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            )", ct);

        _tableEnsured = true;
        logger.LogInformation("[ConversationEventStore] Tables ensured");
    }

    // ── Mapping ────────────────────────────────────────────

    private static ConversationEvent MapFromReader(System.Data.Common.DbDataReader reader)
    {
        int Ord(string name) => reader.GetOrdinal(name);

        return new ConversationEvent
        {
            EventId = reader.GetString(Ord("event_id")),
            ConversationId = reader.GetString(Ord("conversation_id")),
            Sequence = reader.GetInt64(Ord("sequence")),
            WorkspaceId = reader.GetString(Ord("workspace_id")),
            TurnId = reader.GetString(Ord("turn_id")),
            CommandId = reader.IsDBNull(Ord("command_id")) ? null : reader.GetString(Ord("command_id")),
            RunId = reader.IsDBNull(Ord("run_id")) ? null : reader.GetString(Ord("run_id")),
            MessageId = reader.IsDBNull(Ord("message_id")) ? null : reader.GetString(Ord("message_id")),
            Type = reader.GetString(Ord("type")),
            SchemaVersion = reader.GetInt32(Ord("schema_version")),
            OccurredAt = DateTimeOffset.Parse(reader.GetString(Ord("occurred_at"))),
            CommittedAt = DateTimeOffset.Parse(reader.GetString(Ord("committed_at"))),
            CorrelationId = reader.IsDBNull(Ord("correlation_id")) ? null : reader.GetString(Ord("correlation_id")),
            CausationId = reader.IsDBNull(Ord("causation_id")) ? null : reader.GetString(Ord("causation_id")),
            ProducerEventId = reader.IsDBNull(Ord("producer_event_id")) ? null : reader.GetString(Ord("producer_event_id")),
            Payload = JsonDocument.Parse(reader.GetString(Ord("payload"))).RootElement,
        };
    }
}
