using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Idempotently upgrades existing SQLite databases to the Turn-bound Steering
/// contract. EF EnsureCreated covers clean databases but never alters an
/// existing table.
/// </summary>
public static class SessionSteeringSchemaBootstrapper
{
    private const string TableName = "session_steering_messages";
    private const string TargetTurnColumn = "target_turn_id";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "session_steering_messages" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_session_steering_messages" PRIMARY KEY AUTOINCREMENT,
                "steering_id" TEXT NOT NULL,
                "workspace_id" TEXT NOT NULL,
                "session_id" TEXT NOT NULL,
                "target_turn_id" TEXT NOT NULL DEFAULT '',
                "agent_id" TEXT NULL,
                "source_queue_item_id" TEXT NULL,
                "message_text" TEXT NOT NULL,
                "priority" INTEGER NOT NULL,
                "status" TEXT NOT NULL,
                "created_by" TEXT NULL,
                "created_at_utc" TEXT NOT NULL,
                "consumed_at_utc" TEXT NULL,
                "consumed_round" INTEGER NULL,
                "expired_at_utc" TEXT NULL
            );
            """,
            ct);

        var addedTargetTurn = !await ColumnExistsAsync(db, TargetTurnColumn, ct);
        if (addedTargetTurn)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE "session_steering_messages"
                ADD COLUMN "target_turn_id" TEXT NOT NULL DEFAULT '';

                UPDATE "session_steering_messages"
                SET "status" = 'expired',
                    "expired_at_utc" = strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')
                WHERE "status" = 'pending' AND "target_turn_id" = '';
                """,
                ct);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS "IX_session_steering_messages_SessionId_Status_Priority";
            DROP INDEX IF EXISTS "IX_session_steering_messages_session_id_status_priority";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_session_steering_messages_SteeringId"
                ON "session_steering_messages" ("steering_id");
            CREATE INDEX IF NOT EXISTS "IX_session_steering_messages_WorkspaceId_CreatedAtUtc"
                ON "session_steering_messages" ("workspace_id", "created_at_utc");
            CREATE INDEX IF NOT EXISTS "IX_session_steering_messages_SessionId_TargetTurnId_Status_Priority"
                ON "session_steering_messages" ("session_id", "target_turn_id", "status", "priority");
            """,
            ct);

        if (addedTargetTurn)
        {
            logger?.LogInformation(
                "[SessionSteeringSchema] Added {Table}.{Column}; legacy pending rows were expired",
                TableName,
                TargetTurnColumn);
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        PlatformDbContext db,
        string columnName,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedConnection = connection.State != System.Data.ConnectionState.Open;
        if (openedConnection)
            await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(1) FROM pragma_table_info(@tableName)
                WHERE name = @columnName;
                """;
            var tableNameParam = command.CreateParameter();
            tableNameParam.ParameterName = "@tableName";
            tableNameParam.Value = TableName;
            command.Parameters.Add(tableNameParam);
            var columnNameParam = command.CreateParameter();
            columnNameParam.ParameterName = "@columnName";
            columnNameParam.Value = columnName;
            command.Parameters.Add(columnNameParam);

            var result = await command.ExecuteScalarAsync(ct);
            return result is not null && Convert.ToInt64(result) > 0;
        }
        finally
        {
            if (openedConnection)
                await connection.CloseAsync();
        }
    }
}
