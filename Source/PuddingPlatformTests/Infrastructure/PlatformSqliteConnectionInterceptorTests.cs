using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingPlatformTests.Infrastructure;

[TestClass]
public sealed class PlatformSqliteConnectionInterceptorTests
{
    [TestMethod]
    public async Task OpenConnectionAsync_AppliesPlatformPragmas()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-platform-pragmas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "platform.db");

        try
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(new PlatformSqliteConnectionInterceptor())
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                await db.Database.OpenConnectionAsync();

                Assert.AreEqual(1, await ExecuteScalarIntAsync(db.Database.GetDbConnection(), "PRAGMA synchronous;"));
                Assert.AreEqual(2, await ExecuteScalarIntAsync(db.Database.GetDbConnection(), "PRAGMA temp_store;"));
                Assert.AreEqual(5000, await ExecuteScalarIntAsync(db.Database.GetDbConnection(), "PRAGMA busy_timeout;"));
                Assert.AreEqual(4000, await ExecuteScalarIntAsync(db.Database.GetDbConnection(), "PRAGMA wal_autocheckpoint;"));
                await db.Database.CloseConnectionAsync();
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

    private static async Task<int> ExecuteScalarIntAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }
}
