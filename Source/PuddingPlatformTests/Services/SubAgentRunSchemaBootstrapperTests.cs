using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SubAgentRunSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_AddsParentExecutionIdentityColumnsAndIndexToLegacyRunTable()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "sub_agent_runs",
            SubAgentRunSchemaBootstrapper.ParentTurnIdColumn));
        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "sub_agent_runs",
            SubAgentRunSchemaBootstrapper.ParentCommandIdColumn));
        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "sub_agent_runs",
            SubAgentRunSchemaBootstrapper.ParentRunIdColumn));
        Assert.IsTrue(await IndexExistsAsync(
            scope.Db,
            SubAgentRunSchemaBootstrapper.ParentTurnStatusIndex));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

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

        // 旧库没有子代理索引表时，schema 升级不得让启动失败。
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsFalse(await TableExistsAsync(scope.Db, "sub_agent_runs"));
    }

    private static async Task<TestDatabaseScope> CreateLegacyDatabaseAsync()
    {
        var scope = await CreateEmptyDatabaseAsync();
        await scope.Db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "sub_agent_runs" (
                "Id"                INTEGER NOT NULL CONSTRAINT "PK_sub_agent_runs" PRIMARY KEY AUTOINCREMENT,
                "run_id"            TEXT    NOT NULL,
                "parent_session_id" TEXT    NOT NULL,
                "status"            TEXT    NOT NULL DEFAULT 'running'
            );
            """);
        return scope;
    }

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
