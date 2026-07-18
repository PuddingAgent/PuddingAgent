using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class MemoryDatabaseInitializationTests
{
    [TestMethod]
    public async Task CoreInitializer_RepairsDatabaseAlreadyCreatedByLibraryContext()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            $"pudding-shared-memory-{Guid.NewGuid():N}.db");

        try
        {
            var connectionString = $"Data Source={dbPath}";
            var libraryOptions = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var coreOptions = new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite(connectionString)
                .Options;

            var libraryFactory = new TestDbContextFactory<MemoryLibraryDbContext>(
                libraryOptions,
                options => new MemoryLibraryDbContext(options));
            var coreFactory = new TestDbContextFactory<MemoryDbContext>(
                coreOptions,
                options => new MemoryDbContext(options));

            await MemoryLibraryDbInitializer.InitializeAsync(libraryFactory);
            Assert.IsFalse(await TableExistsAsync(connectionString, "Messages"));

            await MemoryDbInitializer.InitializeAsync(coreFactory);

            foreach (var table in new[]
                     {
                         "Sessions",
                         "Messages",
                         "Memories",
                         "SubconsciousJobs",
                         "EventQueue",
                         "EventDiagnosticLogs",
                         "AgentCheckpoints",
                         "EventSubscriptions",
                     })
            {
                Assert.IsTrue(
                    await TableExistsAsync(connectionString, table),
                    $"Expected core memory table '{table}' to exist.");
            }

            await MemoryDbInitializer.InitializeAsync(coreFactory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static async Task<bool> TableExistsAsync(
        string connectionString,
        string tableName)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type IN ('table', 'view') AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private sealed class TestDbContextFactory<TContext>(
        DbContextOptions<TContext> options,
        Func<DbContextOptions<TContext>, TContext> create)
        : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        public TContext CreateDbContext() => create(options);
    }
}
