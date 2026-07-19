using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TokenUsageSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_UpgradesLegacyTableWithColumnAndIndex()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(scope.Db, "TokenUsageEvents", "ParentSessionId"));
        Assert.IsTrue(await IndexExistsAsync(scope.Db, "IX_TokenUsageEvents_ParentSessionId"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(scope.Db, "TokenUsageEvents", "ParentSessionId"));
        Assert.IsTrue(await IndexExistsAsync(scope.Db, "IX_TokenUsageEvents_ParentSessionId"));
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
            CREATE TABLE "TokenUsageEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TokenUsageEvents" PRIMARY KEY AUTOINCREMENT,
                "SessionId" TEXT NULL
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

    private static async Task<bool> IndexExistsAsync(DbContext db, string indexName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
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
