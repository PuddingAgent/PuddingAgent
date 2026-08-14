using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Execution;

/// <summary>
/// Idempotently upgrades the execution_runs schema for existing SQLite databases.
/// EF EnsureCreated creates clean databases (with trace_id once the entity declares it),
/// but does not add fields to existing tables.
/// </summary>
public static class ExecutionRunSchemaBootstrapper
{
    private const string TableName = "execution_runs";
    private const string TraceIdColumn = "trace_id";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        if (!await ColumnExistsAsync(db, TableName, TraceIdColumn, ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE "execution_runs"
                ADD COLUMN "trace_id" TEXT NULL;
                """,
                ct);

            logger?.LogInformation(
                "[ExecutionRunSchema] Added {Table}.{Column}",
                TableName,
                TraceIdColumn);
        }
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
