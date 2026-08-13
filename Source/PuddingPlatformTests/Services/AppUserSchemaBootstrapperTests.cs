using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AppUserSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_AddsAvatarToLegacyAppUsersTable()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await AppUserSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(scope.Db, "AppUsers", "Avatar"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await AppUserSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await AppUserSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(scope.Db, "AppUsers", "Avatar"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_AcceptsFreshPlatformDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await AppUserSchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.IsTrue(await ColumnExistsAsync(db, "AppUsers", "Avatar"));
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
            CREATE TABLE "AppUsers" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AppUsers" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL
            );
            """);
        return new TestDatabaseScope(connection, db);
    }

    private static async Task<bool> ColumnExistsAsync(
        DbContext db,
        string tableName,
        string columnName)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
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
