using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Execution;

/// <summary>
/// ADR-059: SQLite Execution Lease Store — atomic CAS with per-conversation mutex.
/// Uses BEGIN IMMEDIATE; FencingToken is SQLite auto-increment PK (no mismatch).
/// Reclaims expired runs before acquiring new leases.
/// </summary>
public sealed class SqliteExecutionLeaseStore(
    IServiceScopeFactory scopeFactory,
    ILogger<SqliteExecutionLeaseStore> logger) : IExecutionLeaseStore
{
    public async Task<ExecutionLease?> TryAcquireAsync(
        string workerId, TimeSpan duration, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);

        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var untilMs = nowMs + (long)duration.TotalMilliseconds;

            // Step 0: Reclaim expired runs (crashed workers)
            await ReclaimExpiredRunsAsync(conn, tx, nowMs, ct);

            // Step 1: Find pending command with no active run on its conversation
            using var selCmd = conn.CreateCommand();
            selCmd.Transaction = tx;
            selCmd.CommandText = @"
                SELECT c.command_id, c.workspace_id, c.session_id, c.turn_id,
                       c.user_message_id, c.agent_instance_id, c.user_id,
                       c.message_id, c.client_request_id, c.attempt_count,
                       c.created_at, c.trace_id
                FROM chat_execution_commands c
                WHERE c.status = 'pending'
                  AND NOT EXISTS (
                      SELECT 1 FROM execution_runs r
                      WHERE r.conversation_id = c.session_id
                        AND r.status IN ('leased', 'running', 'cancel_requested')
                        AND (r.lease_until IS NULL OR r.lease_until >= @nowMs)
                  )
                ORDER BY c.created_at ASC
                LIMIT 1";
            AddParam(selCmd, "@nowMs", nowMs);

            using var reader = await selCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var commandId = reader.GetString(0);
            var workspaceId = reader.GetString(1);
            var conversationId = reader.GetString(2);
            var turnId = reader.GetString(3);
            var userMessageId = reader.GetString(4);
            var agentInstanceId = reader.GetString(5);
            var userId = reader.IsDBNull(6) ? null : reader.GetString(6);
            var messageId = reader.GetString(7);
            var clientRequestId = reader.IsDBNull(8) ? null : reader.GetString(8);
            var attemptCount = reader.GetInt32(9);
            var createdAt = reader.GetInt64(10);
            var traceId = reader.IsDBNull(11) ? null : reader.GetString(11);

            await reader.CloseAsync();

            var runId = Guid.NewGuid().ToString("N");
            var newAttempt = attemptCount + 1;

            // Step 2: CAS update Command to leased
            using var updCmd = conn.CreateCommand();
            updCmd.Transaction = tx;
            updCmd.CommandText = @"
                UPDATE chat_execution_commands
                SET status = @status,
                    lease_owner = @workerId,
                    lease_until = @leaseUntil,
                    attempt_count = @attempt,
                    started_at = @startedAt
                WHERE command_id = @commandId
                  AND status = 'pending'";
            AddParam(updCmd, "@status", "leased");
            AddParam(updCmd, "@workerId", workerId);
            AddParam(updCmd, "@leaseUntil", untilMs);
            AddParam(updCmd, "@attempt", newAttempt);
            AddParam(updCmd, "@startedAt", nowMs);
            AddParam(updCmd, "@commandId", commandId);

            var affected = await updCmd.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                await tx.RollbackAsync(ct);
                logger.LogDebug("[LeaseStore] CAS lost cmd={CmdId}", commandId);
                return null;
            }

            // Step 3: Create ExecutionRun — FencingToken is auto-increment PK。
            // G2：(command_id, attempt) 是唯一键。回收/重放会把命令退回 pending 但不回退 attempt_count，
            // 于是 next attempt 可能与既有 run 行重合；直接 INSERT 会撞唯一键并抛 SqliteException。
            // 这里在同一持锁事务内先探针、显式分流：同一 (command_id, attempt) 只产生一行 run，且不抛异常。
            long fencingToken;
            var existingRun = await FindRunForAttemptAsync(conn, tx, commandId, newAttempt, ct);
            if (existingRun is null)
            {
                using var runCmd = conn.CreateCommand();
                runCmd.Transaction = tx;
                runCmd.CommandText = @"
                    INSERT INTO execution_runs
                    (run_id, command_id, conversation_id, turn_id, attempt,
                     worker_id, status, lease_until, started_at, trace_id)
                    VALUES
                    (@runId, @commandId, @conversationId, @turnId, @attempt,
                     @workerId, @status, @leaseUntil, @startedAt, @traceId);
                    SELECT last_insert_rowid()";
                AddParam(runCmd, "@runId", runId);
                AddParam(runCmd, "@commandId", commandId);
                AddParam(runCmd, "@conversationId", conversationId);
                AddParam(runCmd, "@turnId", turnId);
                AddParam(runCmd, "@attempt", newAttempt);
                AddParam(runCmd, "@workerId", workerId);
                AddParam(runCmd, "@status", "leased");
                AddParam(runCmd, "@leaseUntil", untilMs);
                AddParam(runCmd, "@startedAt", nowMs);
                AddParam(runCmd, "@traceId", traceId ?? (object)DBNull.Value);

                fencingToken = (long)(await runCmd.ExecuteScalarAsync(ct))!;
            }
            else
            {
                // 同一 attempt 已有历史 run 行：复用该行及其 fencing_token（幂等复跑），不新建第二行。
                if (existingRun.Value.Status is "leased" or "running" or "cancel_requested")
                {
                    // 该 attempt 仍被其它 writer 的活跃租约持有：本次 claim 让位（连同上面的 CAS 一起回滚）。
                    await tx.RollbackAsync(ct);
                    logger.LogDebug(
                        "[LeaseStore] Attempt already leased cmd={CmdId} attempt={Attempt} run={RunId}",
                        commandId, newAttempt, existingRun.Value.RunId);
                    return null;
                }

                using var adoptCmd = conn.CreateCommand();
                adoptCmd.Transaction = tx;
                adoptCmd.CommandText = @"
                    UPDATE execution_runs
                    SET worker_id = @workerId,
                        status = 'leased',
                        lease_until = @leaseUntil,
                        started_at = @startedAt,
                        completed_at = NULL,
                        terminal_sequence = NULL
                    WHERE run_id = @runId";
                AddParam(adoptCmd, "@workerId", workerId);
                AddParam(adoptCmd, "@leaseUntil", untilMs);
                AddParam(adoptCmd, "@startedAt", nowMs);
                AddParam(adoptCmd, "@runId", existingRun.Value.RunId);
                await adoptCmd.ExecuteNonQueryAsync(ct);

                runId = existingRun.Value.RunId;
                fencingToken = existingRun.Value.FencingToken;
            }

            await tx.CommitAsync(ct);

            var expiresAt = DateTimeOffset.UtcNow + duration;
            logger.LogInformation(
                "[LeaseStore] Acquired cmd={CmdId} turn={TurnId} runId={RunId} fence={Fence} attempt={Attempt}",
                commandId, turnId, runId, fencingToken, newAttempt);

            return new ExecutionLease(
                CommandId: commandId,
                WorkerId: workerId,
                WorkspaceId: workspaceId,
                ConversationId: conversationId,
                TurnId: turnId,
                RunId: runId,
                FencingToken: fencingToken,
                ExpiresAt: expiresAt)
            {
                TraceId = traceId,
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<bool> RenewAsync(
        ExecutionLease lease, TimeSpan duration, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var untilMs = nowMs + (long)duration.TotalMilliseconds;

        var affected = await db.ExecutionRuns
            .Where(r => r.RunId == lease.RunId)
            .Where(r => r.WorkerId == lease.WorkerId)
            .Where(r => r.FencingToken == lease.FencingToken)
            .Where(r => r.Status == "leased" || r.Status == "running" ||
                        r.Status == "cancel_requested")
            .Where(r => r.LeaseUntil != null && r.LeaseUntil >= nowMs)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LeaseUntil, untilMs)
                .SetProperty(r => r.Status, (string)"running"),
                ct);

        if (affected == 0)
            logger.LogWarning("[LeaseStore] Renew failed run={RunId} fence={Fence}",
                lease.RunId, lease.FencingToken);

        if (affected > 0)
        {
            await db.ChatExecutionCommands
                .Where(c => c.CommandId == lease.CommandId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.LeaseUntil, untilMs),
                    ct);
        }

        return affected > 0;
    }

    /// <summary>
    /// G1：按调用方声明的 <paramref name="outcome"/> 释放租约。
    /// LeaseLost = 真实丢失/中止回退（run→lease_lost，command→pending，Turn→accepted，可重试）；
    /// Succeeded/Failed/Cancelled = 优雅释放（run/command 记为对应终态并清空租约，不重排队、不回退 Turn）。
    /// </summary>
    public async Task ReleaseAsync(ExecutionLease lease, RunStatus outcome, CancellationToken ct)
    {
        var runStatus = outcome switch
        {
            RunStatus.LeaseLost => "lease_lost",
            RunStatus.Succeeded => "succeeded",
            RunStatus.Failed => "failed",
            RunStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "ReleaseAsync 只接受终态或 LeaseLost；Leased/Running 不是释放语义。"),
        };
        var retryable = outcome == RunStatus.LeaseLost;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        try
        {
            // 只有仍活跃的 run 会被本次释放改写；Journal 已提交终态的行保持不变（affected=0）。
            using var runCmd = conn.CreateCommand();
            runCmd.Transaction = tx;
            runCmd.CommandText = @"
                UPDATE execution_runs
                SET lease_until = @nowMs,
                    status = @runStatus, completed_at = COALESCE(completed_at, @nowMs)
                WHERE run_id = @runId
                  AND worker_id = @workerId
                  AND fencing_token = @fencingToken
                  AND status IN ('leased', 'running', 'cancel_requested')";
            AddParam(runCmd, "@nowMs", nowMs);
            AddParam(runCmd, "@runStatus", runStatus);
            AddParam(runCmd, "@runId", lease.RunId);
            AddParam(runCmd, "@workerId", lease.WorkerId);
            AddParam(runCmd, "@fencingToken", lease.FencingToken);
            var released = await runCmd.ExecuteNonQueryAsync(ct);

            if (released > 0)
            {
                using var commandCmd = conn.CreateCommand();
                commandCmd.Transaction = tx;
                if (retryable)
                {
                    // 中止回退：命令回到 pending 以便重启/重试后再次领取（attempt_count 不回退）。
                    commandCmd.CommandText = @"
                        UPDATE chat_execution_commands
                        SET status = 'pending',
                            lease_owner = NULL,
                            lease_until = NULL
                        WHERE command_id = @commandId
                          AND lease_owner = @workerId
                          AND status IN ('leased', 'running', 'cancel_requested')";
                }
                else
                {
                    // 优雅释放：沿用调用方声明的终态，禁止退回 pending（否则已完成的 Turn 会被重复执行）。
                    commandCmd.CommandText = @"
                        UPDATE chat_execution_commands
                        SET status = @commandStatus,
                            lease_owner = NULL,
                            lease_until = NULL,
                            completed_at = COALESCE(completed_at, @nowMs)
                        WHERE command_id = @commandId
                          AND lease_owner = @workerId
                          AND status IN ('leased', 'running', 'cancel_requested')";
                    AddParam(commandCmd, "@commandStatus", runStatus);
                    AddParam(commandCmd, "@nowMs", nowMs);
                }
                AddParam(commandCmd, "@commandId", lease.CommandId);
                AddParam(commandCmd, "@workerId", lease.WorkerId);
                await commandCmd.ExecuteNonQueryAsync(ct);

                if (retryable)
                {
                    await ResetTurnForRetryAsync(
                        conn, tx, lease.ConversationId, lease.TurnId, ct);
                }
            }

            await tx.CommitAsync(ct);
            logger.LogInformation(
                "[LeaseStore] Released run={RunId} cmd={CmdId} outcome={Outcome} released={Released} requeued={Requeued}",
                lease.RunId, lease.CommandId, outcome, released > 0, retryable && released > 0);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// G2：读取同一 (command_id, attempt) 上已存在的 run 行（无则返回 null）。
    /// 调用方必须已持有该 command 的写事务，否则返回值不能作为幂等依据。
    /// </summary>
    private static async Task<(string RunId, long FencingToken, string Status)?> FindRunForAttemptAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        string commandId,
        int attempt,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT run_id, fencing_token, status
            FROM execution_runs
            WHERE command_id = @commandId AND attempt = @attempt";
        AddParam(cmd, "@commandId", commandId);
        AddParam(cmd, "@attempt", attempt);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return (reader.GetString(0), reader.GetInt64(1), reader.GetString(2));
    }

    private async Task ReclaimExpiredRunsAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        long nowMs,
        CancellationToken ct)
    {
        // Find runs with expired leases that are still active
        using var findCmd = conn.CreateCommand();
        findCmd.Transaction = tx;
        findCmd.CommandText = @"
            SELECT DISTINCT r.command_id, r.run_id, r.conversation_id, r.turn_id
            FROM execution_runs r
            WHERE r.status IN ('leased', 'running', 'cancel_requested')
              AND r.lease_until IS NOT NULL
              AND r.lease_until < @nowMs";
        AddParam(findCmd, "@nowMs", nowMs);

        var expired = new List<(string cmdId, string runId, string conversationId, string turnId)>();
        using (var reader = await findCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                expired.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        foreach (var (cmdId, runId, conversationId, turnId) in expired)
        {
            // Mark run as lease_lost
            using var runUpd = conn.CreateCommand();
            runUpd.Transaction = tx;
            runUpd.CommandText = @"
                UPDATE execution_runs
                SET status = 'lease_lost', completed_at = COALESCE(completed_at, @nowMs)
                WHERE run_id = @runId
                  AND status IN ('leased', 'running', 'cancel_requested')";
            AddParam(runUpd, "@runId", runId);
            AddParam(runUpd, "@nowMs", nowMs);
            await runUpd.ExecuteNonQueryAsync(ct);

            // Reset command to pending (only if not already terminal)
            using var cmdUpd = conn.CreateCommand();
            cmdUpd.Transaction = tx;
            cmdUpd.CommandText = @"
                UPDATE chat_execution_commands
                SET status = 'pending',
                    lease_owner = NULL,
                    lease_until = NULL
                WHERE command_id = @cmdId
                  AND status IN ('leased', 'running', 'cancel_requested')";
            AddParam(cmdUpd, "@cmdId", cmdId);
            await cmdUpd.ExecuteNonQueryAsync(ct);

            await ResetTurnForRetryAsync(conn, tx, conversationId, turnId, ct);
        }

        if (expired.Count > 0)
            logger.LogInformation("[LeaseStore] Reclaimed {Count} expired runs", expired.Count);
    }

    private static async Task ResetTurnForRetryAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        string conversationId,
        string turnId,
        CancellationToken ct)
    {
        using var turnCmd = conn.CreateCommand();
        turnCmd.Transaction = tx;
        turnCmd.CommandText = @"
            UPDATE conversation_turns
            SET status = 'accepted'
            WHERE conversation_id = @conversationId
              AND turn_id = @turnId
              AND status = 'running'
              AND terminal_sequence IS NULL";
        AddParam(turnCmd, "@conversationId", conversationId);
        AddParam(turnCmd, "@turnId", turnId);
        await turnCmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
