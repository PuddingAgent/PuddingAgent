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
    private const int OpenAttemptLimit = 3;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// P0-4f-1a 步骤5「Journal 守门人」：execution 域事实事件的固定 producer_component。
    /// 用户拍板的三值之一：chat.acceptance / execution.journal / subagent.runtime。
    /// </summary>
    private const string JournalProducerComponent = "execution.journal";

    /// <summary>
    /// A01-slice-4c：父 Turn park 后的非终态取值。13 字符，满足 execution_runs.status / conversation_turns.status
    /// 的 MaxLength(16) 约束；不新增列、不改列长度。
    /// 语义：本 Turn 的 LLM 循环已结束，但本 Turn 仍有 running 子代理，终态提交被推迟到最后一个子代理收口。
    /// </summary>
    private const string WaitingChildStatus = "waiting_child";

    /// <summary>命令 metadata_json 中承载「待提交终态」的键（复用既有 JSON 列，不新增列）。</summary>
    private const string ParkedTerminalKey = "parked_terminal";
    public async Task<AppendResult> StartRunAsync(
        ExecutionLease lease,
        string snapshotId,
        NewConversationEvent startedEvent,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await OpenConnectionAsync(conn, lease, ct);

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
        await OpenConnectionAsync(conn, lease, ct);

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
        var allowPrestartCancel = terminal.Kind == TurnTerminalKind.Cancelled && pendingEvents.Count == 0;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await OpenConnectionAsync(conn, lease, ct);

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
            var assistantMessageId = await ReadAssistantMessageIdAsync(
                conn, tx, lease.CommandId, ct);

            var terminalEvent = new NewConversationEvent(
                EventId: Guid.NewGuid().ToString("N"),
                Type: terminal.TerminalEventType,
                SchemaVersion: 1,
                WorkspaceId: lease.WorkspaceId,
                TurnId: lease.TurnId,
                CommandId: lease.CommandId,
                RunId: lease.RunId,
                MessageId: assistantMessageId,
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
                  AND (status = 'running' OR (@allowPrestartCancel = 1 AND status = 'accepted'))";
            AddParam(turnCmd, "@allowPrestartCancel", allowPrestartCancel);
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
                  AND (status IN ('running', 'cancel_requested') OR (@allowPrestartCancel = 1 AND status = 'leased'))";
            AddParam(runCmd, "@allowPrestartCancel", allowPrestartCancel);
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
                  AND (status IN ('running', 'cancel_requested') OR (@allowPrestartCancel = 1 AND status = 'leased'))";
            AddParam(cmdCmd, "@allowPrestartCancel", allowPrestartCancel);
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

    /// <summary>
    /// A01-slice-4c：父 Turn park —— 本 Turn 仍有 running 子代理时，把 Turn/Run/Command 收敛为非终态
    /// waiting_child，并 flush 本 Turn 的非终态 pending 输出。同一事务内：
    ///   1. 校验 Run（runId + workerId + fencingToken + status = running）；不校验 lease_until，
    ///      因为 park 的语义就是停止续租、改由 waiting_child 状态承担判活（回收扫描不再命中该行）。
    ///   2. 写入 pending 非终态输出事件。
    ///   3. Turn running → waiting_child（CAS）。
    ///   4. Run running → waiting_child（释放租约，不写 completed_at / terminal_sequence）。
    ///   5. Command running｜cancel_requested → waiting_child（释放租约）。
    ///   6. 把待提交终态持久化进命令 metadata_json（merged，保留既有键），使收口不依赖进程内状态。
    /// 不写任何 terminal 事件、不写业务 completed；任一 CAS 未命中即整事务回滚并返回 null。
    /// </summary>
    public async Task<ExecutionParkResult?> ParkForChildrenAsync(
        ExecutionLease lease,
        TurnTerminal deferredTerminal,
        IReadOnlyList<NewConversationEvent> pendingEvents,
        CancellationToken ct)
    {
        if (deferredTerminal.Kind != TurnTerminalKind.Completed)
            throw new ArgumentException(
                "Park only accepts a completed terminal.",
                nameof(deferredTerminal));
        if (pendingEvents.Any(e => IsTerminalType(e.Type)))
            throw new InvalidOperationException(
                "ParkForChildrenAsync rejects terminal events. Use CommitTerminalAsync.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await OpenConnectionAsync(conn, lease, ct);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            var runMatches = false;
            using (var guardCmd = conn.CreateCommand())
            {
                guardCmd.Transaction = tx;
                guardCmd.CommandText = @"
                    SELECT worker_id, fencing_token, status
                    FROM execution_runs
                    WHERE run_id = @runId";
                AddParam(guardCmd, "@runId", lease.RunId);
                using var reader = await guardCmd.ExecuteReaderAsync(ct);
                runMatches = await reader.ReadAsync(ct)
                    && reader.GetString(0) == lease.WorkerId
                    && reader.GetInt64(1) == lease.FencingToken
                    && reader.GetString(2) == "running";
            }

            if (!runMatches)
            {
                await tx.RollbackAsync(ct);
                logger.LogWarning(
                    "[Journal] Park rejected run={RunId} fence={Fence}",
                    lease.RunId,
                    lease.FencingToken);
                return null;
            }

            var parkedJson = MergeParkedTerminalJson(
                await ReadCommandMetadataJsonAsync(conn, tx, lease.CommandId, ct),
                deferredTerminal);

            long lastSeq = 0;
            if (pendingEvents.Count > 0)
            {
                var pendingResult = await AppendEventsInternalAsync(
                    conn, tx, lease, pendingEvents, ct);
                lastSeq = pendingResult.LastSequence;
            }

            using (var turnCmd = conn.CreateCommand())
            {
                turnCmd.Transaction = tx;
                turnCmd.CommandText = @"
                    UPDATE conversation_turns
                    SET status = @status
                    WHERE turn_id = @turnId
                      AND status = 'running'
                      AND terminal_sequence IS NULL";
                AddParam(turnCmd, "@status", WaitingChildStatus);
                AddParam(turnCmd, "@turnId", lease.TurnId);
                var turnAffected = await turnCmd.ExecuteNonQueryAsync(ct);
                if (turnAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogWarning(
                        "[Journal] Park rejected turn={TurnId} rows={Rows}",
                        lease.TurnId,
                        turnAffected);
                    return null;
                }
            }

            using (var runCmd = conn.CreateCommand())
            {
                runCmd.Transaction = tx;
                runCmd.CommandText = @"
                    UPDATE execution_runs
                    SET status = @status,
                        lease_until = NULL
                    WHERE run_id = @runId
                      AND fencing_token = @fenceToken
                      AND worker_id = @workerId
                      AND status = 'running'";
                AddParam(runCmd, "@status", WaitingChildStatus);
                AddParam(runCmd, "@runId", lease.RunId);
                AddParam(runCmd, "@fenceToken", lease.FencingToken);
                AddParam(runCmd, "@workerId", lease.WorkerId);
                var runAffected = await runCmd.ExecuteNonQueryAsync(ct);
                if (runAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogWarning(
                        "[Journal] Park rejected run update run={RunId} rows={Rows}",
                        lease.RunId,
                        runAffected);
                    return null;
                }
            }

            using (var cmdCmd = conn.CreateCommand())
            {
                cmdCmd.Transaction = tx;
                cmdCmd.CommandText = @"
                    UPDATE chat_execution_commands
                    SET status = @status,
                        metadata_json = @metadataJson,
                        lease_owner = NULL,
                        lease_until = NULL
                    WHERE command_id = @commandId
                      AND status IN ('running', 'cancel_requested')";
                AddParam(cmdCmd, "@status", WaitingChildStatus);
                AddParam(cmdCmd, "@metadataJson", parkedJson);
                AddParam(cmdCmd, "@commandId", lease.CommandId);
                var cmdAffected = await cmdCmd.ExecuteNonQueryAsync(ct);
                if (cmdAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogWarning(
                        "[Journal] Park rejected command={CommandId} rows={Rows}",
                        lease.CommandId,
                        cmdAffected);
                    return null;
                }
            }

            await tx.CommitAsync(ct);
            if (pendingEvents.Count > 0)
                signal.Signal(lease.ConversationId, lastSeq);

            logger.LogInformation(
                "[Journal] Parked run={RunId} turn={TurnId} cmd={CmdId} seq={Seq}",
                lease.RunId, lease.TurnId, lease.CommandId, lastSeq);

            return new ExecutionParkResult(lastSeq, pendingEvents.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// A01-slice-4c：唤醒收口 —— 父 Turn 已无 running 子代理时，把 park 的父 Turn 收敛为终态。
    /// 以 WHERE status = 'waiting_child' 的 CAS 抢占唯一收口权：并发或重复触发只允许一次成功，
    /// 其余调用返回 null（绝不写第二个终态事件）。终态事件与 park 时持久化的待提交终态逐字节一致。
    /// </summary>
    public async Task<ExecutionParkFinalizeResult?> TryFinalizeWaitingTurnAsync(
        string parentTurnId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentTurnId))
            return null;

        var turnId = parentTurnId.Trim();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            string runId;
            string conversationId;
            string commandId;
            string workerId;
            string workspaceId;
            long fencingToken;
            string? metadataJson;
            string? assistantMessageId;
            string? traceId;

            using (var selCmd = conn.CreateCommand())
            {
                selCmd.Transaction = tx;
                selCmd.CommandText = @"
                    SELECT r.run_id, r.conversation_id, r.command_id, r.worker_id, r.fencing_token,
                           t.workspace_id, c.metadata_json, c.message_id, c.trace_id
                    FROM execution_runs r
                    JOIN conversation_turns t ON t.turn_id = r.turn_id
                    JOIN chat_execution_commands c ON c.command_id = r.command_id
                    WHERE r.turn_id = @turnId
                      AND r.status = @parked
                      AND t.status = @parked
                    LIMIT 1";
                AddParam(selCmd, "@turnId", turnId);
                AddParam(selCmd, "@parked", WaitingChildStatus);
                using var reader = await selCmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    await tx.RollbackAsync(ct);
                    return null;
                }

                runId = reader.GetString(0);
                conversationId = reader.GetString(1);
                commandId = reader.GetString(2);
                workerId = reader.GetString(3);
                fencingToken = reader.GetInt64(4);
                workspaceId = reader.GetString(5);
                metadataJson = reader.IsDBNull(6) ? null : reader.GetString(6);
                assistantMessageId = reader.IsDBNull(7) ? null : reader.GetString(7);
                traceId = reader.IsDBNull(8) ? null : reader.GetString(8);
            }

            if (!TryReadParkedTerminal(metadataJson, out var parked))
            {
                await tx.RollbackAsync(ct);
                logger.LogWarning(
                    "[Journal] Finalize skipped — no parked terminal turn={TurnId}", turnId);
                return null;
            }

            var lease = new ExecutionLease(
                commandId,
                workerId,
                workspaceId,
                conversationId,
                turnId,
                runId,
                fencingToken,
                DateTimeOffset.UtcNow.AddMinutes(2))
            {
                TraceId = traceId,
            };

            var terminalEvent = new NewConversationEvent(
                EventId: Guid.NewGuid().ToString("N"),
                Type: parked.TerminalEventType,
                SchemaVersion: 1,
                WorkspaceId: workspaceId,
                TurnId: turnId,
                CommandId: commandId,
                RunId: runId,
                MessageId: assistantMessageId,
                CorrelationId: conversationId,
                CausationId: turnId,
                ProducerEventId: null,
                Payload: parked.Payload);

            var appendResult = await AppendEventsInternalAsync(
                conn, tx, lease, [terminalEvent], ct);
            var lastSeq = appendResult.LastSequence;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using (var turnCmd = conn.CreateCommand())
            {
                turnCmd.Transaction = tx;
                turnCmd.CommandText = @"
                    UPDATE conversation_turns
                    SET status = @status,
                        terminal_sequence = @termSeq,
                        terminal_kind = @termKind,
                        completed_at = @completedAt
                    WHERE turn_id = @turnId
                      AND status = @parked
                      AND terminal_sequence IS NULL";
                AddParam(turnCmd, "@status", parked.TurnStatus);
                AddParam(turnCmd, "@termSeq", lastSeq);
                AddParam(turnCmd, "@termKind", parked.TerminalKind);
                AddParam(turnCmd, "@completedAt", nowMs);
                AddParam(turnCmd, "@turnId", turnId);
                AddParam(turnCmd, "@parked", WaitingChildStatus);
                var turnAffected = await turnCmd.ExecuteNonQueryAsync(ct);
                if (turnAffected != 1)
                {
                    // 并发收口：另一个调用已写入终态。回滚本次事件，绝不写第二个终态事实。
                    await tx.RollbackAsync(ct);
                    logger.LogInformation(
                        "[Journal] Finalize lost CAS turn={TurnId} rows={Rows}",
                        turnId,
                        turnAffected);
                    return null;
                }
            }

            using (var runCmd = conn.CreateCommand())
            {
                runCmd.Transaction = tx;
                runCmd.CommandText = @"
                    UPDATE execution_runs
                    SET status = @status,
                        terminal_sequence = @termSeq,
                        completed_at = @completedAt
                    WHERE run_id = @runId
                      AND status = @parked";
                AddParam(runCmd, "@status", parked.RunStatus);
                AddParam(runCmd, "@termSeq", lastSeq);
                AddParam(runCmd, "@completedAt", nowMs);
                AddParam(runCmd, "@runId", runId);
                AddParam(runCmd, "@parked", WaitingChildStatus);
                var runAffected = await runCmd.ExecuteNonQueryAsync(ct);
                if (runAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogWarning(
                        "[Journal] Finalize run CAS lost run={RunId} rows={Rows}",
                        runId,
                        runAffected);
                    return null;
                }
            }

            using (var cmdCmd = conn.CreateCommand())
            {
                cmdCmd.Transaction = tx;
                cmdCmd.CommandText = @"
                    UPDATE chat_execution_commands
                    SET status = @status,
                        terminal_sequence = @termSeq,
                        completed_at = @completedAt,
                        lease_owner = NULL,
                        lease_until = NULL
                    WHERE command_id = @commandId
                      AND status = @parked";
                AddParam(cmdCmd, "@status", parked.CommandStatus);
                AddParam(cmdCmd, "@termSeq", lastSeq);
                AddParam(cmdCmd, "@completedAt", nowMs);
                AddParam(cmdCmd, "@commandId", commandId);
                AddParam(cmdCmd, "@parked", WaitingChildStatus);
                var cmdAffected = await cmdCmd.ExecuteNonQueryAsync(ct);
                if (cmdAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogWarning(
                        "[Journal] Finalize command CAS lost cmd={CommandId} rows={Rows}",
                        commandId,
                        cmdAffected);
                    return null;
                }
            }

            await tx.CommitAsync(ct);
            signal.Signal(conversationId, lastSeq);

            logger.LogInformation(
                "[Journal] Finalized waiting turn={TurnId} run={RunId} kind={Kind} seq={Seq}",
                turnId, runId, parked.TerminalKind, lastSeq);

            return new ExecutionParkFinalizeResult(
                turnId, runId, lastSeq, parked.TerminalKind);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<AppendResult?> TryCommitInfrastructureFailureAsync(
        ExecutionLease lease,
        TurnTerminal terminal,
        IReadOnlyList<NewConversationEvent> pendingEvents,
        CancellationToken ct)
    {
        if (terminal.Kind != TurnTerminalKind.Failed)
            throw new ArgumentException("Infrastructure fallback only accepts a failed terminal.", nameof(terminal));

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await OpenConnectionAsync(conn, lease, ct);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            if (!await ValidateRunAsync(conn, tx, lease, ct)
                || !await VerifyTurnNotTerminalAsync(conn, tx, lease.TurnId, ct))
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var events = pendingEvents
                .Where(e => !IsTerminalType(e.Type))
                .ToList();
            var assistantMessageId = await ReadAssistantMessageIdAsync(
                conn, tx, lease.CommandId, ct);
            events.Add(new NewConversationEvent(
                EventId: Guid.NewGuid().ToString("N"),
                Type: terminal.TerminalEventType,
                SchemaVersion: 1,
                WorkspaceId: lease.WorkspaceId,
                TurnId: lease.TurnId,
                CommandId: lease.CommandId,
                RunId: lease.RunId,
                MessageId: assistantMessageId,
                CorrelationId: lease.ConversationId,
                CausationId: lease.TurnId,
                ProducerEventId: null,
                Payload: BuildTerminalPayload(terminal)));

            var result = await AppendEventsInternalAsync(conn, tx, lease, events, ct);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var turnCmd = conn.CreateCommand();
            turnCmd.Transaction = tx;
            turnCmd.CommandText = @"
                UPDATE conversation_turns
                SET status = 'failed',
                    terminal_sequence = @termSeq,
                    terminal_kind = 'failed',
                    completed_at = @completedAt
                WHERE turn_id = @turnId
                  AND status IN ('accepted', 'running')";
            AddParam(turnCmd, "@termSeq", result.LastSequence);
            AddParam(turnCmd, "@completedAt", nowMs);
            AddParam(turnCmd, "@turnId", lease.TurnId);

            using var runCmd = conn.CreateCommand();
            runCmd.Transaction = tx;
            runCmd.CommandText = @"
                UPDATE execution_runs
                SET status = 'failed',
                    terminal_sequence = @termSeq,
                    completed_at = @completedAt
                WHERE run_id = @runId
                  AND fencing_token = @fenceToken
                  AND worker_id = @workerId
                  AND status IN ('leased', 'running', 'cancel_requested')";
            AddParam(runCmd, "@termSeq", result.LastSequence);
            AddParam(runCmd, "@completedAt", nowMs);
            AddParam(runCmd, "@runId", lease.RunId);
            AddParam(runCmd, "@fenceToken", lease.FencingToken);
            AddParam(runCmd, "@workerId", lease.WorkerId);

            using var commandCmd = conn.CreateCommand();
            commandCmd.Transaction = tx;
            commandCmd.CommandText = @"
                UPDATE chat_execution_commands
                SET status = 'failed',
                    terminal_sequence = @termSeq,
                    completed_at = @completedAt,
                    lease_owner = NULL,
                    lease_until = NULL,
                    last_error = @lastError
                WHERE command_id = @commandId
                  AND status IN ('leased', 'running', 'cancel_requested')";
            AddParam(commandCmd, "@termSeq", result.LastSequence);
            AddParam(commandCmd, "@completedAt", nowMs);
            AddParam(commandCmd, "@lastError", $"{terminal.ErrorCode}: {terminal.ErrorMessage}");
            AddParam(commandCmd, "@commandId", lease.CommandId);

            var turnAffected = await turnCmd.ExecuteNonQueryAsync(ct);
            var runAffected = await runCmd.ExecuteNonQueryAsync(ct);
            var commandAffected = await commandCmd.ExecuteNonQueryAsync(ct);
            if (turnAffected != 1 || runAffected != 1 || commandAffected != 1)
                throw new InvalidOperationException(
                    $"Infrastructure failure transition rejected turn={turnAffected} run={runAffected} command={commandAffected}.");

            await tx.CommitAsync(ct);
            signal.Signal(lease.ConversationId, result.LastSequence);
            logger.LogError(
                "[Journal] Infrastructure failure committed run={RunId} command={CommandId} seq={Sequence}",
                lease.RunId, lease.CommandId, result.LastSequence);
            return result;
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

    private static async Task<string?> ReadAssistantMessageIdAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        string commandId,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT message_id
            FROM chat_execution_commands
            WHERE command_id = @commandId";
        AddParam(cmd, "@commandId", commandId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result);
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
                 payload, occurred_at, committed_at, correlation_id, causation_id,
                 producer_event_id, agent_id, source_kind, trace_id, producer_component)
                VALUES
                (@cid, @seq, @eid, @wid, @tid,
                 @cmid, @rid, @mid, @type, @sv,
                 @payload, @oat, @cat, @corr, @caus, @peid, @aid, @skind, @traceid, @pcomp)";
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
            AddParam(insCmd, "@peid", evt.ProducerEventId ?? (object)DBNull.Value);
            AddParam(insCmd, "@aid", evt.AgentId ?? (object)DBNull.Value);
            AddParam(insCmd, "@skind", evt.SourceKind?.ToString().ToLowerInvariant() ?? (object)DBNull.Value);
            AddParam(insCmd, "@traceid", string.IsNullOrWhiteSpace(lease.TraceId) ? (object)DBNull.Value : lease.TraceId);
            AddParam(insCmd, "@pcomp", JournalProducerComponent);
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

    /// <summary>
    /// A1（G92-1 S1-c 片6）：proposal 以独立键 goal_contract_proposal 写入 turn.completed payload
    /// （对象内 camelCase，与段1 parser 输入形状一致，round-trip 可再解析）；无 proposal 时写 null。
    /// 既有键（kind/errorCode/errorMessage/reply）保持不变，reply 原文不被污染。
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions GoalContractProposalPayloadOptions =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private static System.Text.Json.JsonElement BuildTerminalPayload(TurnTerminal terminal)
    {
        var proposalJson = terminal.GoalContractProposal is null
            ? "null"
            : System.Text.Json.JsonSerializer.Serialize(
                terminal.GoalContractProposal,
                GoalContractProposalPayloadOptions);
        using var doc = System.Text.Json.JsonDocument.Parse(
            $$"""{"kind":"{{terminal.Kind}}","errorCode":{{JsonOrNull(terminal.ErrorCode)}},"errorMessage":{{JsonOrNull(terminal.ErrorMessage)}},"reply":{{JsonOrNull(terminal.Reply)}},"goal_contract_proposal":{{proposalJson}}}""");
        return doc.RootElement.Clone();
    }

    private static string JsonOrNull(string? value) =>
        value is null ? "null" : $"\"{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(value)}\"";

    private static bool IsTerminalType(string eventType) =>
        eventType == ConversationEventTypes.TurnCompleted ||
        eventType == ConversationEventTypes.TurnFailed ||
        eventType == ConversationEventTypes.TurnCancelled;

    private static async Task<string?> ReadCommandMetadataJsonAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        string commandId,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT metadata_json
            FROM chat_execution_commands
            WHERE command_id = @commandId";
        AddParam(cmd, "@commandId", commandId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    /// <summary>
    /// 把「待提交终态」并入命令 metadata_json（保留既有键）：终态状态串、事件类型与事件 payload
    /// 全部取自即将提交的 TurnTerminal，因此收口时写出的事件与正常终态提交逐字节一致。
    /// 既有 metadata 不可解析时 fail-closed 抛错，由调用方整事务回滚并回退常规终态提交。
    /// </summary>
    private static string MergeParkedTerminalJson(string? existingJson, TurnTerminal terminal)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            Dictionary<string, string>? existing;
            try
            {
                existing = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(existingJson);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException(
                    "Command metadata_json is not a flat string map; park is rejected.", ex);
            }

            if (existing is not null)
                foreach (var pair in existing)
                    metadata[pair.Key] = pair.Value;
        }

        metadata[ParkedTerminalKey] = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["turn_status"] = TurnStatusToString(terminal),
                ["run_status"] = RunStatusToString(terminal.RunStatus),
                ["command_status"] = CommandStatusToString(terminal.CommandStatus),
                ["terminal_event_type"] = terminal.TerminalEventType,
                ["terminal_kind"] = terminal.Kind.ToString().ToLowerInvariant(),
                ["payload"] = BuildTerminalPayload(terminal).GetRawText(),
            });

        return System.Text.Json.JsonSerializer.Serialize(metadata);
    }

    private static bool TryReadParkedTerminal(string? metadataJson, out ParkedTerminalValue parked)
    {
        parked = default;
        if (string.IsNullOrWhiteSpace(metadataJson))
            return false;

        Dictionary<string, string>? metadata;
        try
        {
            metadata = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(metadataJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (metadata is null || !metadata.TryGetValue(ParkedTerminalKey, out var parkedJson))
            return false;

        Dictionary<string, string>? parkedFields;
        try
        {
            parkedFields = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(parkedJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (parkedFields is null
            || !parkedFields.TryGetValue("payload", out var payloadText))
            return false;

        using var doc = System.Text.Json.JsonDocument.Parse(payloadText);
        parked = new ParkedTerminalValue(
            parkedFields["turn_status"],
            parkedFields["run_status"],
            parkedFields["command_status"],
            parkedFields["terminal_event_type"],
            parkedFields["terminal_kind"],
            doc.RootElement.Clone());
        return true;
    }

    private readonly record struct ParkedTerminalValue(
        string TurnStatus,
        string RunStatus,
        string CommandStatus,
        string TerminalEventType,
        string TerminalKind,
        System.Text.Json.JsonElement Payload);

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

    private async Task OpenConnectionAsync(
        System.Data.Common.DbConnection connection,
        ExecutionLease lease,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= OpenAttemptLimit; attempt++)
        {
            try
            {
                await connection.OpenAsync(ct);
                return;
            }
            catch (SqliteException ex) when (
                attempt < OpenAttemptLimit && IsPooledConnectionActivationFailure(ex))
            {
                // No transaction or event write exists at this point, so retrying
                // cannot duplicate a committed journal fact. Drop the affected
                // pool to avoid reusing a handle whose activation/deactivation
                // was interrupted by an outstanding native statement.
                if (connection is SqliteConnection sqliteConnection)
                    SqliteConnection.ClearPool(sqliteConnection);

                logger.LogWarning(
                    ex,
                    "[Journal] SQLite pooled connection activation failed; retrying open attempt={Attempt}/{Limit} run={RunId} cmd={CommandId}",
                    attempt,
                    OpenAttemptLimit,
                    lease.RunId,
                    lease.CommandId);
                await Task.Delay(OpenRetryDelay * attempt, ct);
            }
        }
    }

    private static bool IsPooledConnectionActivationFailure(SqliteException ex)
    {
        if (ex.SqliteErrorCode is not (5 or 6))
            return false;

        return ex.Message.Contains("not an error", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("active statements", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
