using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PuddingPlatform.Data;

namespace PuddingPlatformTests.Infrastructure;

[TestClass]
public sealed class PlatformMigrationTests
{
    [TestMethod]
    public async Task MigrateAsync_CreatesTokenUsageStatsTable()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-platform-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "platform.db");

        try
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.IsTrue(await TableExistsAsync(db.Database.GetDbConnection(), "TokenUsageStats"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string tableName)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value) > 0;
    }
}
