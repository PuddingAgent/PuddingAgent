using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// WP-L2c：SessionChunkVectors 读取侧——向量召回第 5 路（姊妹路 = 章节向量第 4 路）。
/// 覆盖：相似度降序 / workspace 隔离 / RRF 全链路集成（chunk: 前缀 SourceId）/ Embedding 为 null 不参与。
/// 文件库模式仿 SessionChunkVectorsTests.cs（生产路径 MemoryLibraryDbInitializer 建表）。
/// </summary>
[TestClass]
public sealed class SessionChunkVectorRecallTests
{
    // ── Test Infrastructure ────────────────────────────────────────────

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
        var dbPath = CreateTempDbPath("chunk-recall");
        var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
        await MemoryLibraryDbInitializer.InitializeAsync(factory);
        return dbPath;
    }

    /// <summary>MemoryRecallService 的 facts/prefs 路需要 MemoryDbContext（独立文件库，EnsureCreated 建表）。</summary>
    private static async Task<string> InitializeMemoryDbAsync()
    {
        var dbPath = CreateTempDbPath("chunk-recall-mem");
        var factory = new TestMemoryDbContextFactory(
            new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
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

    private sealed class TestMemoryDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        private readonly DbContextOptions<MemoryDbContext> _options;

        public TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options) => _options = options;

        public MemoryDbContext CreateDbContext() => new(_options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryDbContext(_options));
    }

    /// <summary>固定向量 embedding 假实现——记录调用次数，验证同一次召回只生成一次 query embedding。</summary>
    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        private readonly float[] _vector;

        public FakeEmbeddingService(float[] vector) => _vector = vector;

        public int GenerateCalls { get; private set; }

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            GenerateCalls++;
            return Task.FromResult((float[])_vector.Clone());
        }

        public Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult(texts.Select(_ => (float[])_vector.Clone()).ToArray());
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

    // ── a) 3 条已知向量 chunk（不同相似度），查询返回按相似度降序 ─────────

    [TestMethod]
    public async Task SearchChunksByVector_ShouldReturnDescendingSimilarity()
    {
        var dbPath = await InitializeLibraryDbAsync();
        try
        {
            var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
            await using (var db = factory.CreateDbContext())
            {
                // 查询向量 Q=[1,0,0,0]：相似度 A=1.0, B=0.8, C=0.0（互不相同）
                db.SessionChunkVectors.AddRange(
                    new SessionChunkVectorEntity
                    {
                        WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-a", ChunkSeq = 0,
                        Role = "user", SourceText = "chunk A：与查询向量完全一致", Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                    },
                    new SessionChunkVectorEntity
                    {
                        WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-b", ChunkSeq = 0,
                        Role = "user", SourceText = "chunk B：与查询向量夹角较小", Embedding = EmbeddingBytes(0.8f, 0.6f, 0f, 0f),
                    },
                    new SessionChunkVectorEntity
                    {
                        WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-c", ChunkSeq = 0,
                        Role = "user", SourceText = "chunk C：与查询向量正交", Embedding = EmbeddingBytes(0f, 1f, 0f, 0f),
                    });
                await db.SaveChangesAsync();
            }

            var library = new MemoryLibrary(factory);
            var results = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10);

            Assert.AreEqual(3, results.Count);
            Assert.IsTrue(
                results[0].Score > results[1].Score && results[1].Score > results[2].Score,
                "结果应按余弦相似度降序排列");
            Assert.AreEqual(1.0, results[0].Score, 1e-4);
            Assert.AreEqual("chunk A：与查询向量完全一致", results[0].SourceText);
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── b) workspace 隔离：另一 workspace 的 chunk 不返回 ────────────────

    [TestMethod]
    public async Task SearchChunksByVector_ShouldRespectWorkspaceIsolation()
    {
        var dbPath = await InitializeLibraryDbAsync();
        try
        {
            var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
            string otherChunkId;
            await using (var db = factory.CreateDbContext())
            {
                var ws1 = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-1", ChunkSeq = 0,
                    Role = "user", SourceText = "ws-1 的 chunk", Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                };
                var ws2 = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-2", SessionId = "sess-other", MessageId = "m-2", ChunkSeq = 0,
                    Role = "user", SourceText = "ws-2 的 chunk（向量与 ws-1 完全相同）", Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                };
                db.SessionChunkVectors.AddRange(ws1, ws2);
                otherChunkId = ws2.ChunkId;
                await db.SaveChangesAsync();
            }

            var library = new MemoryLibrary(factory);
            var results = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10);

            Assert.AreEqual(1, results.Count);
            Assert.AreNotEqual(otherChunkId, results[0].ChunkId, "另一 workspace 的 chunk 不应返回");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── c) RRF 集成：走 MemoryRecallService 全链路，结果含 chunk: 前缀 SourceId ──

    [TestMethod]
    public async Task Recall_WithChunkVectors_ShouldFuseChunkPrefixedSourceIds()
    {
        var libDbPath = await InitializeLibraryDbAsync();
        var memDbPath = await InitializeMemoryDbAsync();
        try
        {
            var libFactory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(libDbPath));
            string chunkAId;
            string chunkBId;
            await using (var db = libFactory.CreateDbContext())
            {
                var a = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-a", ChunkSeq = 0,
                    Role = "user", SourceText = "MySQL 连接池调优要点", Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                    // T2 写侧冗余列（模拟生产索引后写入）：原始消息 hash 有值、generation 为 null
                    CanonicalContentHash = "hash-a",
                    ContextGeneration = null,
                };
                var b = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-b", ChunkSeq = 0,
                    Role = "user", SourceText = "SQLite 事务写入性能", Embedding = EmbeddingBytes(0.8f, 0.6f, 0f, 0f),
                    CanonicalContentHash = "hash-b",
                    ContextGeneration = 1,
                };
                db.SessionChunkVectors.AddRange(a, b);
                chunkAId = a.ChunkId;
                chunkBId = b.ChunkId;
                await db.SaveChangesAsync();
            }

            var memFactory = new TestMemoryDbContextFactory(
                new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite($"Data Source={memDbPath}").Options);

            var fakeEmbedding = new FakeEmbeddingService([1f, 0f, 0f, 0f]);
            var service = new MemoryRecallService(
                library: null!, // MemoryRecallService 当前实现不引用 _library 字段（仅赋值），传 null 安全
                memoryLibrary: new MemoryLibrary(libFactory),
                dbFactory: memFactory,
                logger: NullLogger<MemoryRecallService>.Instance,
                embeddingService: fakeEmbedding);

            var result = await service.RecallAsync(
                query: "mysql connection pool tuning",
                workspaceId: "ws-1",
                topK: 10);

            Assert.AreEqual(1, fakeEmbedding.GenerateCalls, "同一次召回只允许调用一次 embedding API");
            Assert.AreEqual(2, result.HitStats.ChunkVectorHits, "第 5 路会话块命中数应为 2");

            var chunkSourceIds = result.Items
                .Where(i => i.SourceId?.StartsWith("chunk:") == true)
                .Select(i => i.SourceId!)
                .ToList();
            Assert.AreEqual(2, chunkSourceIds.Count, "融合结果应包含 2 条 chunk: 前缀的会话块记忆");
            CollectionAssert.Contains(chunkSourceIds, $"chunk:sess-1:{chunkAId}");
            CollectionAssert.Contains(chunkSourceIds, $"chunk:sess-1:{chunkBId}");
            Assert.IsTrue(
                result.Items.Any(i => i.Source == "chunk-vector"),
                "融合结果中应有 Source=chunk-vector 的条目");
            // P1-2 T4：chunk-vector 路召回项必须携带源消息溯源元数据（SourceMessageId/hash/generation 透传）
            var chunkAHit = result.Items.Single(i => i.SourceId == $"chunk:sess-1:{chunkAId}");
            Assert.AreEqual("m-a", chunkAHit.SourceMessageId, "chunk-vector 项应透传源消息 ID");
            Assert.AreEqual("hash-a", chunkAHit.CanonicalContentHash, "chunk-vector 项应透传写侧冗余 hash");
            Assert.IsNull(chunkAHit.ContextGeneration, "原始消息（generation 为 null）应透传 null");

            var chunkBHit = result.Items.Single(i => i.SourceId == $"chunk:sess-1:{chunkBId}");
            Assert.AreEqual("m-b", chunkBHit.SourceMessageId, "chunk-vector 项应透传源消息 ID");
            Assert.AreEqual("hash-b", chunkBHit.CanonicalContentHash, "chunk-vector 项应透传写侧冗余 hash");
            Assert.AreEqual(1, chunkBHit.ContextGeneration, "chunk-vector 项应透传压缩代际");

            // 非 chunk 路（library/fact/preference）不携带会话消息溯源字段（向后兼容：默认 null）
            Assert.IsTrue(
                result.Items.Where(i => i.Source != "chunk-vector")
                    .All(i => i.SourceMessageId is null && i.CanonicalContentHash is null && i.ContextGeneration is null),
                "非 chunk-vector 路召回项不应携带会话消息溯源字段");
        }
        finally
        {
            CleanupDbFile(libDbPath);
            CleanupDbFile(memDbPath);
        }
    }

    // ── d) Embedding 为 null 的 chunk 不参与向量检索 ────────────────────

    [TestMethod]
    public async Task SearchChunksByVector_ShouldSkipChunksWithoutEmbedding()
    {
        var dbPath = await InitializeLibraryDbAsync();
        try
        {
            var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
            string embeddedChunkId;
            await using (var db = factory.CreateDbContext())
            {
                var withEmbedding = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-1", ChunkSeq = 0,
                    Role = "user", SourceText = "有向量的 chunk", Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                };
                var noEmbedding = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-2", ChunkSeq = 0,
                    Role = "user", SourceText = "没有向量的 chunk（不应参与向量检索）", Embedding = null,
                };
                db.SessionChunkVectors.AddRange(withEmbedding, noEmbedding);
                embeddedChunkId = withEmbedding.ChunkId;
                await db.SaveChangesAsync();
            }

            var library = new MemoryLibrary(factory);
            var results = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10);

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(embeddedChunkId, results[0].ChunkId, "Embedding 为 null 的 chunk 不应参与向量检索");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── e) covered 过滤：CompactedBy != null 的 chunk 默认不返回；includeCovered=true 时返回且带 hash ──

    [TestMethod]
    public async Task SearchChunksByVector_ShouldFilterCoveredByDefaultAndReturnWithHashWhenIncluded()
    {
        var dbPath = await InitializeLibraryDbAsync();
        try
        {
            var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
            await CreateMessagesTableAsync(factory);

            string coveredChunkId;
            string uncoveredChunkId;
            await using (var db = factory.CreateDbContext())
            {
                var covered = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-covered", ChunkSeq = 0,
                    Role = "user", SourceText = "已被压缩覆盖的 chunk（默认不应召回）", Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                };
                var uncovered = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-open", ChunkSeq = 0,
                    Role = "user", SourceText = "未被覆盖的 chunk（应召回）", Embedding = EmbeddingBytes(0.9f, 0.1f, 0f, 0f),
                };
                db.SessionChunkVectors.AddRange(covered, uncovered);
                coveredChunkId = covered.ChunkId;
                uncoveredChunkId = uncovered.ChunkId;
                await db.SaveChangesAsync();

                // 同库 Messages 行：covered 的 CompactedBy 非空；uncovered 的为空
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Messages (MessageId, CanonicalContentHash, ContextGeneration, CompactedBy)
                    VALUES ($coveredId, 'hash-covered', 1, 'compaction-1'),
                           ($uncoveredId, 'hash-open', 0, NULL)
                    """;
                cmd.Parameters.Add(new SqliteParameter("$coveredId", "m-covered"));
                cmd.Parameters.Add(new SqliteParameter("$uncoveredId", "m-open"));
                await cmd.ExecuteNonQueryAsync();
            }

            var library = new MemoryLibrary(factory);

            // 默认：covered chunk 不返回
            var filtered = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10);
            Assert.AreEqual(1, filtered.Count, "默认应过滤 covered chunk");
            Assert.AreEqual(uncoveredChunkId, filtered[0].ChunkId);
            Assert.IsFalse(filtered[0].IsCovered);

            // includeCovered=true：covered chunk 返回且带 hash
            var all = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10, includeCovered: true);
            Assert.AreEqual(2, all.Count);
            var coveredHit = all.Single(x => x.ChunkId == coveredChunkId);
            Assert.IsTrue(coveredHit.IsCovered);
            Assert.AreEqual("hash-covered", coveredHit.CanonicalContentHash);
            Assert.AreEqual(1, coveredHit.ContextGeneration);
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ── f) hash 透传：返回项含 MessageId/CanonicalContentHash/ContextGeneration；Messages 行缺失时现算兜底 ──

    [TestMethod]
    public async Task SearchChunksByVector_ShouldExposeMessageHashAndComputeFallbackWhenMissing()
    {
        var dbPath = await InitializeLibraryDbAsync();
        try
        {
            var factory = new TestLibraryDbContextFactory(CreateLibraryFileOptions(dbPath));
            await CreateMessagesTableAsync(factory);

            string withMessageChunkId;
            string orphanChunkId;
            await using (var db = factory.CreateDbContext())
            {
                // chunk 写侧冗余列留空（模拟 T2 前存量行），联表取 Messages 值
                var withMessage = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-1", ChunkSeq = 0,
                    Role = "user", SourceText = "有 Messages 行的 chunk", Embedding = EmbeddingBytes(1f, 0f, 0f, 0f),
                };
                // Messages 无对应行（LEFT JOIN 落空）→ hash 现算兜底，chunk 不丢（风险4）
                var orphan = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-orphan", ChunkSeq = 0,
                    Role = "user", SourceText = "Messages 缺失的孤立 chunk", Embedding = EmbeddingBytes(0.95f, 0.05f, 0f, 0f),
                };
                db.SessionChunkVectors.AddRange(withMessage, orphan);
                withMessageChunkId = withMessage.ChunkId;
                orphanChunkId = orphan.ChunkId;
                await db.SaveChangesAsync();

                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Messages (MessageId, CanonicalContentHash, ContextGeneration, CompactedBy)
                    VALUES ($id, 'expected-hash', 2, NULL)
                    """;
                cmd.Parameters.Add(new SqliteParameter("$id", "m-1"));
                await cmd.ExecuteNonQueryAsync();
            }

            var library = new MemoryLibrary(factory);
            var results = await library.SearchSessionChunksByVectorAsync([1f, 0f, 0f, 0f], "ws-1", topK: 10);

            Assert.AreEqual(2, results.Count, "LEFT JOIN 落空的行不应丢 chunk（方案风险4）");

            var hit = results.Single(x => x.ChunkId == withMessageChunkId);
            Assert.AreEqual("m-1", hit.MessageId);
            Assert.AreEqual("expected-hash", hit.CanonicalContentHash);
            Assert.AreEqual(2, hit.ContextGeneration);
            Assert.IsFalse(hit.IsCovered);

            var orphanHit = results.Single(x => x.ChunkId == orphanChunkId);
            Assert.AreEqual("m-orphan", orphanHit.MessageId);
            // 现算兜底：SHA-256(UTF-8(SourceText)) 小写 hex（与写侧 T2 等价算法）
            var expectedFallback = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("Messages 缺失的孤立 chunk"))).ToLowerInvariant();
            Assert.AreEqual(expectedFallback, orphanHit.CanonicalContentHash);
            Assert.IsNull(orphanHit.ContextGeneration);
            Assert.IsFalse(orphanHit.IsCovered, "Messages 无行 → 视为未覆盖");
        }
        finally
        {
            CleanupDbFile(dbPath);
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
}
