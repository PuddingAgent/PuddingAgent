using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// agent_pool schema bootstrap 的两条建表路径验收。
/// <para>
/// 为什么这组用例必须存在：本项目不用 EF Migrations，<c>EnsureCreated</c> 只在「库里一张表都没有」时
/// 建整套 schema —— 存量平台库早就有了表，所以新加的 <c>agent_pool</c> 只能由
/// <see cref="AgentPoolSchemaBootstrapper"/> 的 raw DDL 补出来。这里锁定三件事：
/// ① 库里没有该表时 bootstrapper 真的能把表建出来（存量库路径）；
/// ② raw DDL 与 EF 模型产出的列集合、索引名完全一致（两条路径漂移会让查询在某一侧炸掉）；
/// ③ 重复调用幂等，不会在同一张表上叠出第二套索引。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolSchemaBootstrapperTests
{
    /// <summary>当前定稿的 21 列，顺序与实体属性声明一致（三列身份信息追加在末尾）。</summary>
    private static readonly string[] ExpectedColumns =
    [
        "Id", "pool_id", "owner_workspace_id", "name", "sub_session_id", "template_id",
        "role", "owner_session_id", "owner_agent_id", "active_execution_id",
        "status", "status_reason", "task_count", "last_success",
        "created_at", "last_used_at", "version", "deleted_at",
        "model_id", "avatar_key", "responsibility",
    ];

    /// <summary>
    /// 存量库的 18 列（本表首次上线时的形态）。新加的三列不在其中，
    /// 用来证明「补列」这条路径真实存在，而不是被 CREATE TABLE 顺手带出来的。
    /// </summary>
    private static readonly string[] LegacyColumns =
    [
        "Id", "pool_id", "owner_workspace_id", "name", "sub_session_id", "template_id",
        "role", "owner_session_id", "owner_agent_id", "active_execution_id",
        "status", "status_reason", "task_count", "last_success",
        "created_at", "last_used_at", "version", "deleted_at",
    ];

    /// <summary>
    /// 18 列旧表的建表语句（逐字复制自补列之前的 <c>CreateTableSql</c>）。
    /// 必须手写、不能复用 <see cref="AgentPoolSchemaBootstrapper.CreateTableSql"/>：
    /// 后者现在已含三列，用它建出来的表根本不需要补列，用例会变成空跑。
    /// </summary>
    private const string LegacyCreateTableSql =
        """
        CREATE TABLE "agent_pool" (
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
            "deleted_at"          TEXT    NULL
        );
        """;

    private static readonly string[] ExpectedIndexes =
    [
        AgentPoolEntity.NameIndexName,
        AgentPoolEntity.SubSessionIndexName,
        AgentPoolEntity.StatusLastUsedIndexName,
    ];

    [TestMethod]
    public async Task ExistingDatabase_WithoutTable_BootstrapperCreatesColumnsAndIndexes()
    {
        await using var scope = await CreateEmptyDatabaseAsync();

        // 前置：空库里没有 agent_pool —— 这正是存量平台库的状态（其它表都在，只缺新表）。
        Assert.IsFalse(
            await TableExistsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName),
            "前置条件不成立：空库里不该已经有 agent_pool");

        await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(
            await TableExistsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName),
            "存量库路径下 agent_pool 必须由 bootstrapper 的 CREATE TABLE IF NOT EXISTS 建出");

        var columns = await ReadColumnsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName);
        Console.WriteLine($"[evidence] agent_pool columns = {string.Join(", ", columns)}");
        CollectionAssert.AreEquivalent(
            ExpectedColumns,
            columns,
            $"agent_pool 列集合必须与任务书定稿一致；实际: [{string.Join(", ", columns)}]");

        var indexes = await ReadIndexNamesAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName);
        Console.WriteLine($"[evidence] agent_pool indexes = {string.Join(", ", indexes)}");
        CollectionAssert.AreEquivalent(ExpectedIndexes, indexes, "三条索引必须全部由 bootstrapper 建出");

        // 只看名字不够：软删后复用同名池完全依赖「name 索引是带过滤的部分唯一索引」。
        // 若某一条建表路径把 WHERE 丢了，两条路径的语义就会静默不一致。
        var nameIndex = (await ReadIndexFlagsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName))
            .Single(x => x.Name == AgentPoolEntity.NameIndexName);
        Console.WriteLine($"[evidence] {nameIndex}");
        Assert.IsTrue(nameIndex.Unique, "IX_agent_pool_name 必须唯一");
        Assert.IsTrue(
            nameIndex.Partial,
            "IX_agent_pool_name 必须是带 WHERE \"deleted_at\" IS NULL 的部分索引，否则软删后无法复用同名池");

        // 幂等：第二遍不得抛错，也不得改变列集合或索引集合。
        await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        CollectionAssert.AreEquivalent(
            columns,
            await ReadColumnsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName),
            "重复 bootstrap 后列集合必须不变");
        CollectionAssert.AreEquivalent(
            indexes,
            await ReadIndexNamesAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName),
            "重复 bootstrap 后索引集合必须不变（不得叠出第二套索引）");
    }

    /// <summary>
    /// 存量库路径：库里已经有 18 列的 agent_pool（正是本表首次上线后的真实状态），
    /// 启动时 bootstrapper 必须补出三列，且第二次启动不得报 duplicate column name。
    /// <para>
    /// 这条用例的变异靶点就是 <c>EnsureColumnAsync</c> 里的 <c>PRAGMA table_info</c> 守卫：
    /// 改成无条件 ALTER 后，第二遍 bootstrap 会抛 “duplicate column name: model_id”，
    /// 用例立刻变红。SQLite 不支持 ADD COLUMN IF NOT EXISTS，所以守卫不是防御性代码，是必需条件。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ExistingLegacyTable_With18Columns_BootstrapperAddsThreeColumns_Idempotently()
    {
        await using var scope = await CreateEmptyDatabaseAsync();

        // 夹具：手工建 18 列旧表（不走 bootstrapper，否则就没有「缺列」这个前提了）。
        await ExecuteAsync(scope.Db, LegacyCreateTableSql);
        var before = await ReadColumnsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName);
        Console.WriteLine($"[evidence] legacy columns before = {string.Join(", ", before)}");
        CollectionAssert.AreEquivalent(
            LegacyColumns,
            before,
            $"夹具必须是 18 列旧表；实际: [{string.Join(", ", before)}]");

        await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        var after = await ReadColumnsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName);
        Console.WriteLine($"[evidence] columns after bootstrap = {string.Join(", ", after)}");
        CollectionAssert.AreEquivalent(
            ExpectedColumns,
            after,
            $"存量库补列后必须是 21 列；实际: [{string.Join(", ", after)}]");

        // 幂等：第二次启动不得抛错（无条件 ALTER 会在这里抛 duplicate column name）。
        await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        CollectionAssert.AreEquivalent(
            after,
            await ReadColumnsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName),
            "重复 bootstrap 后列集合必须不变");

        // 两路一致：ALTER 补出来的三列与 EF 新库建出来的三列，列定义必须逐列相同
        // （名 / 类型 / NOT NULL / DEFAULT / PK）。类型写成 TEXT(64) 或带默认值都会在这里变红。
        await using var freshScope = await CreateEmptyDatabaseAsync();
        await freshScope.Db.Database.EnsureCreatedAsync();
        await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(freshScope.Db);

        var efDetails = await ReadColumnDetailsAsync(freshScope.Db, AgentPoolSchemaBootstrapper.TableName);
        var alteredDetails = await ReadColumnDetailsAsync(scope.Db, AgentPoolSchemaBootstrapper.TableName);
        Console.WriteLine($"[evidence] EF columns = {string.Join("; ", efDetails)}");
        Console.WriteLine($"[evidence] ALTERed legacy columns = {string.Join("; ", alteredDetails)}");
        CollectionAssert.AreEquivalent(
            efDetails,
            alteredDetails,
            "EF 新库与「旧表 + 补列」的列定义必须逐列一致");
    }

    [TestMethod]
    public async Task EfEnsuredTable_AndRawDdlTable_HaveIdenticalColumnsAndIndexes()
    {
        // 路径 A：全新库 —— EnsureCreated 依实体声明建表，随后启动仍会跑一遍 bootstrapper。
        await using var freshScope = await CreateEmptyDatabaseAsync();
        await freshScope.Db.Database.EnsureCreatedAsync();
        await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(freshScope.Db);

        // 路径 B：存量库 —— 只有 raw DDL（上面的第一个用例已证明它会建表）。
        await using var legacyScope = await CreateEmptyDatabaseAsync();
        await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(legacyScope.Db);

        var efColumns = await ReadColumnsAsync(freshScope.Db, AgentPoolSchemaBootstrapper.TableName);
        var ddlColumns = await ReadColumnsAsync(legacyScope.Db, AgentPoolSchemaBootstrapper.TableName);
        CollectionAssert.AreEquivalent(
            efColumns,
            ddlColumns,
            $"EF 模型与 raw DDL 的列集合必须一致；EF: [{string.Join(", ", efColumns)}]；" +
            $"DDL: [{string.Join(", ", ddlColumns)}]");

        // 名称一致还不够：类型 / NOT NULL / DEFAULT / PK 任一不一致，同一句 SQL 就会在某一条路径上失败。
        var efDetails = await ReadColumnDetailsAsync(freshScope.Db, AgentPoolSchemaBootstrapper.TableName);
        var ddlDetails = await ReadColumnDetailsAsync(legacyScope.Db, AgentPoolSchemaBootstrapper.TableName);
        Console.WriteLine($"[evidence] EF columns = {string.Join("; ", efDetails)}");
        Console.WriteLine($"[evidence] DDL columns = {string.Join("; ", ddlDetails)}");
        CollectionAssert.AreEquivalent(
            efDetails,
            ddlDetails,
            "EF 模型与 raw DDL 的列定义（名 + 类型 + NOT NULL + DEFAULT + PK）必须逐列一致");

        // 索引必须连 unique / partial 一起对齐：只比名字的话，某条路径丢了 WHERE 子句也不会被发现。
        var efIndexes = await ReadIndexFlagsAsync(freshScope.Db, AgentPoolSchemaBootstrapper.TableName);
        var ddlIndexes = await ReadIndexFlagsAsync(legacyScope.Db, AgentPoolSchemaBootstrapper.TableName);
        Console.WriteLine($"[evidence] EF indexes = {string.Join("; ", efIndexes)}");
        Console.WriteLine($"[evidence] DDL indexes = {string.Join("; ", ddlIndexes)}");
        CollectionAssert.AreEquivalent(
            efIndexes,
            ddlIndexes,
            "EF 模型与 raw DDL 的索引（名字 + unique + partial）必须一致；" +
            $"EF: [{string.Join("; ", efIndexes)}]；DDL: [{string.Join("; ", ddlIndexes)}]");
        CollectionAssert.AreEquivalent(
            ExpectedIndexes,
            efIndexes.Select(x => x.Name).ToList(),
            "两条路径都必须只有这三条索引");
    }

    private static async Task ExecuteAsync(DbContext db, string sql)
        => await db.Database.ExecuteSqlRawAsync(sql);

    private static async Task<TestDatabaseScope> CreateEmptyDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabaseScope(connection, new PlatformDbContext(options));
    }

    private static async Task<List<string>> ReadColumnsAsync(DbContext db, string table)
    {
        var columns = new List<string>();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>
    /// PRAGMA table_info 的完整列定义（名 / 类型 / NOT NULL / DEFAULT / PK），用于比两条建表路径。
    /// </summary>
    private static async Task<List<string>> ReadColumnDetailsAsync(DbContext db, string table)
    {
        var details = new List<string>();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // 列序：cid(0), name(1), type(2), notnull(3), dflt_value(4), pk(5)。
            var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var defaultValue = reader.IsDBNull(4) ? "-" : Convert.ToString(reader.GetValue(4))!;
            details.Add(
                $"{reader.GetString(1)}|{type}|notnull={reader.GetInt64(3)}|default={defaultValue}|pk={reader.GetInt64(5)}");
        }

        return details;
    }

    private static async Task<List<string>> ReadIndexNamesAsync(DbContext db, string table)
    {
        var indexes = new List<string>();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // PRAGMA index_list 列序：seq, name, unique, origin, partial。
            // 这里不做任何过滤：期望集合就是「表上实际存在的全部索引」，
            // 若两条建表路径各建一套或重复叠加，多出来的名字会直接让断言变红。
            indexes.Add(reader.GetString(1));
        }

        return indexes;
    }

    /// <summary>PRAGMA index_list 里的索引属性（name 之外的 unique / partial 两列）。</summary>
    private sealed record IndexFlags(string Name, bool Unique, bool Partial);

    private static async Task<List<IndexFlags>> ReadIndexFlagsAsync(DbContext db, string table)
    {
        var flags = new List<IndexFlags>();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // 列序：seq(0), name(1), unique(2), origin(3), partial(4)。
            flags.Add(new IndexFlags(
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                !reader.IsDBNull(4) && reader.GetInt64(4) != 0));
        }

        return flags;
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

    private sealed class TestDatabaseScope(
        SqliteConnection connection,
        PlatformDbContext db) : IAsyncDisposable
    {
        public PlatformDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
