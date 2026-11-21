using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 持久子代理池 <c>agent_pool</c> 的幂等建表与索引补齐（S1：只建表，不碰任何数据）。
/// <para>
/// <b>为什么这里要自己建表，而不是像 <see cref="SubAgentRunSchemaBootstrapper"/> 那样只补索引：</b>
/// 全新库的表由 EF <c>EnsureCreated</c> 依实体声明创建，但 <c>EnsureCreated</c> 对「已经存在的库」
/// 什么都不做 —— 它只在库里一张表都没有时才建整套 schema。存量平台库早已有几十上百张表，
/// 因此新加的 <c>agent_pool</c> 永远不会被它补上。要让存量库也拿到这张表，只能显式
/// <c>CREATE TABLE IF NOT EXISTS</c>。
/// </para>
/// <para>
/// <b>为什么现在有 ALTER，且只补列不回填：</b>本表先以 18 列上线（S1），随后前端管理视图需要
/// model_id / avatar_key / responsibility 三列。已经落库的 <c>agent_pool</c> 不会被
/// <c>CREATE TABLE IF NOT EXISTS</c> 改列，也无法走 EF migration（全仓 <c>Migrate()</c> 零调用），
/// 所以这三列只能在这里补。三列均可空、无默认值：旧行的 NULL 就是合法语义（「未设置」），
/// 不需要任何回填。
/// </para>
/// <para>
/// <b>幂等性：</b>建表与三处索引全部带 <c>IF NOT EXISTS</c>，启动多少次结果都一样。
/// 索引名是字面量，与 <see cref="AgentPoolEntity"/> 上的常量一字不差：新库走 EF 模型建索引、
/// 存量库走本类建索引，两边同名才不会在同一张表上留下两套索引；
/// 名字一旦漂移，<c>SchemaBootstrapperFreshDatabaseColumnTests</c> 里按常量名做的索引存在性断言会直接变红。
/// </para>
/// <para>
/// 这里的 SQL 刻意用 <c>const string</c> 而不是内插字符串：内插会触发 EF1002（SQL 注入分析器）警告，
/// 而这些语句里没有任何外部输入，用字面量既消警告也让 DDL 可以直接照抄进 PRAGMA 比对。
/// </para>
/// </summary>
public static class AgentPoolSchemaBootstrapper
{
    /// <summary>表名常量。</summary>
    public const string TableName = "agent_pool";

    /// <summary>
    /// 建表语句。列清单、顺序与 <see cref="AgentPoolEntity"/> 的属性声明逐一对应：
    /// 两条建表路径（EF / raw DDL）产出不同列集合时，查询会在其中一条路径上炸掉。
    /// <para>
    /// 类型只用 TEXT / INTEGER：SQLite 不执行长度约束，而 EF 为 MaxLength 生成的也是 TEXT；
    /// 若这里写 TEXT(64)，两条路径的 PRAGMA type 就会不同（text(64) vs TEXT），徒增比对噪声而无约束收益。
    /// </para>
    /// </summary>
    public const string CreateTableSql =
        """
        CREATE TABLE IF NOT EXISTS "agent_pool" (
            "Id"                  INTEGER NOT NULL CONSTRAINT "PK_agent_pool" PRIMARY KEY AUTOINCREMENT,
            "pool_id"             TEXT    NOT NULL,
            "owner_workspace_id"  TEXT    NOT NULL,
            "name"                TEXT    NOT NULL,
            "sub_session_id"      TEXT    NOT NULL,
            "template_id"         TEXT    NOT NULL,
            "role"                TEXT    NULL,
            "owner_session_id"    TEXT    NULL,
            "owner_agent_id"      TEXT    NULL,
            "active_execution_id" TEXT    NULL,
            "status"              TEXT    NOT NULL,
            "status_reason"       TEXT    NULL,
            "task_count"          INTEGER NOT NULL DEFAULT 0,
            "last_success"        INTEGER NULL,
            "created_at"          TEXT    NOT NULL,
            "last_used_at"        TEXT    NOT NULL,
            "version"             INTEGER NOT NULL DEFAULT 0,
            "deleted_at"          TEXT    NULL,
            "model_id"            TEXT    NULL,
            "avatar_key"          TEXT    NULL,
            "responsibility"      TEXT    NULL
        );
        """;

    /// <summary>
    /// name 的部分唯一索引：软删（deleted_at 非空）后同名可被新条目复用，
    /// 否则「删了就再也建不了同名池」。
    /// </summary>
    public const string CreateNameIndexSql =
        """
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_agent_pool_name"
            ON "agent_pool" ("name") WHERE "deleted_at" IS NULL;
        """;

    /// <summary>复用键全局唯一：同一会话不能被两个池条目同时认领，否则恢复会话时无法判定归属。</summary>
    public const string CreateSubSessionIndexSql =
        """
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_agent_pool_sub_session"
            ON "agent_pool" ("sub_session_id");
        """;

    /// <summary>选池主查询的覆盖索引：先按状态过滤，再按最近使用时间排序（LRU）。</summary>
    public const string CreateStatusLastUsedIndexSql =
        """
        CREATE INDEX IF NOT EXISTS "IX_agent_pool_status_last_used"
            ON "agent_pool" ("status", "last_used_at");
        """;

    /// <summary>默认模型路由列（存量库由本类 ALTER 补）。</summary>
    public const string ModelIdColumn = "model_id";

    /// <summary>预设图形键列（存量库由本类 ALTER 补）。</summary>
    public const string AvatarKeyColumn = "avatar_key";

    /// <summary>一句话职责列（存量库由本类 ALTER 补）。</summary>
    public const string ResponsibilityColumn = "responsibility";

    /// <summary>
    /// 补列语句。类型只用 TEXT（与 EF 为 <c>MaxLength</c> 生成的一致）：SQLite 不执行长度约束，
    /// 写成 TEXT(64) 只会让「EF 新库」与「存量库 ALTER」的 PRAGMA type 不同，徒增比对噪声。
    /// </summary>
    private const string AddModelIdSql =
        """
        ALTER TABLE "agent_pool"
        ADD COLUMN "model_id" TEXT NULL;
        """;

    private const string AddAvatarKeySql =
        """
        ALTER TABLE "agent_pool"
        ADD COLUMN "avatar_key" TEXT NULL;
        """;

    private const string AddResponsibilitySql =
        """
        ALTER TABLE "agent_pool"
        ADD COLUMN "responsibility" TEXT NULL;
        """;

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        await db.Database.ExecuteSqlRawAsync(CreateTableSql, ct);
        await db.Database.ExecuteSqlRawAsync(CreateNameIndexSql, ct);
        await db.Database.ExecuteSqlRawAsync(CreateSubSessionIndexSql, ct);
        await db.Database.ExecuteSqlRawAsync(CreateStatusLastUsedIndexSql, ct);

        // 存量库补列：CREATE TABLE IF NOT EXISTS 对「已经存在」的表是空操作，
        // 所以新增的三列只能逐列 PRAGMA 守卫后再 ALTER（SQLite 没有 ADD COLUMN IF NOT EXISTS）。
        await EnsureColumnAsync(db, ModelIdColumn, AddModelIdSql, logger, ct);
        await EnsureColumnAsync(db, AvatarKeyColumn, AddAvatarKeySql, logger, ct);
        await EnsureColumnAsync(db, ResponsibilityColumn, AddResponsibilitySql, logger, ct);

        logger?.LogDebug("[AgentPoolSchema] Ensured table {Table}, its indexes and the identity columns", TableName);
    }

    /// <summary>
    /// 缺列才 ALTER。守卫不可省：SQLite 不支持 <c>ADD COLUMN IF NOT EXISTS</c>，
    /// 重复执行会直接报 “duplicate column name”，启动第二次就失败。
    /// </summary>
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
            "[AgentPoolSchema] Added {Table}.{Column}",
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

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
