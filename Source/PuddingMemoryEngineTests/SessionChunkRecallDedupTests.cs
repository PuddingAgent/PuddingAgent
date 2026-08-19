using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// WP-L2c / P1-2 T7：Recall 代际与同源去重——端到端回归。
/// 链路：索引写侧冗余 hash（T2，SessionChunkVectors.CanonicalContentHash 非空）
///   → 压缩覆盖（P1-1/P1-2 T1，Messages.CompactedBy 非空）
///   → 召回（T3，SearchSessionChunksByVectorAsync 默认 covered 过滤）
///   → includeCovered=true 时返回且带 hash/MessageId 供上层同源去重（T4 契约透传）。
/// 与 SessionChunkVectorRecallTests 的区分：本文件聚焦「写侧已有 hash」的端到端闭环，
/// 并验证写侧冗余 hash 优先于联表值（T2 写侧 → T3 查询侧的一致性锚点）。
/// 文件库模式仿 SessionChunkVectorsTests.cs（生产路径 MemoryLibraryDbInitializer 建表）。
/// </summary>
[TestClass]
public sealed class SessionChunkRecallDedupTests
{
    // ── Test Infrastructure ────────────────────────────────────────────
    // 与 SessionChunkVectorRecallTests.cs 同风格的 helper（独立文件自建，不跨文件引用私有成员）

    private static DbContextOptions<MemoryLibraryDbContext> CreateLibraryFileOptions(string dbPath)
        => new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .EnableSensitiveDataLogging()
            .Options;

    private static string CreateTempDbPath(string prefix)
        => Path.Combine(Path.GetTempPath(), $"pudding-{prefix}-{Guid.NewGuid():N}.db");

    /// <summary>通过 MemoryLibraryDbInitializer 建表（与生产启动路径一致，含 SessionChunkVectors 与 FTS5 虚拟表）。</summary>
    private static async Task<string> InitializeLibraryDbAsync()
    {
        var dbPath = CreateTempDbPath("recall-dedup");
        var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
        await MemoryLibraryDbInitializer.InitializeAsync(factory);
        return dbPath;
    }

    private sealed class TestLibraryDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestLibraryDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options) => _options = options;

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }

    private static byte[] EmbeddingBytes(params float[] values) => VectorSimilarity.FloatsToBytes(values);

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

    /// <summary>在 library 库中手工建 Messages 表（模拟生产同库；MemoryLibraryDbInitializer 不建此表）。</summary>
    private static async Task CreateMessagesTableAsync(TestLibraryDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Messages (
                MessageId TEXT PRIMARY KEY,
                CanonicalContentHash TEXT NULL,
                ContextGeneration INTEGER NULL,
                CompactedBy TEXT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── a) 端到端主链路：索引写 hash → 压缩覆盖 → 默认召回被过滤；includeCovered=true 返回且带 hash/MessageId ──

    [TestMethod]
    public async Task RecallDedup_ShouldFilterCoveredByDefaultAndReturnWithHashWhenIncluded()
    {
        var dbPath = await InitializeLibraryDbAsync();
        try
        {
            var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
            await CreateMessagesTableAsync(factory);

            const string coveredMessageId = "m-covered";
            const string writeSideHash = "write-side-hash-covered"; // T2 写侧冗余列（索引时回填）
            string coveredChunkId;
            await using (var db = factory.CreateDbContext())
            {
                // 写侧（T2）：索引 chunk 时已回填 CanonicalContentHash（模拟生产 SessionChunkIndexer 行为）
                var covered = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = coveredMessageId, ChunkSeq = 0,
                    Role = "user", SourceText = "旧消息的 chunk：已被压缩覆盖，默认不应再召回注入",
                    Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                    CanonicalContentHash = writeSideHash,
                    ContextGeneration = null, // 原始消息代际为 null
                };
                db.SessionChunkVectors.Add(covered);
                coveredChunkId = covered.ChunkId;
                await db.SaveChangesAsync();

                // 压缩侧（P1-1）：Messages 行标 CompactedBy 非空；联表 hash 故意与写侧不同，验证写侧优先
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Messages (MessageId, CanonicalContentHash, ContextGeneration, CompactedBy)
                    VALUES ($messageId, $messageHash, 2, 'compaction-run-7')
                    """;
                cmd.Parameters.Add(new SqliteParameter("$messageId", coveredMessageId));
                cmd.Parameters.Add(new SqliteParameter("$messageHash", "message-table-hash-different"));
                await cmd.ExecuteNonQueryAsync();
            }

            var library = new MemoryLibrary(factory);

            // 默认（includeCovered=false）：covered chunk 不进入召回
            var filtered = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10);
            Assert.AreEqual(0, filtered.Count, "被压缩覆盖的旧消息 chunk 默认不应被召回");

            // includeCovered=true：返回且带 hash/MessageId（hash 优先写侧冗余列，T2 值）
            var all = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10, includeCovered: true);
            Assert.AreEqual(1, all.Count);
            var hit = all[0];
            Assert.AreEqual(coveredChunkId, hit.ChunkId);
            Assert.AreEqual(coveredMessageId, hit.MessageId, "召回结果应携带源消息 ID 供上层溯源/去重");
            Assert.AreEqual(writeSideHash, hit.CanonicalContentHash, "hash 应优先取写侧冗余列（T2 索引时回填值）");
            Assert.IsTrue(hit.IsCovered, "includeCovered=true 时应标记 IsCovered");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── b) 对照：未覆盖消息的召回片段正常返回（同源去重只拦 covered，不误伤开放消息）──

    [TestMethod]
    public async Task RecallDedup_ShouldReturnUncoveredChunkNormally()
    {
        var dbPath = await InitializeLibraryDbAsync();
        try
        {
            var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
            await CreateMessagesTableAsync(factory);

            const string uncoveredMessageId = "m-open";
            const string writeSideHash = "write-side-hash-open";
            string uncoveredChunkId;
            await using (var db = factory.CreateDbContext())
            {
                var uncovered = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = uncoveredMessageId, ChunkSeq = 0,
                    Role = "user", SourceText = "未覆盖消息的 chunk：应正常召回",
                    Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                    CanonicalContentHash = writeSideHash,
                    ContextGeneration = null,
                };
                db.SessionChunkVectors.Add(uncovered);
                uncoveredChunkId = uncovered.ChunkId;
                await db.SaveChangesAsync();

                // Messages 行存在但 CompactedBy 为 null（未压缩 → 不 covered）
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Messages (MessageId, CanonicalContentHash, ContextGeneration, CompactedBy)
                    VALUES ($messageId, $messageHash, 0, NULL)
                    """;
                cmd.Parameters.Add(new SqliteParameter("$messageId", uncoveredMessageId));
                cmd.Parameters.Add(new SqliteParameter("$messageHash", writeSideHash));
                await cmd.ExecuteNonQueryAsync();
            }

            var library = new MemoryLibrary(factory);
            var results = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10);

            Assert.AreEqual(1, results.Count, "未覆盖消息的召回片段应正常返回");
            var hit = results[0];
            Assert.AreEqual(uncoveredChunkId, hit.ChunkId);
            Assert.AreEqual(uncoveredMessageId, hit.MessageId);
            Assert.AreEqual(writeSideHash, hit.CanonicalContentHash);
            Assert.IsFalse(hit.IsCovered, "未覆盖 chunk 不应标记 IsCovered");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }
}
