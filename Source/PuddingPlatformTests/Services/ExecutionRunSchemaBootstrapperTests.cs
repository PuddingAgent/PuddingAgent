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
