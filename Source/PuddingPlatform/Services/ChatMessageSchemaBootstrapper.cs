using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Idempotently upgrades the durable ChatMessages schema for existing SQLite databases
/// (ADR-077 content_parts_json)。EF EnsureCreated 只覆盖全新库；本 bootstrap 覆盖已有库。
/// </summary>
public static class ChatMessageSchemaBootstrapper
{
    private const string TableName = "ChatMessages";
    private const string ContentPartsJsonColumn = "content_parts_json";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        if (!await ColumnExistsAsync(db, TableName, ContentPartsJsonColumn, ct))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE "ChatMessages"
                ADD COLUMN "content_parts_json" TEXT NULL;
                """,
                ct);

            logger?.LogInformation(
                "[ChatMessageSchema] Added {Table}.{Column}",
                TableName,
                ContentPartsJsonColumn);
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        PlatformDbContext db,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        // EF does not open the connection for raw ADO.NET commands; opening it
        // here on demand keeps the bootstrap usable before any EF query ran.
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
            tableNameParam.Value = tableName;
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
