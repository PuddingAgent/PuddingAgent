using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngineTests;

/// <summary>
/// WP-L2a：SessionChunkVectors（会话块向量表）。
/// 验证 DbSet / EF 映射 / DDL 三处同步一致（memory 库 schema 由自定义 DDL 初始化器管理，非 EnsureCreated）。
/// </summary>
[TestClass]
public sealed class SessionChunkVectorsTests
{
    // ── Test Infrastructure ────────────────────────────────────────────

    private static DbContextOptions<MemoryLibraryDbContext> CreateFileOptions(string dbPath)
        => new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .EnableSensitiveDataLogging()
            .Options;

    private static string CreateTempDbPath()
        => Path.Combine(Path.GetTempPath(), $"pudding-session-chunk-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 通过 MemoryLibraryDbInitializer 建表（与生产启动路径一致）。
    /// 必须用文件库而非 InMemory：初始化器内部按连接字符串另开连接，InMemory 各连接互不可见。
    /// </summary>
    private static async Task<string> InitializeFileDbAsync()
    {
        var dbPath = CreateTempDbPath();
        var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
        await MemoryLibraryDbInitializer.InitializeAsync(factory);
        return dbPath;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }

    // ── a) 初始化器跑两遍幂等不抛异常 ──────────────────────────────────

    [TestMethod]
    public async Task InitializeSchema_Twice_ShouldBeIdempotent()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));

            // 第一遍建表
            await MemoryLibraryDbInitializer.InitializeAsync(factory);
            // 第二遍必须幂等、不抛异常
            await MemoryLibraryDbInitializer.InitializeAsync(factory);

            // 表确实存在（走 EF DbSet 查询路径验证 DbSet/映射/DDL 一致）
            await using var db = factory.CreateDbContext();
            var exists = await DbTableExistsAsync(db, "SessionChunkVectors");
            Assert.IsTrue(exists, "初始化后应存在 SessionChunkVectors 表");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── b) DbSet / 映射 / DDL 三处一致：插入 + 查询 ───────────────────

    [TestMethod]
    public async Task InsertAndQuery_SessionChunkVector_ShouldSucceed()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            await using var db = factory.CreateDbContext();

            // float32 × 1024 = 4096B，当前 lmstudio qwen3-0.6b 维度
            var embedding = new byte[1024 * 4];
            for (var i = 0; i < embedding.Length; i++)
                embedding[i] = (byte)(i % 251);

            var vector = new SessionChunkVectorEntity
            {
                WorkspaceId = "ws-1",
                SessionId = "session-abc",
                MessageId = "msg-001",
                ChunkSeq = 0,
                Role = "user",
                SourceText = "如何调优 MySQL 连接池？",
                Embedding = embedding,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            db.SessionChunkVectors.Add(vector);
            await db.SaveChangesAsync();

            var loaded = await db.SessionChunkVectors.SingleAsync(v => v.ChunkId == vector.ChunkId);
            Assert.AreEqual("ws-1", loaded.WorkspaceId);
            Assert.AreEqual("session-abc", loaded.SessionId);
            Assert.AreEqual("msg-001", loaded.MessageId);
            Assert.AreEqual(0, loaded.ChunkSeq);
            Assert.AreEqual("user", loaded.Role);
            Assert.AreEqual("如何调优 MySQL 连接池？", loaded.SourceText);
            Assert.IsNotNull(loaded.Embedding);
            Assert.AreEqual(1024 * 4, loaded.Embedding!.Length);
            Assert.IsTrue(embedding.SequenceEqual(loaded.Embedding), "Embedding 字节内容应一致");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── c) 重复 (MessageId, ChunkSeq) 违反唯一约束（回填幂等依赖） ──────

    [TestMethod]
    public async Task DuplicateMessageChunkSeq_ShouldViolateUniqueConstraint()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            await using var db = factory.CreateDbContext();

            db.SessionChunkVectors.Add(new SessionChunkVectorEntity
            {
                WorkspaceId = "ws-1",
                SessionId = "session-1",
                MessageId = "msg-dup",
                ChunkSeq = 1,
                Role = "assistant",
                SourceText = "第一块",
            });
            await db.SaveChangesAsync();

            db.SessionChunkVectors.Add(new SessionChunkVectorEntity
            {
                WorkspaceId = "ws-1",
                SessionId = "session-1",
                MessageId = "msg-dup",
                ChunkSeq = 1,
                Role = "assistant",
                SourceText = "重复块（应违反唯一约束）",
            });

            var threw = false;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "重复 (MessageId, ChunkSeq) 插入应违反唯一约束并抛出 DbUpdateException");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── d) 同源去重列：写入 + 读回往返 ────────────────────────────────

    [TestMethod]
    public async Task InsertAndQuery_SessionChunkVector_ShouldRoundTripDedupColumns()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            await using var db = factory.CreateDbContext();

            const int generation = 2;
            var hash = new string('b', 64);

            db.SessionChunkVectors.Add(new SessionChunkVectorEntity
            {
                WorkspaceId = "ws-2",
                SessionId = "session-dedup",
                MessageId = "msg-dedup",
                ChunkSeq = 0,
                Role = "user",
                SourceText = "同源去重锚点测试",
                CanonicalContentHash = hash,
                ContextGeneration = generation,
            });
            await db.SaveChangesAsync();

            var loaded = await db.SessionChunkVectors.SingleAsync(v => v.MessageId == "msg-dedup");
            Assert.AreEqual(hash, loaded.CanonicalContentHash);
            Assert.AreEqual(generation, loaded.ContextGeneration);
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── e) 存量库自愈：旧 schema 表缺两列 → 初始化 ALTER TABLE 补列、不删旧数据 ──

    [TestMethod]
    public async Task InitializeSchema_OnLegacyTable_ShouldAddDedupColumnsWithoutDroppingData()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            // 1) 手工构造“存量库”：仅含旧 schema 的 SessionChunkVectors 表 + 一行旧数据
            await using (var legacyConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await legacyConn.OpenAsync();
                await using var legacyCmd = legacyConn.CreateCommand();
                legacyCmd.CommandText = """
CREATE TABLE SessionChunkVectors (
    ChunkId     TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL,
    SessionId   TEXT NOT NULL,
    MessageId   TEXT NOT NULL,
    ChunkSeq    INTEGER NOT NULL,
    Role        TEXT NOT NULL,
    SourceText  TEXT NOT NULL,
    Embedding   BLOB,
    CreatedAt   INTEGER NOT NULL
);
INSERT INTO SessionChunkVectors (ChunkId, WorkspaceId, SessionId, MessageId, ChunkSeq, Role, SourceText, CreatedAt)
VALUES ('legacy-1', 'ws-legacy', 'session-legacy', 'msg-legacy', 0, 'user', '存量数据', 1);
""";
                await legacyCmd.ExecuteNonQueryAsync();
            }

            // 2) 走生产初始化路径 → 应自动补列且不抛异常
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            await MemoryLibraryDbInitializer.InitializeAsync(factory);

            // 3) 两列已补、旧数据仍在
            await using var db = factory.CreateDbContext();
            Assert.IsTrue(await ColumnExistsAsync(db, "CanonicalContentHash"), "存量库初始化后应补 CanonicalContentHash 列");
            Assert.IsTrue(await ColumnExistsAsync(db, "ContextGeneration"), "存量库初始化后应补 ContextGeneration 列");

            var legacy = await db.SessionChunkVectors.SingleOrDefaultAsync(v => v.ChunkId == "legacy-1");
            Assert.IsNotNull(legacy, "存量数据不应被删");
            Assert.AreEqual("session-legacy", legacy!.SessionId);
            Assert.AreEqual("存量数据", legacy.SourceText);
            Assert.IsNull(legacy.CanonicalContentHash);
            Assert.IsNull(legacy.ContextGeneration);
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<bool> DbTableExistsAsync(MemoryLibraryDbContext db, string tableName)
    {
        var found = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name = {0}", tableName)
            .ToListAsync();
        return found.Count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(MemoryLibraryDbContext db, string column)
    {
        var found = await db.Database
            .SqlQueryRaw<string>(
                "SELECT name FROM pragma_table_info('SessionChunkVectors') WHERE name = {0}",
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
