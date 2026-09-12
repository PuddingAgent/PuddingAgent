using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Idempotently upgrades the sub_agent_runs schema for existing SQLite databases.
/// <para>
/// EF EnsureCreated creates clean databases (with the parent execution identity columns once the
/// entity declares them), but does not add columns to tables created by older builds.
/// </para>
/// <para>
/// 父执行身份（parent_turn_id / parent_command_id / parent_run_id）是 slice-4
/// 「父 Turn 等待异步子代理」的归属依据：缺失时按父 Turn 统计运行中子代理只能退化为会话粒度，
/// 兄弟 Turn 的子代理会被误计。
/// </para>
/// </summary>
public static class SubAgentRunSchemaBootstrapper
{
    private const string TableName = "sub_agent_runs";

    /// <summary>父 Turn ID 列（RuntimeExecutionIdentity.TurnId）。</summary>
    public const string ParentTurnIdColumn = "parent_turn_id";

    /// <summary>父命令 ID 列（RuntimeExecutionIdentity.CommandId）。</summary>
    public const string ParentCommandIdColumn = "parent_command_id";

    /// <summary>父 Run ID 列（RuntimeExecutionIdentity.RunId）。</summary>
    public const string ParentRunIdColumn = "parent_run_id";

    /// <summary>「父 Turn + 状态」复合索引，服务「按父 Turn 统计运行中子代理」查询。</summary>
    public const string ParentTurnStatusIndex = "IX_sub_agent_runs_parent_turn_id_status";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        // 索引表不存在（旧库早于 ADR-021）时无事可做：EF EnsureCreated 不会给已存在的库补表，
        // 此时既没有可升级的列，也不该让启动失败。每次启动都会再试一次，表出现后自然补齐。
        if (!await TableExistsAsync(db, ct))
        {
            logger?.LogDebug(
                "[SubAgentRunSchema] {Table} not found; parent identity upgrade skipped",
                TableName);
            return;
        }

        await EnsureColumnAsync(db, ParentTurnIdColumn, logger, ct);
        await EnsureColumnAsync(db, ParentCommandIdColumn, logger, ct);
        await EnsureColumnAsync(db, ParentRunIdColumn, logger, ct);

        // 该索引未在 EF 模型（PlatformDbContext）中声明，因此新库与旧库都靠这里补；
        // CREATE INDEX IF NOT EXISTS 保证重复调用幂等。
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE INDEX IF NOT EXISTS \"{ParentTurnStatusIndex}\" ON \"{TableName}\" (\"{ParentTurnIdColumn}\", \"status\");",
            ct);

        logger?.LogDebug("[SubAgentRunSchema] Ensured index {Index}", ParentTurnStatusIndex);
    }

    private static async Task EnsureColumnAsync(
        PlatformDbContext db,
        string columnName,
        ILogger? logger,
        CancellationToken ct)
    {
        if (await ColumnExistsAsync(db, columnName, ct))
            return;

        await db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"{TableName}\" ADD COLUMN \"{columnName}\" TEXT NULL;",
            ct);

        logger?.LogInformation(
            "[SubAgentRunSchema] Added {Table}.{Column}",
            TableName,
            columnName);
    }

    private static async Task<bool> TableExistsAsync(PlatformDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = TableName;
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
        PlatformDbContext db,
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
            command.CommandText = $"PRAGMA table_info(\"{TableName}\");";
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
}
