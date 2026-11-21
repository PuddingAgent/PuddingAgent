using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// <c>session_sub_agents</c> 人类可读身份三列（display_name / avatar_key / responsibility）的 schema 验收。
/// <para>
/// 本项目运行时<b>从不执行 EF migration</b>（全仓 <c>Migrate()</c> 零调用），只有 <c>EnsureCreated</c>；
/// 而 <c>EnsureCreated</c> 对「已经存在的库」什么都不做。于是这里必须同时锁死两条建列路径：
/// </para>
/// <list type="number">
/// <item>新库 —— 列来自 <see cref="PuddingPlatform.Data.Entities.SessionSubAgentEntity"/> 的实体声明；</item>
/// <item>存量库 —— 列来自 <see cref="SessionSubAgentIdentitySchemaBootstrapper"/> 的 PRAGMA + ALTER。</item>
/// </list>
/// <para>
/// 两条路径产出的列集合若漂移，查询会在其中一条路径上炸掉（有的库有列、有的库没有），
/// 所以最后一条用例做列集合与列类型的双向相等断言。
/// </para>
/// </summary>
[TestClass]
public sealed class SessionSubAgentIdentitySchemaBootstrapperTests
{
    private const string Table = SessionSubAgentIdentitySchemaBootstrapper.TableName;

    private static readonly string[] IdentityColumns =
    [
        SessionSubAgentIdentitySchemaBootstrapper.DisplayNameColumn,
        SessionSubAgentIdentitySchemaBootstrapper.AvatarKeyColumn,
        SessionSubAgentIdentitySchemaBootstrapper.ResponsibilityColumn,
    ];

    /// <summary>EF 实体声明建出的 14 列（本特性之前的历史结构，用于模拟存量库）。</summary>
    private const string LegacyCreateTableSql =
        """
        CREATE TABLE "session_sub_agents" (
            "Id"                INTEGER NOT NULL CONSTRAINT "PK_session_sub_agents" PRIMARY KEY AUTOINCREMENT,
            "parent_session_id" TEXT    NOT NULL,
            "parent_agent_id"   TEXT    NULL,
            "sub_session_id"    TEXT    NOT NULL,
            "Status"            TEXT    NOT NULL,
            "template_id"       TEXT    NULL,
            "model_id"          TEXT    NULL,
            "task_summary"      TEXT    NOT NULL,
            "spawned_at"        TEXT    NOT NULL,
            "completed_at"      TEXT    NULL,
            "Success"           INTEGER NULL,
            "reply_summary"     TEXT    NULL,
            "error_summary"     TEXT    NULL,
            "full_result_json"  TEXT    NULL
        );
        """;

    [TestMethod]
    public async Task EfFreshDatabase_DeclaresAllThreeIdentityColumnsAsNullable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);

        await db.Database.EnsureCreatedAsync();

        var columns = await ReadColumnsAsync(db, Table);
        foreach (var column in IdentityColumns)
        {
            Assert.IsTrue(
                columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)),
                $"新库 {Table} 缺少 {column}（EF 实体声明未生效）。实际列: [{string.Join(", ", columns.Select(c => c.Name))}]");
            var actual = columns.Single(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("TEXT", actual.Type, $"{column} 的类型必须是 TEXT。");
            Assert.IsFalse(actual.NotNull, $"{column} 必须可空：存量行没有它，非空会让升级直接失败。");
        }
    }

    [TestMethod]
    public async Task LegacyDatabase_BootstrapperAddsIdentityColumns_AndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);

        // 刻意不跑 EnsureCreated：模拟「表早已存在、但没有新列」的存量库。
        await db.Database.ExecuteSqlRawAsync(LegacyCreateTableSql);

        var before = await ReadColumnsAsync(db, Table);
        foreach (var column in IdentityColumns)
        {
            Assert.IsFalse(
                before.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)),
                $"前置条件不成立：存量库不应一开始就有 {column}。");
        }

        await SessionSubAgentIdentitySchemaBootstrapper.EnsureCreatedAsync(db);
        var afterFirst = await ReadColumnsAsync(db, Table);
        foreach (var column in IdentityColumns)
        {
            Assert.IsTrue(
                afterFirst.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)),
                $"bootstrapper 未补出 {column}。实际列: [{string.Join(", ", afterFirst.Select(c => c.Name))}]");
        }

        // 第二次执行必须既不抛 "duplicate column name"，也不改变列集合。
        await SessionSubAgentIdentitySchemaBootstrapper.EnsureCreatedAsync(db);
        var afterSecond = await ReadColumnsAsync(db, Table);

        CollectionAssert.AreEquivalent(
            afterFirst.Select(c => c.Name).ToList(),
            afterSecond.Select(c => c.Name).ToList(),
            "bootstrapper 重复执行后列集合必须不变（幂等）。");
    }

    [TestMethod]
    public async Task EfFreshDatabase_And_LegacyPlusBootstrapper_ProduceIdenticalColumns()
    {
        // 路径 ①：EF EnsureCreated（新库）。
        await using var freshConnection = new SqliteConnection("Data Source=:memory:");
        await freshConnection.OpenAsync();
        await using var freshDb = CreateDbContext(freshConnection);
        await freshDb.Database.EnsureCreatedAsync();
        // 新库也跑一遍 bootstrapper：走 PRAGMA 命中分支，必须是无副作用 no-op。
        await SessionSubAgentIdentitySchemaBootstrapper.EnsureCreatedAsync(freshDb);
        var efColumns = await ReadColumnsAsync(freshDb, Table);

        // 路径 ②：存量旧表 + bootstrapper ALTER。
        await using var legacyConnection = new SqliteConnection("Data Source=:memory:");
        await legacyConnection.OpenAsync();
        await using var legacyDb = CreateDbContext(legacyConnection);
        await legacyDb.Database.ExecuteSqlRawAsync(LegacyCreateTableSql);
        await SessionSubAgentIdentitySchemaBootstrapper.EnsureCreatedAsync(legacyDb);
        var ddlColumns = await ReadColumnsAsync(legacyDb, Table);

        CollectionAssert.AreEquivalent(
            efColumns.Select(c => c.Name).ToList(),
            ddlColumns.Select(c => c.Name).ToList(),
            "EF 新库与「旧表 + bootstrapper」的列名集合必须一致。");

        CollectionAssert.AreEquivalent(
            efColumns.Select(c => $"{c.Name}:{c.Type}").ToList(),
            ddlColumns.Select(c => $"{c.Name}:{c.Type}").ToList(),
            "两条建列路径的列类型也必须一致（TEXT vs TEXT(64) 之类的漂移会让比对与查询都变味）。");
    }

    [TestMethod]
    public async Task Bootstrapper_WhenTableMissing_DoesNotThrow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);

        // 比 session_sub_agents 更早的库：没有可升级对象，也不能让启动失败。
        await SessionSubAgentIdentitySchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.IsFalse(await TableExistsAsync(db, Table));
    }

    private static PlatformDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        return new PlatformDbContext(options);
    }

    private static async Task<List<(string Name, string Type, bool NotNull)>> ReadColumnsAsync(
        DbContext db,
        string table)
    {
        var columns = new List<(string, string, bool)>();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // PRAGMA table_info：0=cid 1=name 2=type 3=notnull 4=dflt_value 5=pk
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
        }

        return columns;
    }

    private static async Task<bool> TableExistsAsync(DbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync();
        return value is not null && value is not DBNull;
    }
}
