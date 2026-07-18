using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Execution;

/// <summary>
/// ADR-059: SQLite Execution Journal — unified fenced event writing + atomic terminal commit.
/// Replaces SqliteExecutionEventCommitter.
/// All writes carry the same ExecutionLease instance for fencing validation.
/// </summary>
public sealed class SqliteExecutionJournal(
    IServiceScopeFactory scopeFactory,
    ICommittedEventSignal signal,
    ILogger<SqliteExecutionJournal> logger) : IExecutionJournal
{
    public async Task<AppendResult> StartRunAsync(
        ExecutionLease lease,
        string snapshotId,
        NewConversationEvent startedEvent,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            // 1. Validate lease
            if (!await ValidateRunAsync(conn, tx, lease, ct))
                throw new InvalidOperationException(
                    $"StartRun fence rejected run={lease.RunId} cmd={lease.CommandId}");

            // 2. Run leased → running + snapshot
            using var runCmd = conn.CreateCommand();
            runCmd.Transaction = tx;
            runCmd.CommandText = @"
                UPDATE execution_runs
                SET status = 'running',
                    snapshot_id = @snapshotId,
                    started_at = @nowMs
                WHERE run_id = @runId
                  AND fencing_token = @fenceToken
                  AND worker_id = @workerId
                  AND status = 'leased'";
            AddParam(runCmd, "@runId", lease.RunId);
            AddParam(runCmd, "@fenceToken", lease.FencingToken);
            AddParam(runCmd, "@workerId", lease.WorkerId);
            AddParam(runCmd, "@snapshotId", snapshotId);
            AddParam(runCmd, "@nowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var runAffected = await runCmd.ExecuteNonQueryAsync(ct);
            if (runAffected != 1)
                throw new InvalidOperationException(
                    $"StartRun: Run update affected {runAffected} rows. Fence mismatch or not 'leased'.");

            // 3. Command leased → running
            using var cmdCmd = conn.CreateCommand();
            cmdCmd.Transaction = tx;
            cmdCmd.CommandText = @"
                UPDATE chat_execution_commands
                SET status = 'running'
                WHERE command_id = @commandId
                  AND status = 'leased'";
            AddParam(cmdCmd, "@commandId", lease.CommandId);
            var cmdAffected = await cmdCmd.ExecuteNonQueryAsync(ct);
            if (cmdAffected != 1)
                throw new InvalidOperationException(
                    $"StartRun: Command update affected {cmdAffected} rows.");

            // 4. Turn accepted → running
            using var turnCmd = conn.CreateCommand();
            turnCmd.Transaction = tx;
            turnCmd.CommandText = @"
                UPDATE conversation_turns
                SET status = 'running'
                WHERE turn_id = @turnId
                  AND status = 'accepted'";
            AddParam(turnCmd, "@turnId", lease.TurnId);
            var turnAffected = await turnCmd.ExecuteNonQueryAsync(ct);
            if (turnAffected != 1)
                throw new InvalidOperationException(
                    $"StartRun: Turn update affected {turnAffected} rows.");

            // 5. Write turn.started event
            var result = await AppendEventsInternalAsync(
                conn, tx, lease, [startedEvent], ct);

            await tx.CommitAsync(ct);
            signal.Signal(lease.ConversationId, result.LastSequence);

            logger.LogInformation(
                "[Journal] StartRun run={RunId} cmd={CmdId} snapshot={SnapshotId} seq={Seq}",
                lease.RunId, lease.CommandId, snapshotId, result.LastSequence);

            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<AppendResult> AppendOutputAsync(
        ExecutionLease lease,
        IReadOnlyList<NewConversationEvent> events,
        CancellationToken ct)
    {
        if (events.Count == 0)
            return new AppendResult(0, 0, 0);

        // Reject terminal events in output path
        if (events.Any(e => IsTerminalType(e.Type)))
            throw new InvalidOperationException(
                "AppendOutputAsync rejects terminal events. Use CommitTerminalAsync.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            if (!await ValidateRunAsync(conn, tx, lease, ct))
                throw new InvalidOperationException(
                    $"Output fence rejected run={lease.RunId} cmd={lease.CommandId}");

            var result = await AppendEventsInternalAsync(conn, tx, lease, events, ct);
            await tx.CommitAsync(ct);
            signal.Signal(lease.ConversationId, result.LastSequence);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<AppendResult> CommitTerminalAsync(
        ExecutionLease lease,
        TurnTerminal terminal,
        IReadOnlyList<NewConversationEvent> pendingEvents,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            if (!await ValidateRunAsync(conn, tx, lease, ct))
                throw new InvalidOperationException(
                    $"Terminal fence rejected run={lease.RunId} cmd={lease.CommandId}");

            // Verify Turn not already terminal
            if (!await VerifyTurnNotTerminalAsync(conn, tx, lease.TurnId, ct))
                throw new InvalidOperationException(
                    $"Turn {lease.TurnId} already has a terminal event.");

            // 1. Write pending events first
            long lastSeq = 0;
            int writtenCount = 0;
            if (pendingEvents.Count > 0)
            {
                var pendingResult = await AppendEventsInternalAsync(
                    conn, tx, lease, pendingEvents, ct);
                lastSeq = pendingResult.LastSequence;
                writtenCount = pendingResult.Count;
            }

            // 2. Write terminal event
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();
            var committedAt = now.ToString("O");

            var terminalEvent = new NewConversationEvent(
                EventId: Guid.NewGuid().ToString("N"),
                Type: terminal.TerminalEventType,
                SchemaVersion: 1,
                WorkspaceId: lease.WorkspaceId,
                TurnId: lease.TurnId,
                CommandId: lease.CommandId,
                RunId: lease.RunId,
                MessageId: null,
                CorrelationId: lease.ConversationId,
                CausationId: lease.TurnId,
                ProducerEventId: null,
                Payload: BuildTerminalPayload(terminal));

            var terminalResult = await AppendEventsInternalAsync(
                conn, tx, lease, [terminalEvent], ct);
            lastSeq = terminalResult.LastSequence;
            writtenCount += terminalResult.Count;

            // 3. Update Turn
            using var turnCmd = conn.CreateCommand();
            turnCmd.Transaction = tx;
            turnCmd.CommandText = @"
                UPDATE conversation_turns
                SET status = @status,
                    terminal_sequence = @termSeq,
                    terminal_kind = @termKind,
                    completed_at = @completedAt
                WHERE turn_id = @turnId
                  AND status = 'running'";
            AddParam(turnCmd, "@status", TurnStatusToString(terminal));
            AddParam(turnCmd, "@termSeq", lastSeq);
            AddParam(turnCmd, "@termKind", terminal.Kind.ToString().ToLowerInvariant());
            AddParam(turnCmd, "@completedAt", nowMs);
            AddParam(turnCmd, "@turnId", lease.TurnId);
            var turnAffected = await turnCmd.ExecuteNonQueryAsync(ct);
            if (turnAffected != 1)
                throw new InvalidOperationException(
                    $"CommitTerminal: Turn update affected {turnAffected} rows.");

            // 4. Update ExecutionRun
            using var runCmd = conn.CreateCommand();
            runCmd.Transaction = tx;
            runCmd.CommandText = @"
                UPDATE execution_runs
                SET status = @status,
                    terminal_sequence = @termSeq,
                    completed_at = @completedAt
                WHERE run_id = @runId
                  AND fencing_token = @fenceToken
                  AND worker_id = @workerId
                  AND status IN ('running', 'cancel_requested')";
            AddParam(runCmd, "@status", RunStatusToString(terminal.RunStatus));
            AddParam(runCmd, "@termSeq", lastSeq);
            AddParam(runCmd, "@completedAt", nowMs);
            AddParam(runCmd, "@runId", lease.RunId);
            AddParam(runCmd, "@fenceToken", lease.FencingToken);
            AddParam(runCmd, "@workerId", lease.WorkerId);
            var runAffected = await runCmd.ExecuteNonQueryAsync(ct);
            if (runAffected != 1)
                throw new InvalidOperationException(
                    $"CommitTerminal: Run update affected {runAffected} rows. Fence may be lost.");

            // 5. Update Command
            using var cmdCmd = conn.CreateCommand();
            cmdCmd.Transaction = tx;
            cmdCmd.CommandText = @"
                UPDATE chat_execution_commands
                SET status = @status,
                    terminal_sequence = @termSeq,
                    completed_at = @completedAt,
                    lease_owner = NULL,
                    lease_until = NULL
                WHERE command_id = @commandId
                  AND status IN ('running', 'cancel_requested')";
            AddParam(cmdCmd, "@status", CommandStatusToString(terminal.CommandStatus));
            AddParam(cmdCmd, "@termSeq", lastSeq);
            AddParam(cmdCmd, "@completedAt", nowMs);
            AddParam(cmdCmd, "@commandId", lease.CommandId);
            var cmdAffected = await cmdCmd.ExecuteNonQueryAsync(ct);
            if (cmdAffected != 1)
                throw new InvalidOperationException(
                    $"CommitTerminal: Command update affected {cmdAffected} rows.");

            await tx.CommitAsync(ct);
            signal.Signal(lease.ConversationId, lastSeq);

            logger.LogInformation(
                "[Journal] Terminal run={RunId} cmd={CmdId} kind={Kind} seq={Seq}",
                lease.RunId, lease.CommandId, terminal.Kind, lastSeq);

            return new AppendResult(
                pendingEvents.Count > 0 ? lastSeq - writtenCount + 1 : lastSeq,
                lastSeq, writtenCount);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<bool> ValidateRunAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        ExecutionLease lease,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT r.run_id, r.worker_id, r.fencing_token, r.status, r.lease_until
            FROM execution_runs r
            WHERE r.run_id = @runId";
        AddParam(cmd, "@runId", lease.RunId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;

        var storedRunId = reader.GetString(0);
        var storedWorkerId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var storedFence = reader.GetInt64(2);
        var storedStatus = reader.GetString(3);
        var leaseUntil = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);

        await reader.CloseAsync();

        if (storedRunId != lease.RunId) return false;
        if (storedWorkerId != lease.WorkerId) return false;
        if (storedFence != lease.FencingToken) return false;
        if (storedStatus != "leased" && storedStatus != "running" &&
            storedStatus != "cancel_requested") return false;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (leaseUntil > 0 && leaseUntil < nowMs) return false;

        return true;
    }

    private static async Task<bool> VerifyTurnNotTerminalAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        string turnId,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT terminal_sequence FROM conversation_turns
            WHERE turn_id = @turnId";
        AddParam(cmd, "@turnId", turnId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull || result is null;
    }

    private static async Task<AppendResult> AppendEventsInternalAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        ExecutionLease lease,
        IReadOnlyList<NewConversationEvent> events,
        CancellationToken ct)
    {
        var committedAt = DateTimeOffset.UtcNow.ToString("O");

        using var headCmd = conn.CreateCommand();
        headCmd.Transaction = tx;
        headCmd.CommandText = @"
            INSERT OR IGNORE INTO conversation_heads (conversation_id, head_sequence)
            VALUES (@convId, 0)";
        AddParam(headCmd, "@convId", lease.ConversationId);
        await headCmd.ExecuteNonQueryAsync(ct);

        using var getCmd = conn.CreateCommand();
        getCmd.Transaction = tx;
        getCmd.CommandText = @"
            SELECT head_sequence FROM conversation_heads
            WHERE conversation_id = @convId";
        AddParam(getCmd, "@convId", lease.ConversationId);
        var currentHead = (long)(await getCmd.ExecuteScalarAsync(ct) ?? 0L);

        long firstSeq = currentHead + 1;

        foreach (var evt in events)
        {
            currentHead++;
            using var insCmd = conn.CreateCommand();
            insCmd.Transaction = tx;
            insCmd.CommandText = @"
                INSERT INTO conversation_events
                (conversation_id, sequence, event_id, workspace_id, turn_id,
                 command_id, run_id, message_id, type, schema_version,
                 payload, occurred_at, committed_at, correlation_id, causation_id)
                VALUES
                (@cid, @seq, @eid, @wid, @tid,
                 @cmid, @rid, @mid, @type, @sv,
                 @payload, @oat, @cat, @corr, @caus)";
            AddParam(insCmd, "@cid", lease.ConversationId);
            AddParam(insCmd, "@seq", currentHead);
            AddParam(insCmd, "@eid", evt.EventId);
            AddParam(insCmd, "@wid", evt.WorkspaceId ?? "");
            AddParam(insCmd, "@tid", evt.TurnId ?? "");
            AddParam(insCmd, "@cmid", evt.CommandId ?? "");
            AddParam(insCmd, "@rid", evt.RunId ?? lease.RunId);
            AddParam(insCmd, "@mid", evt.MessageId ?? "");
            AddParam(insCmd, "@type", evt.Type);
            AddParam(insCmd, "@sv", evt.SchemaVersion);
            AddParam(insCmd, "@payload", evt.Payload.GetRawText());
            AddParam(insCmd, "@oat", committedAt);
            AddParam(insCmd, "@cat", committedAt);
            AddParam(insCmd, "@corr", evt.CorrelationId ?? "");
            AddParam(insCmd, "@caus", evt.CausationId ?? "");
            await insCmd.ExecuteNonQueryAsync(ct);
        }

        using var updCmd = conn.CreateCommand();
        updCmd.Transaction = tx;
        updCmd.CommandText = @"
            UPDATE conversation_heads
            SET head_sequence = @head
            WHERE conversation_id = @convId";
        AddParam(updCmd, "@head", currentHead);
        AddParam(updCmd, "@convId", lease.ConversationId);
        await updCmd.ExecuteNonQueryAsync(ct);

        return new AppendResult(firstSeq, currentHead, events.Count);
    }

    private static System.Text.Json.JsonElement BuildTerminalPayload(TurnTerminal terminal)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            $$"""{"kind":"{{terminal.Kind}}","errorCode":{{JsonOrNull(terminal.ErrorCode)}},"errorMessage":{{JsonOrNull(terminal.ErrorMessage)}},"reply":{{JsonOrNull(terminal.Reply)}}}""");
        return doc.RootElement.Clone();
    }

    private static string JsonOrNull(string? value) =>
        value is null ? "null" : $"\"{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(value)}\"";

    private static bool IsTerminalType(string eventType) =>
        eventType == ConversationEventTypes.TurnCompleted ||
        eventType == ConversationEventTypes.TurnFailed ||
        eventType == ConversationEventTypes.TurnCancelled;

    private static string TurnStatusToString(TurnTerminal terminal) => terminal.Kind switch
    {
        TurnTerminalKind.Completed => "completed",
        TurnTerminalKind.Failed => "failed",
        TurnTerminalKind.Cancelled => "cancelled",
        _ => "failed",
    };

    private static string RunStatusToString(RunStatus s) => s switch
    {
        RunStatus.Succeeded => "succeeded",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    private static string CommandStatusToString(CommandStatus s) => s switch
    {
        CommandStatus.Succeeded => "succeeded",
        CommandStatus.Failed => "failed",
        CommandStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
