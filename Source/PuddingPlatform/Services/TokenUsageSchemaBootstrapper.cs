using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Idempotently upgrades the token-usage ledger schema for existing SQLite databases.
/// EF EnsureCreated only creates a clean database; it does not add fields to an existing table.
/// </summary>
public static class TokenUsageSchemaBootstrapper
{
    private const string TableName = "TokenUsageEvents";
    private const string ParentSessionIdColumn = "ParentSessionId";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        if (!await ColumnExistsAsync(db, TableName, ParentSessionIdColumn, ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE "TokenUsageEvents"
                ADD COLUMN "ParentSessionId" TEXT NULL;
                """,
                ct);

            logger?.LogInformation(
                "[TokenUsageSchema] Added {Table}.{Column}",
                TableName,
                ParentSessionIdColumn);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_TokenUsageEvents_ParentSessionId"
            ON "TokenUsageEvents" ("ParentSessionId");
            """,
            ct);
    }

    private static async Task<bool> ColumnExistsAsync(
        DbContext db,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
