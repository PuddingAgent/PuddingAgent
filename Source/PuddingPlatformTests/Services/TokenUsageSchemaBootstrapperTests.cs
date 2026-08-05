using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TokenUsageSchemaBootstrapperTests
{
    /// <summary>
    /// Nullable columns the bootstrapper must self-heal on legacy SQLite databases.
    /// Mirrors TokenUsageSchemaBootstrapper.RequiredColumns.
    /// </summary>
    private static readonly string[] ExpectedColumns =
    [
        "ParentSessionId",
        "HistoryMessageEntropy",
        "SystemMessageEntropy",
        "ToolDefinitionEntropy",
        "TurnRound",
        "ToolCallCount",
        "ToolNames",
        "SubAgentId",
    ];

    [TestMethod]
    public async Task EnsureCreatedAsync_UpgradesLegacyTableWithColumnsAndIndex()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        foreach (var column in ExpectedColumns)
            Assert.IsTrue(await ColumnExistsAsync(scope.Db, "TokenUsageEvents", column),
                $"Expected column '{column}' to be added.");

        Assert.IsTrue(await IndexExistsAsync(scope.Db, "IX_TokenUsageEvents_ParentSessionId"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateLegacyDatabaseAsync();

        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        foreach (var column in ExpectedColumns)
            Assert.IsTrue(await ColumnExistsAsync(scope.Db, "TokenUsageEvents", column),
                $"Expected column '{column}' to survive a second run.");

        Assert.IsTrue(await IndexExistsAsync(scope.Db, "IX_TokenUsageEvents_ParentSessionId"));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_ColumnAlreadyExists_IsNoOp()
    {
        await using var scope = await CreateLegacyDatabaseAsync(
            extraColumns: ExpectedColumns.Select(c => $"\"{c}\" TEXT NULL"));

        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        foreach (var column in ExpectedColumns)
            Assert.IsTrue(await ColumnExistsAsync(scope.Db, "TokenUsageEvents", column),
                $"Expected pre-existing column '{column}' to remain untouched.");
    }

    private static async Task<TestDatabaseScope> CreateLegacyDatabaseAsync(
        IEnumerable<string>? extraColumns = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);

        var extra = extraColumns is null ? string.Empty : ", " + string.Join(", ", extraColumns);
#pragma warning disable EF1002 // Test-only SQL; extra columns come from the fixed ExpectedColumns constants
        await db.Database.ExecuteSqlRawAsync(
            $"""
            CREATE TABLE "TokenUsageEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TokenUsageEvents" PRIMARY KEY AUTOINCREMENT,
                "SessionId" TEXT NULL{extra}
            );
            """);
#pragma warning restore EF1002

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
