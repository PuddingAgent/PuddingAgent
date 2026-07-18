using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Chat 命令持久存储 — SQLite 实现。
/// 提供命令的可靠受理、租约管理(CAS)、终态提交(fencing)、状态迁移。
/// </summary>
/// <remarks>
/// ADR-058: PayloadJson 列已从表结构中删除。
/// 命令只保存执行引用(UserMessageId/AgentInstanceId/ChannelId)，
/// LLM/Tool/Skill 配置由 ChatExecutionWorker 执行时动态装配。
/// </remarks>
public sealed class ChatCommandStore : IChatCommandStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatCommandStore> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private bool _tableEnsured;

    public ChatCommandStore(IServiceScopeFactory scopeFactory, ILogger<ChatCommandStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private async ValueTask EnsureTableAsync(CancellationToken ct)
    {
        if (_tableEnsured) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS chat_execution_commands (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT," +
            "batch_id TEXT NOT NULL," +
            "command_id TEXT NOT NULL," +
            "client_request_id TEXT," +
            "workspace_id TEXT NOT NULL," +
            "session_id TEXT NOT NULL," +
            "message_id TEXT NOT NULL," +
            "user_message_id TEXT NOT NULL," +
            "turn_id TEXT NOT NULL," +
            "agent_instance_id TEXT NOT NULL," +
            "user_id TEXT," +
            "channel_id TEXT," +
            "run_id TEXT," +
            "terminal_sequence INTEGER," +
            "status TEXT NOT NULL DEFAULT 'pending'," +
            "attempt_count INTEGER NOT NULL DEFAULT 0," +
            "lease_owner TEXT," +
            "lease_until INTEGER," +
            "created_at INTEGER NOT NULL," +
            "started_at INTEGER," +
            "completed_at INTEGER," +
            "last_error TEXT," +
            "event_cursor TEXT," +
            "fence_token TEXT)", ct);

        // ADR-058: Drop legacy payload_json column (no backwards compat)
        try { await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS _chat_exec_old", ct); } catch { }

        // ADR-059: Migrate add new columns for existing tables
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE chat_execution_commands ADD COLUMN batch_id TEXT", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE chat_execution_commands ADD COLUMN run_id TEXT", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE chat_execution_commands ADD COLUMN terminal_sequence INTEGER", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }

        // Migrate: add fence_token column to pre-existing tables (ADR-056).
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_execution_commands ADD COLUMN fence_token TEXT", ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }

        // Migrate: add user_message_id column for P0 event identity model.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_execution_commands ADD COLUMN user_message_id TEXT", ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }

        // ADR-058: Add channel_id column (replaces payload_json for channel info)
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_execution_commands ADD COLUMN channel_id TEXT", ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }

        // ADR-058: Migrate ChatMessages table — add new stable business columns
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE ChatMessages ADD COLUMN message_id TEXT", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE ChatMessages ADD COLUMN turn_id TEXT", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE ChatMessages ADD COLUMN command_id TEXT", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE ChatMessages ADD COLUMN user_id TEXT", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE ChatMessages ADD COLUMN metadata_json TEXT", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_chatmsg_message_id ON ChatMessages(message_id) WHERE message_id != ''", ct); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }

        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_cec_cmd_id ON chat_execution_commands(command_id)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_cec_client_ws ON chat_execution_commands(client_request_id, workspace_id)" +
            " WHERE client_request_id IS NOT NULL", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_cec_session_status ON chat_execution_commands(session_id, status)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_cec_status_lease ON chat_execution_commands(status, lease_until)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_cec_status_created ON chat_execution_commands(status, created_at)", ct);
        _tableEnsured = true;
        _logger.LogInformation("[CommandStore] Table chat_execution_commands ensured (ADR-058 schema)");
    }

    public async Task<ChatCommandRecord> SaveAsync(ChatCommandRecord command, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);

        var (result, _) = await SaveCommandWithTurnAcceptedAsync(
            command, string.Empty, string.Empty, null, 0, ct);
        return result;
    }

    public async Task<(ChatCommandRecord Command, long EventCursor)> SaveCommandWithTurnAcceptedAsync(
        ChatCommandRecord command,
        string turnAcceptedPayloadJson,
        string turnAcceptedEventType,
        string? agentId,
        int schemaVersion,
        CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = new ChatExecutionCommandEntity
        {
            BatchId = Guid.NewGuid().ToString("N"), // legacy: tests may not set this
            CommandId = command.CommandId,
            ClientRequestId = command.ClientRequestId,
            WorkspaceId = command.WorkspaceId,
            SessionId = command.SessionId,
            MessageId = command.MessageId,
            UserMessageId = command.UserMessageId,
            TurnId = command.TurnId,
            AgentInstanceId = command.AgentInstanceId,
            UserId = command.UserId,
            ChannelId = command.ChannelId,
            Status = StatusToString(command.Status),
            AttemptCount = command.AttemptCount,
            CreatedAt = command.CreatedAt,
            EventCursor = command.EventCursor,
            FenceToken = command.FenceToken,
        };

        db.ChatExecutionCommands.Add(entity);
        await db.SaveChangesAsync(ct);

        // ADR-057: turn.accepted is now emitted by ChatExecutionWorker via ConversationEventStore.
        // No session_event_log write here. EventCursor = 0 signals "not yet persisted to Event Store".

        _logger.LogInformation(
            "[CommandStore] Saved command={CommandId} turn={TurnId} session={SessionId} status={Status}",
            command.CommandId, command.TurnId, command.SessionId, command.Status);

        return (Map(entity), 0);
    }

    public async Task<ChatCommandRecord?> GetAsync(string commandId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CommandId == commandId, ct);

        return entity is null ? null : Map(entity);
    }

    public async Task<ChatCommandRecord?> FindByClientRequestIdAsync(string clientRequestId, string workspaceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(c => c.ClientRequestId == clientRequestId && c.WorkspaceId == workspaceId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : Map(entity);
    }

    /// <summary>
    /// ADR-059: 按 conversationId + turnId 查找命令（用于 Cancel/Steering API）。
    /// </summary>
    public async Task<ChatCommandRecord?> FindByTurnIdAsync(string conversationId, string turnId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(c => c.SessionId == conversationId && c.TurnId == turnId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : Map(entity);
    }

    /// <summary>
    /// ADR-059: CAS Running → CancelRequested。FenceToken 校验由 Worker 在 commit 前执行。
    /// </summary>
    public async Task<bool> RequestCancellationAsync(string commandId, string fenceToken, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var cancelRequestedStr = StatusToString(CommandStatus.CancelRequested);
        var runningStr = StatusToString(CommandStatus.Running);

        var affected = await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId && c.Status == runningStr)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, cancelRequestedStr),
                ct);

        if (affected == 0)
        {
            _logger.LogWarning(
                "[CommandStore] RequestCancellation failed: not running cmd={CommandId}",
                commandId);
            return false;
        }

        _logger.LogInformation(
            "[CommandStore] CancelRequested cmd={CommandId}", commandId);
        return true;
    }

    /// <summary>
    /// CAS (Compare-And-Swap) 租约领取 — SELECT + UPDATE WHERE 双阶段原子操作。
    /// 客户端并发领取时只有一个 Worker 能胜出；失败返回 null，调用方重新轮询。
    /// FenceToken 在领取时分配，用于后续所有操作（续租/释放/终态提交）的所有权校验。
    /// </summary>
    public async Task<ChatCommandRecord?> LeaseNextAsync(string leaseOwner, long leaseDurationMs, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var until = now + leaseDurationMs;
        var fenceToken = Guid.NewGuid().ToString("N");

        // Atomic CAS: UPDATE with WHERE predicate ensures only one worker wins.
        // Returns the id of the updated row; if 0, no row matched (race lost or empty queue).
        var targetEntity = await db.ChatExecutionCommands
            .Where(c => c.Status == "pending" || (c.LeaseUntil.HasValue && c.LeaseUntil.Value < now))
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync(ct);

        if (targetEntity is null) return null;

        var runningStr = StatusToString(CommandStatus.Running);
        var affected = await db.ChatExecutionCommands
            .Where(c => c.Id == targetEntity.Id)
            .Where(c => c.Status == "pending" || (c.LeaseUntil.HasValue && c.LeaseUntil.Value < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, runningStr)
                .SetProperty(c => c.LeaseOwner, leaseOwner)
                .SetProperty(c => c.LeaseUntil, until)
                .SetProperty(c => c.FenceToken, fenceToken)
                .SetProperty(c => c.StartedAt, now)
                .SetProperty(c => c.AttemptCount, c => c.AttemptCount + 1),
                ct);

        if (affected == 0)
        {
            // Another worker claimed it first — retry by returning null (caller re-polls)
            _logger.LogDebug("[CommandStore] CAS lost for command id={Id}", targetEntity.Id);
            return null;
        }

        db.ChangeTracker.Clear();
        var entity = await db.ChatExecutionCommands.AsNoTracking()
            .FirstAsync(c => c.Id == targetEntity.Id, ct);

        _logger.LogInformation(
            "[CommandStore] Leased command={CommandId} turn={TurnId} attempt={Attempt} leaseOwner={LeaseOwner} fence={FenceToken}",
            entity.CommandId, entity.TurnId, entity.AttemptCount, leaseOwner, fenceToken);

        return Map(entity);
    }

    public async Task MarkRunningAsync(string commandId, string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var statusStr = StatusToString(CommandStatus.Running);
        await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, statusStr)
                .SetProperty(c => c.StartedAt, c => c.StartedAt ?? now),
                ct);

        _logger.LogInformation(
            "[CommandStore] MarkRunning command={CommandId} runId={RunId}", commandId, runId);
    }

    public async Task CommitSucceededAsync(string commandId, string fenceToken, string runId, long terminalSequence, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var statusStr = StatusToString(CommandStatus.Succeeded);
        await CommitTerminalAsync(commandId, fenceToken, statusStr, null, null, terminalSequence, now, ct);
        _logger.LogInformation(
            "[CommandStore] CommitSucceeded command={CommandId} runId={RunId} seq={Seq}",
            commandId, runId, terminalSequence);
    }

    public async Task CommitFailedAsync(string commandId, string fenceToken, string runId, string errorCode, string errorMessage, long? terminalSequence, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var statusStr = StatusToString(CommandStatus.Failed);
        var lastError = $"{errorCode}: {errorMessage}";
        await CommitTerminalAsync(commandId, fenceToken, statusStr, lastError, errorMessage, terminalSequence ?? 0, now, ct);
        _logger.LogInformation(
            "[CommandStore] CommitFailed command={CommandId} runId={RunId} code={ErrorCode}",
            commandId, runId, errorCode);
    }

    public async Task CommitCancelledAsync(string commandId, string fenceToken, string runId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var statusStr = StatusToString(CommandStatus.Cancelled);
        await CommitTerminalAsync(commandId, fenceToken, statusStr, null, null, 0, now, ct);
        _logger.LogInformation(
            "[CommandStore] CommitCancelled command={CommandId} runId={RunId}", commandId, runId);
    }

    public async Task MarkLeaseLostAsync(string commandId, string fenceToken, string runId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var statusStr = StatusToString(CommandStatus.LeaseLost);
        await CommitTerminalAsync(commandId, fenceToken, statusStr, null, null, 0, now, ct);
        _logger.LogInformation(
            "[CommandStore] MarkLeaseLost command={CommandId} runId={RunId}", commandId, runId);
    }

    private async Task CommitTerminalAsync(
        string commandId, string fenceToken, string statusStr,
        string? lastError, string? errorMessage, long terminalSequence,
        long now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var affected = await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId && c.FenceToken == fenceToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, statusStr)
                .SetProperty(c => c.CompletedAt, now)
                .SetProperty(c => c.LeaseOwner, (string?)null)
                .SetProperty(c => c.LeaseUntil, (long?)null)
                .SetProperty(c => c.LastError, lastError),
                ct);

        if (affected == 0)
        {
            // Fence mismatch: refuse to overwrite. The lease was lost.
            _logger.LogError(
                "[CommandStore] Fence mismatch on terminal commit cmd={CommandId} expectedFence={FenceToken}. Status NOT updated.",
                commandId, fenceToken);
            throw new InvalidOperationException(
                $"Fence mismatch on terminal commit for command {commandId}. " +
                "Lease may have been lost or command already completed by another worker.");
        }
    }

    public async Task ReleaseLeaseAsync(string commandId, string fenceToken, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var pendingStr = StatusToString(CommandStatus.Pending);

        var affected = await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId && c.FenceToken == fenceToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, pendingStr)
                .SetProperty(c => c.LeaseOwner, (string?)null)
                .SetProperty(c => c.LeaseUntil, (long?)null)
                .SetProperty(c => c.FenceToken, (string?)null),
                ct);

        if (affected == 0)
        {
            _logger.LogWarning(
                "[CommandStore] Release fence mismatch, refusing release cmd={CommandId} fence={FenceToken}",
                commandId, fenceToken);
        }
    }

    public async Task<bool> RenewLeaseAsync(string commandId, string fenceToken, long leaseDurationMs, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var until = now + leaseDurationMs;

        var runningStr = StatusToString(CommandStatus.Running);
        var affected = await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId && c.FenceToken == fenceToken && c.Status == runningStr)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.LeaseUntil, until),
                ct);

        if (affected == 0)
        {
            _logger.LogWarning(
                "[CommandStore] RenewLease fence mismatch or not running command={CommandId} fence={FenceToken}",
                commandId, fenceToken);
            return false;
        }

        _logger.LogDebug(
            "[CommandStore] Renewed lease command={CommandId} until={LeaseUntil}",
            commandId, until);
        return true;
    }

    private static ChatCommandRecord Map(ChatExecutionCommandEntity e) => new()
    {
        CommandId = e.CommandId,
        ClientRequestId = e.ClientRequestId,
        WorkspaceId = e.WorkspaceId,
        SessionId = e.SessionId,
        MessageId = e.MessageId,
        UserMessageId = e.UserMessageId ?? string.Empty,
        TurnId = e.TurnId,
        AgentInstanceId = e.AgentInstanceId ?? string.Empty,
        UserId = e.UserId,
        ChannelId = e.ChannelId,
        Status = ParseStatus(e.Status ?? "pending"),
        AttemptCount = e.AttemptCount,
        LeaseOwner = e.LeaseOwner,
        LeaseUntil = e.LeaseUntil,
        CreatedAt = e.CreatedAt,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        LastError = e.LastError,
        EventCursor = e.EventCursor,
        FenceToken = e.FenceToken,
        RunId = e.RunId,
    };

    // ── Status helpers ─────────────────────────────────────

    private static string StatusToString(CommandStatus status) => status switch
    {
        CommandStatus.Pending => "pending",
        CommandStatus.Leased => "leased",
        CommandStatus.Running => "running",
        CommandStatus.CancelRequested => "cancel_requested",
        CommandStatus.Succeeded => "succeeded",
        CommandStatus.Failed => "failed",
        CommandStatus.Cancelled => "cancelled",
        CommandStatus.LeaseLost => "lease_lost",
        _ => "pending",
    };

    private static CommandStatus ParseStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "pending" => CommandStatus.Pending,
        "leased" => CommandStatus.Leased,
        "running" => CommandStatus.Running,
        "cancel_requested" => CommandStatus.CancelRequested,
        "succeeded" => CommandStatus.Succeeded,
        "failed" => CommandStatus.Failed,
        "cancelled" => CommandStatus.Cancelled,
        "lease_lost" => CommandStatus.LeaseLost,
        _ => CommandStatus.Pending,
    };
}
