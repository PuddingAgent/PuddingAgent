using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Execution;

/// <summary>
/// Idempotently upgrades the execution_runs / chat_execution_commands schemas for existing SQLite databases.
/// EF EnsureCreated creates clean databases (with the columns once the entities declare them),
/// but does not add fields to existing tables.
/// </summary>
public static class ExecutionRunSchemaBootstrapper
{
    /// <summary>每项为「表 / 列 / 缺失时执行的 DDL」。顺序稳定，便于日志与测试断言。</summary>
    private static readonly (string Table, string Column, string Ddl)[] RequiredColumns =
    [
        (
            "execution_runs",
            "trace_id",
            """
            ALTER TABLE "execution_runs"
            ADD COLUMN "trace_id" TEXT NULL;
            """),
        // NC-01：审批暂停恢复点必须独立成列——metadata_json 上限 4096，
        // 而恢复点携带 Runtime 会话历史与批次状态，量级远超「附加元数据」。
        (
            "chat_execution_commands",
            "approval_resume_json",
            """
            ALTER TABLE "chat_execution_commands"
            ADD COLUMN "approval_resume_json" TEXT NULL;
            """),
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        foreach (var (table, column, ddl) in RequiredColumns)
        {
            // 表本身缺失时不能 ALTER：EF EnsureCreated 会在本方法之前建表，
            // 而面向历史库的测试可能只建了其中一张表。缺表不是本方法的职责。
            if (!await TableExistsAsync(db, table, ct))
            {
                logger?.LogDebug(
                    "[ExecutionRunSchema] Skipped {Table}.{Column} — table is absent",
                    table,
                    column);
                continue;
            }

            if (await ColumnExistsAsync(db, table, column, ct))
                continue;

            await db.Database.ExecuteSqlRawAsync(ddl, ct);

            logger?.LogInformation(
                "[ExecutionRunSchema] Added {Table}.{Column}",
                table,
                column);
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbContext db,
        string tableName,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
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
