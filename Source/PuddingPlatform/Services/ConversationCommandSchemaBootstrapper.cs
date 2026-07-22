using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Idempotently upgrades the durable conversation-command schema for existing SQLite databases.
/// EF EnsureCreated creates clean databases but does not add fields to existing tables.
/// </summary>
public static class ConversationCommandSchemaBootstrapper
{
    private const string TableName = "chat_execution_commands";
    private const string MetadataJsonColumn = "metadata_json";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        if (await ColumnExistsAsync(db, TableName, MetadataJsonColumn, ct))
            return;

        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "chat_execution_commands"
            ADD COLUMN "metadata_json" TEXT NULL;
            """,
            ct);

        logger?.LogInformation(
            "[ConversationCommandSchema] Added {Table}.{Column}",
            TableName,
            MetadataJsonColumn);
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
