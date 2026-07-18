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
                       c.created_at
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

            // Step 3: Create ExecutionRun — FencingToken is auto-increment PK
            using var runCmd = conn.CreateCommand();
            runCmd.Transaction = tx;
            runCmd.CommandText = @"
                INSERT INTO execution_runs
                (run_id, command_id, conversation_id, turn_id, attempt,
                 worker_id, status, lease_until, started_at)
                VALUES
                (@runId, @commandId, @conversationId, @turnId, @attempt,
                 @workerId, @status, @leaseUntil, @startedAt);
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

            var fencingToken = (long)(await runCmd.ExecuteScalarAsync(ct))!;

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
                ExpiresAt: expiresAt);
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

    public async Task ReleaseAsync(ExecutionLease lease, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Release Run only if it matches the exact lease ownership
        await db.ExecutionRuns
            .Where(r => r.RunId == lease.RunId)
            .Where(r => r.WorkerId == lease.WorkerId)
            .Where(r => r.FencingToken == lease.FencingToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LeaseUntil, nowMs)
                .SetProperty(r => r.Status, (string)"lease_lost"),
                ct);

        // Release Command — reset to pending for retry
        await db.ChatExecutionCommands
            .Where(c => c.CommandId == lease.CommandId)
            .Where(c => c.LeaseOwner == lease.WorkerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, (string)"pending")
                .SetProperty(c => c.LeaseOwner, (string?)null)
                .SetProperty(c => c.LeaseUntil, (long?)null),
                ct);

        logger.LogInformation("[LeaseStore] Released run={RunId} cmd={CmdId} → pending",
            lease.RunId, lease.CommandId);
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
            SELECT DISTINCT r.command_id, r.run_id
            FROM execution_runs r
            WHERE r.status IN ('leased', 'running', 'cancel_requested')
              AND r.lease_until IS NOT NULL
              AND r.lease_until < @nowMs";
        AddParam(findCmd, "@nowMs", nowMs);

        var expired = new List<(string cmdId, string runId)>();
        using (var reader = await findCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                expired.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (cmdId, runId) in expired)
        {
            // Mark run as lease_lost
            using var runUpd = conn.CreateCommand();
            runUpd.Transaction = tx;
            runUpd.CommandText = @"
                UPDATE execution_runs
                SET status = 'lease_lost'
                WHERE run_id = @runId
                  AND status IN ('leased', 'running', 'cancel_requested')";
            AddParam(runUpd, "@runId", runId);
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
        }

        if (expired.Count > 0)
            logger.LogInformation("[LeaseStore] Reclaimed {Count} expired runs", expired.Count);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
