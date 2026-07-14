using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

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
            "command_id TEXT NOT NULL," +
            "client_request_id TEXT," +
            "workspace_id TEXT NOT NULL," +
            "session_id TEXT NOT NULL," +
            "message_id TEXT NOT NULL," +
            "turn_id TEXT NOT NULL," +
            "agent_instance_id TEXT," +
            "agent_template_id TEXT," +
            "user_id TEXT," +
            "payload_json TEXT NOT NULL," +
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

        // Migrate: add fence_token column to pre-existing tables (ADR-056).
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_execution_commands ADD COLUMN fence_token TEXT", ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }
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
        _logger.LogInformation("[CommandStore] Table chat_execution_commands ensured");
    }

    public async Task<ChatCommandRecord> SaveAsync(ChatCommandRecord command, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = new ChatExecutionCommandEntity
        {
            CommandId = command.CommandId,
            ClientRequestId = command.ClientRequestId,
            WorkspaceId = command.WorkspaceId,
            SessionId = command.SessionId,
            MessageId = command.MessageId,
            TurnId = command.TurnId,
            AgentInstanceId = command.AgentInstanceId,
            AgentTemplateId = command.AgentTemplateId,
            UserId = command.UserId,
            PayloadJson = command.PayloadJson,
            Status = command.Status,
            AttemptCount = command.AttemptCount,
            CreatedAt = command.CreatedAt,
            EventCursor = command.EventCursor,
            FenceToken = command.FenceToken,
        };

        db.ChatExecutionCommands.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[CommandStore] Saved command={CommandId} turn={TurnId} session={SessionId} status={Status}",
            command.CommandId, command.TurnId, command.SessionId, command.Status);

        return Map(entity);
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

        var affected = await db.ChatExecutionCommands
            .Where(c => c.Id == targetEntity.Id)
            .Where(c => c.Status == "pending" || (c.LeaseUntil.HasValue && c.LeaseUntil.Value < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, "running")
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

    public async Task UpdateStatusAsync(string commandId, string status, string? lastError = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = await db.ChatExecutionCommands
            .FirstOrDefaultAsync(c => c.CommandId == commandId, ct);

        if (entity is null) return;

        entity.Status = status;
        if (lastError is not null) entity.LastError = lastError;

        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(string commandId, string fenceToken, string status, string? lastError = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // Fence token validation: only the worker with the matching token can complete.
        // Uses ExecuteUpdateAsync for atomicity.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var affected = await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId && c.FenceToken == fenceToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, status)
                .SetProperty(c => c.CompletedAt, now)
                .SetProperty(c => c.LeaseOwner, (string?)null)
                .SetProperty(c => c.LeaseUntil, (long?)null)
                .SetProperty(c => c.LastError, lastError),
                ct);

        if (affected == 0)
        {
            _logger.LogWarning(
                "[CommandStore] Complete fence mismatch or already completed command={CommandId} fence={FenceToken}",
                commandId, fenceToken);
            return;
        }

        _logger.LogInformation(
            "[CommandStore] Completed command={CommandId} status={Status}",
            commandId, status);
    }

    public async Task ReleaseLeaseAsync(string commandId, string fenceToken, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var affected = await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId && c.FenceToken == fenceToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, "pending")
                .SetProperty(c => c.LeaseOwner, (string?)null)
                .SetProperty(c => c.LeaseUntil, (long?)null)
                .SetProperty(c => c.FenceToken, (string?)null),
                ct);

        if (affected == 0)
        {
            _logger.LogWarning(
                "[CommandStore] Release fence mismatch or already completed command={CommandId} fence={FenceToken}",
                commandId, fenceToken);
        }
    }

    public async Task<bool> RenewLeaseAsync(string commandId, string fenceToken, long leaseDurationMs, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var until = now + leaseDurationMs;

        var affected = await db.ChatExecutionCommands
            .Where(c => c.CommandId == commandId && c.FenceToken == fenceToken && c.Status == "running")
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
        TurnId = e.TurnId,
        AgentInstanceId = e.AgentInstanceId,
        AgentTemplateId = e.AgentTemplateId,
        UserId = e.UserId,
        PayloadJson = e.PayloadJson,
        Status = e.Status,
        AttemptCount = e.AttemptCount,
        LeaseOwner = e.LeaseOwner,
        LeaseUntil = e.LeaseUntil,
        CreatedAt = e.CreatedAt,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        LastError = e.LastError,
        EventCursor = e.EventCursor,
        FenceToken = e.FenceToken,
    };
}
