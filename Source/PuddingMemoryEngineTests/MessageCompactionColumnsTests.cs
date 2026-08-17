using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngineTests;

/// <summary>
/// P1-1 Task B：MessageEntity 增 ContextGeneration / CanonicalContentHash 两列（设计方案 §6.3）。
/// 验证 EF 映射 / DDL / 幂等自愈迁移三处同步一致，以及完整读写往返。
/// </summary>
[TestClass]
public sealed class MessageCompactionColumnsTests
{
    // ── Test Infrastructure ────────────────────────────────────────────

    private static DbContextOptions<MemoryDbContext> CreateFileOptions(string dbPath)
        => new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .EnableSensitiveDataLogging()
            .Options;

    private static string CreateTempDbPath()
        => Path.Combine(Path.GetTempPath(), $"pudding-msg-compaction-{Guid.NewGuid():N}.db");

    private static async Task<string> InitializeFileDbAsync()
    {
        var dbPath = CreateTempDbPath();
        var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
        await MemoryDbInitializer.InitializeAsync(factory);
        return dbPath;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        private readonly DbContextOptions<MemoryDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MemoryDbContext> options)
        {
            _options = options;
        }

        public MemoryDbContext CreateDbContext() => new(_options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryDbContext(_options));
    }

    // ── a) 初始化器跑两遍幂等，Messages 表两列存在 ───────────────────────

    [TestMethod]
    public async Task InitializeSchema_Twice_MessagesCompactionColumnsShouldExist()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));

            await MemoryDbInitializer.InitializeAsync(factory);
            await MemoryDbInitializer.InitializeAsync(factory);

            await using var db = factory.CreateDbContext();
            Assert.IsTrue(
                await ColumnExistsAsync(db, "ContextGeneration"),
                "初始化后 Messages 表应存在 ContextGeneration 列");
            Assert.IsTrue(
                await ColumnExistsAsync(db, "CanonicalContentHash"),
                "初始化后 Messages 表应存在 CanonicalContentHash 列");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── b) 两字段完整写入 + 读回 ────────────────────────────────────────

    [TestMethod]
    public async Task InsertAndQuery_Message_ShouldRoundTripCompactionColumns()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            const int generation = 3;
            var hash = new string('a', 64);

            await using (var db = new TestDbContextFactory(CreateFileOptions(dbPath)).CreateDbContext())
            {
                db.Sessions.Add(new SessionEntity
                {
                    SessionId = sessionId,
                    WorkspaceId = "ws",
                    AgentId = "ag",
                });
                db.Messages.Add(new MessageEntity
                {
                    SessionId = sessionId,
                    Sequence = 1,
                    Role = "user",
                    Content = "hello",
                    ContextGeneration = generation,
                    CanonicalContentHash = hash,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new TestDbContextFactory(CreateFileOptions(dbPath)).CreateDbContext())
            {
                var msg = await db.Messages.SingleAsync(m => m.SessionId == sessionId);
                Assert.AreEqual(generation, msg.ContextGeneration);
                Assert.AreEqual(hash, msg.CanonicalContentHash);
            }
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── c) 两字段可空（未赋值时为 null）────────────────────────────────

    [TestMethod]
    public async Task Message_CompactionColumns_ShouldBeNullableByDefault()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");

            await using (var db = new TestDbContextFactory(CreateFileOptions(dbPath)).CreateDbContext())
            {
                db.Sessions.Add(new SessionEntity
                {
                    SessionId = sessionId,
                    WorkspaceId = "ws",
                    AgentId = "ag",
                });
                db.Messages.Add(new MessageEntity
                {
                    SessionId = sessionId,
                    Sequence = 1,
                    Role = "user",
                    Content = "plain message without compaction",
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new TestDbContextFactory(CreateFileOptions(dbPath)).CreateDbContext())
            {
                var msg = await db.Messages.SingleAsync(m => m.SessionId == sessionId);
                Assert.IsNull(msg.ContextGeneration);
                Assert.IsNull(msg.CanonicalContentHash);
            }
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<bool> ColumnExistsAsync(MemoryDbContext db, string column)
    {
        var found = await db.Database
            .SqlQueryRaw<string>(
                "SELECT name FROM pragma_table_info('Messages') WHERE name = {0}",
                column)
            .ToListAsync();
        return found.Count > 0;
    }

    private static void CleanupDbFile(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup for pooled SQLite handles on Windows.
        }
    }
}
