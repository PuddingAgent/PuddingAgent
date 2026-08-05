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

    /// <summary>
    /// Nullable columns that exist on <see cref="Data.Entities.TokenUsageEventEntity"/> but
    /// were introduced after the last EF migration / model snapshot. Because EnsureCreated
    /// never alters an existing table, every one of them must be self-healed here.
    /// </summary>
    private static readonly (string Name, string Definition)[] RequiredColumns =
    [
        ("ParentSessionId", "TEXT NULL"),
        // Prompt prefix 分层 token 分解簇
        ("MessageTokens", "INTEGER NULL"),
        ("ToolDefinitionTokens", "INTEGER NULL"),
        ("SystemMessageTokens", "INTEGER NULL"),
        ("HistoryMessageTokens", "INTEGER NULL"),
        // 熵探针簇
        ("HistoryMessageEntropy", "REAL NULL"),
        ("SystemMessageEntropy", "REAL NULL"),
        ("ToolDefinitionEntropy", "REAL NULL"),
        // agent loop 轮次/工具簇
        ("TurnRound", "INTEGER NULL"),
        ("ToolCallCount", "INTEGER NULL"),
        ("ToolNames", "TEXT NULL"),
        ("SubAgentId", "TEXT NULL"),
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        foreach (var (columnName, columnDefinition) in RequiredColumns)
        {
            if (!await ColumnExistsAsync(db, TableName, columnName, ct))
            {
#pragma warning disable EF1002 // Column names/definitions are compile-time constants from RequiredColumns
                await db.Database.ExecuteSqlRawAsync(
                    $"""
                    ALTER TABLE "TokenUsageEvents"
                    ADD COLUMN "{columnName}" {columnDefinition};
                    """,
                    ct);
#pragma warning restore EF1002

                logger?.LogInformation(
                    "[TokenUsageSchema] Added {Table}.{Column}",
                    TableName,
                    columnName);
            }
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
