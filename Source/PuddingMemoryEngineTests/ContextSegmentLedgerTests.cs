using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngineTests;

/// <summary>
/// P1-1 Task A：ContextSegmentLedger（设计方案 §6.1 数据合同）。
/// 验证 DbSet / EF 映射 / DDL 三处同步一致（memory 库 schema 由 init_memory.sql +
/// MemoryDbInitializer 幂等自愈管理，非 EnsureCreated），以及完整读写往返。
/// </summary>
[TestClass]
public sealed class ContextSegmentLedgerTests
{
    // ── Test Infrastructure ────────────────────────────────────────────

    private static DbContextOptions<MemoryDbContext> CreateFileOptions(string dbPath)
        => new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .EnableSensitiveDataLogging()
            .Options;

    private static string CreateTempDbPath()
        => Path.Combine(Path.GetTempPath(), $"pudding-context-segment-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 通过 MemoryDbInitializer 初始化（与生产启动路径一致）。
    /// 必须用文件库而非 InMemory：初始化器内部按连接字符串另开连接，InMemory 各连接互不可见。
    /// </summary>
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

    // ── a) 初始化器跑两遍幂等不抛异常，ContextSegments 表与索引存在 ──────

    [TestMethod]
    public async Task InitializeSchema_Twice_ShouldBeIdempotent()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));

            await MemoryDbInitializer.InitializeAsync(factory);
            await MemoryDbInitializer.InitializeAsync(factory);

            await using var db = factory.CreateDbContext();
            Assert.IsTrue(
                await DbObjectExistsAsync(db, "table", "ContextSegments"),
                "初始化后应存在 ContextSegments 表");
            Assert.IsTrue(
                await DbObjectExistsAsync(db, "index", "IX_ContextSegments_Session_SeqStart"),
                "初始化后应存在 SessionId+SequenceStart 索引");
            Assert.IsTrue(
                await DbObjectExistsAsync(db, "index", "IX_ContextSegments_SourceKind_SourceId"),
                "初始化后应存在 SourceKind+SourceId 索引");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── b) 完整字段写入 + 读回（覆盖 §6.1 全部字段）──────────────────────

    [TestMethod]
    public async Task InsertAndQuery_ContextSegment_ShouldRoundTripAllFields()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            await using var db = factory.CreateDbContext();

            var entity = new ContextSegmentEntity
            {
                SegmentId = "seg-001",
                SessionId = "session-abc",
                RunId = "run-001",
                TurnId = "turn-007",
                SourceKind = "message",
                SourceId = "msg-042",
                SequenceStart = 41,
                SequenceEnd = 42,
                Role = "assistant",
                ContentType = "text",
                CanonicalContentHash = new string('a', 64),
                RawUtf8Bytes = 4096,
                EstimatedTokens = 1024,
                ProviderTokens = 998,
                ArtifactRef = "artifacts/ws-1/msg-042.json",
                ContextGeneration = 3,
                CoveredByManifestId = "manifest-001",
                Tier = "T2",
                IsAtomicToolGroup = true,
                AuthorizationScope = "workspace:ws-1/session:session-abc",
                CreatedAt = 1_720_000_000_000,
                Metadata = """{"probe":"roundtrip"}""",
            };
            db.ContextSegments.Add(entity);
            await db.SaveChangesAsync();

            var loaded = await db.ContextSegments.SingleAsync(s => s.SegmentId == entity.SegmentId);
            Assert.AreEqual("session-abc", loaded.SessionId);
            Assert.AreEqual("run-001", loaded.RunId);
            Assert.AreEqual("turn-007", loaded.TurnId);
            Assert.AreEqual("message", loaded.SourceKind);
            Assert.AreEqual("msg-042", loaded.SourceId);
            Assert.AreEqual(41, loaded.SequenceStart);
            Assert.AreEqual(42, loaded.SequenceEnd);
            Assert.AreEqual("assistant", loaded.Role);
            Assert.AreEqual("text", loaded.ContentType);
            Assert.AreEqual(new string('a', 64), loaded.CanonicalContentHash);
            Assert.AreEqual(4096, loaded.RawUtf8Bytes);
            Assert.AreEqual(1024, loaded.EstimatedTokens);
            Assert.AreEqual(998, loaded.ProviderTokens);
            Assert.AreEqual("artifacts/ws-1/msg-042.json", loaded.ArtifactRef);
            Assert.AreEqual(3, loaded.ContextGeneration);
            Assert.AreEqual("manifest-001", loaded.CoveredByManifestId);
            Assert.AreEqual("T2", loaded.Tier);
            Assert.IsTrue(loaded.IsAtomicToolGroup);
            Assert.AreEqual("workspace:ws-1/session:session-abc", loaded.AuthorizationScope);
            Assert.AreEqual(1_720_000_000_000, loaded.CreatedAt);
            Assert.AreEqual("""{"probe":"roundtrip"}""", loaded.Metadata);
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── c) 可空字段缺省时持久化为 NULL，且默认值生效 ────────────────────

    [TestMethod]
    public async Task NullableFields_ShouldPersistAsNull_WithDefaultsApplied()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            await using var db = factory.CreateDbContext();

            db.ContextSegments.Add(new ContextSegmentEntity
            {
                SegmentId = "seg-nullable",
                SessionId = "session-abc",
                SourceKind = "tool_result",
                SourceId = "tool-call-1",
                SequenceStart = 1,
                SequenceEnd = 1,
                Role = "tool",
                ContentType = "tool_result",
                CanonicalContentHash = new string('b', 64),
                RawUtf8Bytes = 128,
            });
            await db.SaveChangesAsync();

            var loaded = await db.ContextSegments.SingleAsync(s => s.SegmentId == "seg-nullable");
            Assert.IsNull(loaded.RunId);
            Assert.IsNull(loaded.TurnId);
            Assert.IsNull(loaded.ArtifactRef);
            Assert.IsNull(loaded.CoveredByManifestId);
            Assert.IsNull(loaded.AuthorizationScope);
            Assert.IsNull(loaded.EstimatedTokens);
            Assert.IsNull(loaded.ProviderTokens);
            Assert.IsNull(loaded.ContextGeneration);
            Assert.IsNull(loaded.Metadata);
            Assert.AreEqual("T0", loaded.Tier, "Tier 默认值应为 T0");
            Assert.IsFalse(loaded.IsAtomicToolGroup, "IsAtomicToolGroup 默认应为 false");
            Assert.AreNotEqual(0, loaded.CreatedAt, "CreatedAt 默认应为当前 Unix 毫秒时间戳");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── d) 重复 SegmentId 违反主键（幂等回填依赖）──────────────────────

    [TestMethod]
    public async Task DuplicateSegmentId_ShouldViolatePrimaryKey()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));

            // 第一个 context：写入 SegmentId=seg-dup（模拟历史回填）。
            await using (var db1 = factory.CreateDbContext())
            {
                db1.ContextSegments.Add(CreateMinimalSegment("seg-dup", "session-1"));
                await db1.SaveChangesAsync();
            }

            // 第二个独立 context：写入相同 SegmentId → 数据库主键约束 → DbUpdateException。
            // （同一 context 内重复 Add 会被 EF IdentityMap 提前拦截，不是本用例目标。）
            await using (var db2 = factory.CreateDbContext())
            {
                db2.ContextSegments.Add(CreateMinimalSegment("seg-dup", "session-1"));
                var threw = false;
                try
                {
                    await db2.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    threw = true;
                }
                Assert.IsTrue(threw, "重复 SegmentId 插入应违反主键并抛出 DbUpdateException");
            }
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── e) 旧库（无 ContextSegments 表）初始化后自愈补建 ─────────────────

    [TestMethod]
    public async Task SelfHealing_LegacyDatabaseWithoutContextSegments_ShouldCreateTable()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            // 模拟旧版 init_memory.sql 建出的库：只有 Sessions 旧表，无 ContextSegments。
            await using (var legacyConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await legacyConn.OpenAsync();
                await using var legacyCmd = legacyConn.CreateCommand();
                legacyCmd.CommandText = """
                    CREATE TABLE Sessions (
                        SessionId       TEXT PRIMARY KEY,
                        ParentSessionId TEXT,
                        WorkspaceId     TEXT NOT NULL,
                        AgentId         TEXT NOT NULL,
                        Title           TEXT,
                        Mode            TEXT NOT NULL DEFAULT 'chat',
                        Status          TEXT NOT NULL DEFAULT 'active',
                        Tags            TEXT,
                        CreatedAt       INTEGER NOT NULL,
                        LastActivityAt  INTEGER NOT NULL,
                        MessageCount    INTEGER NOT NULL DEFAULT 0,
                        TokenTotal      INTEGER NOT NULL DEFAULT 0,
                        Metadata        TEXT
                    );
                    """;
                await legacyCmd.ExecuteNonQueryAsync();
            }

            // 新版本初始化器应自愈补建 ContextSegments（不删除旧表/旧数据）。
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            await MemoryDbInitializer.InitializeAsync(factory);

            await using var db = factory.CreateDbContext();
            Assert.IsTrue(
                await DbObjectExistsAsync(db, "table", "ContextSegments"),
                "旧库初始化后应自愈补建 ContextSegments 表");
            Assert.IsTrue(
                await DbObjectExistsAsync(db, "table", "Sessions"),
                "旧表 Sessions 应保留（additive，不删除旧数据）");

            // 自愈后可正常写入。
            db.ContextSegments.Add(CreateMinimalSegment("seg-legacy-1", "session-legacy"));
            await db.SaveChangesAsync();
            Assert.AreEqual(
                1,
                await db.ContextSegments.CountAsync(),
                "自愈后的表应可正常写入并计数");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static ContextSegmentEntity CreateMinimalSegment(string segmentId, string sessionId)
        => new()
        {
            SegmentId = segmentId,
            SessionId = sessionId,
            SourceKind = "message",
            SourceId = "src-" + segmentId,
            SequenceStart = 1,
            SequenceEnd = 1,
            Role = "user",
            ContentType = "text",
            CanonicalContentHash = new string('c', 64),
            RawUtf8Bytes = 64,
        };

    private static async Task<bool> DbObjectExistsAsync(
        MemoryDbContext db,
        string type,
        string name)
    {
        var found = await db.Database
            .SqlQueryRaw<string>(
                "SELECT name FROM sqlite_master WHERE type = {0} AND name = {1}",
                type,
                name)
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
