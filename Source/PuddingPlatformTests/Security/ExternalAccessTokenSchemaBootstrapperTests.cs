using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Security;

namespace PuddingPlatformTests.Security;

[TestClass]
public sealed class ExternalAccessTokenSchemaBootstrapperTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_CreatesAllFourTables_Idempotent()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();

        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            // 第二次执行必须幂等（既有库启动路径）。
            await ExternalAccessTokenSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        var tables = await ListTablesAsync(harness);
        CollectionAssert.Contains(tables, "external_access_tokens");
        CollectionAssert.Contains(tables, "external_access_token_scopes");
        CollectionAssert.Contains(tables, "external_access_token_workspaces");
        CollectionAssert.Contains(tables, "external_access_token_audit_events");
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_UpgradesLegacyDatabaseWithoutTokenTables()
    {
        // 模拟已有 platform.db（无 Token 表）的原地升级路径。
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE legacy_table (id TEXT NOT NULL PRIMARY KEY);
            """);

        await ExternalAccessTokenSchemaBootstrapper.EnsureCreatedAsync(db);
        await ExternalAccessTokenSchemaBootstrapper.EnsureCreatedAsync(db);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'external_access%';";
        await using var reader = await command.ExecuteReaderAsync();
        var found = new List<string>();
        while (await reader.ReadAsync())
            found.Add(reader.GetString(0));

        Assert.AreEqual(4, found.Count);
        await connection.DisposeAsync();
    }

    private static async Task<List<string>> ListTablesAsync(ExternalAccessTokenTestHarness harness)
    {
        await using var db = await harness.Factory.CreateDbContextAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }
}
