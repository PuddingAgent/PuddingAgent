using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Execution;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ExecutionRunSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_AddsTraceIdColumnToLegacyRunTable()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "execution_runs",
            "trace_id"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "execution_runs",
            "trace_id"));
    }

    /// <summary>
    /// NC-01：审批暂停恢复点是独立列（metadata_json 上限 4096，装不下恢复点），
    /// 历史库必须被幂等补列。
    /// </summary>
    [TestMethod]
    public async Task EnsureCreatedAsync_AddsApprovalResumeColumnToLegacyCommandTable()
    {
        await using var scope = await CreateLegacyDatabaseAsync();
        await scope.Db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "chat_execution_commands" (
                "id" INTEGER NOT NULL CONSTRAINT "PK_chat_execution_commands" PRIMARY KEY AUTOINCREMENT,
                "command_id" TEXT NOT NULL
            );
            """);

        await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "chat_execution_commands",
            "approval_resume_json"));
    }

    /// <summary>缺少 chat_execution_commands 表时跳过补列，不因 ALTER 不存在的表而失败。</summary>
    [TestMethod]
    public async Task EnsureCreatedAsync_SkipsMissingTableWithoutFailing()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsFalse(await ColumnExistsAsync(
            scope.Db,
            "chat_execution_commands",
            "approval_resume_json"));
    }

    private static async Task<TestDatabaseScope> CreateLegacyDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "execution_runs" (
                "fencing_token" INTEGER NOT NULL CONSTRAINT "PK_execution_runs" PRIMARY KEY AUTOINCREMENT,
                "run_id" TEXT NOT NULL
            );
            """);

        return new TestDatabaseScope(connection, db);
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
