using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// sub_agent_runs schema bootstrap 的「全新库」路径测试。
/// <para>
/// 2026-09-19 压缩决策：旧库一次性 ALTER 补列（parent_* 执行身份列）已删除——产品未发布，
/// 不存在需要升级的旧库；三列由 EF EnsureCreated 依 <see cref="SubAgentRunEntity"/> 声明建列。
/// 本类锁定：① 全新库上 bootstrap 后 EF 列真实存在、EF 未声明的复合索引被补建；
/// ② 重复调用幂等；③ 无表时静默跳过不失败。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentRunSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_OnFreshDatabase_ParentIdentityColumnsAndIndexExist()
    {
        await using var scope = await CreateFreshDatabaseAsync();

        await scope.Db.Database.EnsureCreatedAsync();
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "sub_agent_runs",
            SubAgentRunSchemaBootstrapper.ParentTurnIdColumn));
        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "sub_agent_runs",
            "parent_command_id"));
        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "sub_agent_runs",
            "parent_run_id"));
        Assert.IsTrue(await IndexExistsAsync(
            scope.Db,
            SubAgentRunSchemaBootstrapper.ParentTurnStatusIndex));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateFreshDatabaseAsync();

        await scope.Db.Database.EnsureCreatedAsync();
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "sub_agent_runs",
            SubAgentRunSchemaBootstrapper.ParentTurnIdColumn));
        Assert.IsTrue(await IndexExistsAsync(
            scope.Db,
            SubAgentRunSchemaBootstrapper.ParentTurnStatusIndex));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_SkipsWhenRunTableMissing()
    {
        await using var scope = await CreateEmptyDatabaseAsync();

        // 库里没有子代理索引表时，bootstrap 不得让启动失败。
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsFalse(await TableExistsAsync(scope.Db, "sub_agent_runs"));
    }

    private static async Task<TestDatabaseScope> CreateFreshDatabaseAsync()
        => await CreateEmptyDatabaseAsync();

    private static async Task<TestDatabaseScope> CreateEmptyDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        return new TestDatabaseScope(connection, db);
    }

    private static async Task<bool> TableExistsAsync(DbContext db, string tableName)
    {
        return await ScalarExistsAsync(
            db,
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;",
            tableName);
    }

    private static async Task<bool> IndexExistsAsync(DbContext db, string indexName)
    {
        return await ScalarExistsAsync(
            db,
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;",
            indexName);
    }

    private static async Task<bool> ScalarExistsAsync(DbContext db, string sql, string name)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync();
        return value is not null && value is not DBNull;
    }

    private static async Task<bool> ColumnExistsAsync(
        DbContext db,
        string tableName,
        string columnName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
