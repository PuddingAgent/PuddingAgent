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
            Assert.AreEqual("chunk A：与查询向量完全一致", results[0].Snippet);
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
            Assert.AreNotEqual(otherChunkId, results[0].ChapterId, "另一 workspace 的 chunk 不应返回");
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
                };
                var b = new SessionChunkVectorEntity
                {
                    WorkspaceId = "ws-1", SessionId = "sess-1", MessageId = "m-b", ChunkSeq = 0,
                    Role = "user", SourceText = "SQLite 事务写入性能", Embedding = EmbeddingBytes(0.8f, 0.6f, 0f, 0f),
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
            Assert.AreEqual(embeddedChunkId, results[0].ChapterId, "Embedding 为 null 的 chunk 不应参与向量检索");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }
}
