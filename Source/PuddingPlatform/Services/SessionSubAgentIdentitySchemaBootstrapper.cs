using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// 幂等补建 <c>session_sub_agents</c> 的人类可读身份三列（display_name / avatar_key / responsibility）。
/// <para>
/// <b>为什么需要它：</b>本项目运行时只调 <c>EnsureCreated</c>，而它对「已经存在的库」什么都不做
/// —— 存量平台库里 <c>session_sub_agents</c> 早已存在，EF 模型新增的三列永远不会被补上。
/// 全仓不存在 EF migration（<c>Migrate()</c> 零调用），因此只能由 per-feature bootstrapper 补列。
/// </para>
/// <para>
/// <b>为什么先查 PRAGMA：</b>SQLite 不支持 <c>ALTER TABLE ADD COLUMN IF NOT EXISTS</c>，
/// 重复执行会直接报 “duplicate column name”。所以每列先读 <c>PRAGMA table_info</c>，
/// 缺才 ALTER —— 这样启动多少次结果都一样（幂等）。
/// </para>
/// <para>
/// <b>两路必须一致：</b>新库的列由 EF 实体声明（<see cref="Data.Entities.SessionSubAgentEntity"/>）创建，
/// 存量库由本类补；两边列集合若漂移，查询会在其中一条路径上炸掉。
/// <c>SchemaBootstrapperFreshDatabaseColumnTests</c> 用列集合相等断言锁死这一点。
/// </para>
/// <para>
/// DDL 用 <c>const string</c> 字面量而非内插字符串：这里没有任何外部输入，
/// 字面量既消 EF1002（SQL 注入分析器）警告，也让 DDL 可直接照抄进 PRAGMA 比对。
/// </para>
/// </summary>
public static class SessionSubAgentIdentitySchemaBootstrapper
{
    /// <summary>表名常量。</summary>
    public const string TableName = "session_sub_agents";

    /// <summary>人读名列。</summary>
    public const string DisplayNameColumn = "display_name";

    /// <summary>预设图形键列。</summary>
    public const string AvatarKeyColumn = "avatar_key";

    /// <summary>一句话职责列。</summary>
    public const string ResponsibilityColumn = "responsibility";

    /// <summary>
    /// 类型只用 TEXT（与 EF 为 <c>MaxLength</c> 生成的一致）：SQLite 不执行长度约束，
    /// 写 TEXT(64) 只会让两条建列路径的 PRAGMA type 不同，徒增比对噪声。
    /// </summary>
    private const string AddDisplayNameSql =
        """
        ALTER TABLE "session_sub_agents"
        ADD COLUMN "display_name" TEXT NULL;
        """;

    private const string AddAvatarKeySql =
        """
        ALTER TABLE "session_sub_agents"
        ADD COLUMN "avatar_key" TEXT NULL;
        """;

    private const string AddResponsibilitySql =
        """
        ALTER TABLE "session_sub_agents"
        ADD COLUMN "responsibility" TEXT NULL;
        """;

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        // 表不存在说明这是比本表更早的库：没有可升级的对象，也不该让启动失败
        // （EF EnsureCreated 会在真正空库时把整表建出来，那时三列已由实体声明建好）。
        if (!await TableExistsAsync(db, ct))
        {
            logger?.LogDebug(
                "[SessionSubAgentIdentitySchema] {Table} not found; identity columns skipped",
                TableName);
            return;
        }

        await EnsureColumnAsync(db, DisplayNameColumn, AddDisplayNameSql, logger, ct);
        await EnsureColumnAsync(db, AvatarKeyColumn, AddAvatarKeySql, logger, ct);
        await EnsureColumnAsync(db, ResponsibilityColumn, AddResponsibilitySql, logger, ct);
    }

    private static async Task EnsureColumnAsync(
        PlatformDbContext db,
        string columnName,
        string addColumnSql,
        ILogger? logger,
        CancellationToken ct)
    {
        if (await ColumnExistsAsync(db, columnName, ct))
            return;

        await db.Database.ExecuteSqlRawAsync(addColumnSql, ct);

        logger?.LogInformation(
            "[SessionSubAgentIdentitySchema] Added {Table}.{Column}",
            TableName,
            columnName);
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
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(TableName)});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // PRAGMA table_info 的第 1 列（0 基）是列名。
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

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
