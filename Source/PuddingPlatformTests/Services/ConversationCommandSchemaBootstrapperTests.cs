using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ConversationCommandSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_AddsMetadataColumnToLegacyCommandTable()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "chat_execution_commands",
            "metadata_json"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await ColumnExistsAsync(
            scope.Db,
            "chat_execution_commands",
            "metadata_json"));
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

        await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(db);

        Assert.IsTrue(await ColumnExistsAsync(
            db,
            "chat_execution_commands",
            "metadata_json"));
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
            CREATE TABLE "chat_execution_commands" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_chat_execution_commands" PRIMARY KEY AUTOINCREMENT,
                "command_id" TEXT NOT NULL
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
