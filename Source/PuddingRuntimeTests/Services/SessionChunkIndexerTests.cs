using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingMemoryEngine.Infrastructure.Text;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// WP-L2b：SessionChunkVectors 写入侧——TextChunker 切块器 + SessionChunkIndexer 索引服务。
/// Indexer 测试用 fake IEmbeddingService（固定 1024 维）+ 临时文件库（MemoryLibraryDbInitializer 建表）。
/// </summary>
[TestClass]
public sealed class SessionChunkIndexerTests
{
    // ═══════════════════ TextChunker ═══════════════════

    [TestMethod]
    public void Chunk_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, TextChunker.Chunk(null).Count);
        Assert.AreEqual(0, TextChunker.Chunk("").Count);
        Assert.AreEqual(0, TextChunker.Chunk("   \n\t  ").Count);
    }

    [TestMethod]
    public void Chunk_ShortText_ReturnsSingleChunk()
    {
        var text = "你好，世界。这是一条短消息。";
        var chunks = TextChunker.Chunk(text, maxChars: 1024);
        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(text, chunks[0]);

        // 恰好等于 maxChars 也应返回单块
        var exact = new string('甲', 64);
        var exactChunks = TextChunker.Chunk(exact, maxChars: 64);
        Assert.AreEqual(1, exactChunks.Count);
        Assert.AreEqual(exact, exactChunks[0]);
    }

    [TestMethod]
    public void Chunk_LongText_SplitsBySentenceBoundaries_EachChunkWithinMaxChars()
    {
        // 多句长文本（含全角标点与换行），maxChars 卡在句间
        var sentence1 = new string('一', 60);
        var sentence2 = new string('二', 60);
        var sentence3 = new string('三', 60);
        var sentence4 = new string('四', 60);
        var text = sentence1 + "。\n" + sentence2 + "！" + sentence3 + "？" + sentence4 + "。";

        var chunks = TextChunker.Chunk(text, maxChars: 100, overlapChars: 20);
        Assert.IsTrue(chunks.Count >= 2, "长文本应被切成多块");
        foreach (var c in chunks)
        {
            Assert.IsFalse(string.IsNullOrEmpty(c));
            Assert.IsTrue(c.Length <= 100, $"块长度 {c.Length} 超过 maxChars");
        }
        // 句子不应被拦腰截断：块尾应是句末标点
        foreach (var c in chunks)
        {
            Assert.IsTrue(
                c.EndsWith('。') || c.EndsWith('！') || c.EndsWith('？') || c.EndsWith('!') || c.EndsWith('?') || c.EndsWith('\n'),
                $"块 [{c[^1]}] 未以句末标点结尾");
        }
    }

    [TestMethod]
    public void Chunk_LongText_AdjacentChunksOverlap_KeepingSentencesComplete()
    {
        // 每句 25 字 + 句号 = 26 字符；maxChars=80 → 每块 3 句；overlapChars=20 → 重叠最后一句
        var s1 = new string('甲', 25);
        var s2 = new string('乙', 25);
        var s3 = new string('丙', 25);
        var s4 = new string('丁', 25);
        var s5 = new string('戊', 25);
        var s6 = new string('己', 25);
        var text = s1 + "。" + s2 + "。" + s3 + "。" + s4 + "。" + s5 + "。" + s6 + "。";

        var chunks = TextChunker.Chunk(text, maxChars: 80, overlapChars: 20);
        Assert.IsTrue(chunks.Count >= 2, "长文本应被切成多块");

        // 块 1 以块 0 的尾句（丙句）开头 → 相邻块存在重叠且句子完整
        Assert.IsTrue(chunks[0].EndsWith(s3 + "。"), "块 0 应以丙句结尾");
        Assert.IsTrue(chunks[1].StartsWith(s3 + "。"), "块 1 应以丙句开头（重叠）");
        // 块 2 以块 1 的尾句（戊句）开头
        Assert.IsTrue(chunks[1].EndsWith(s5 + "。"), "块 1 应以戊句结尾");
        Assert.IsTrue(chunks[2].StartsWith(s5 + "。"), "块 2 应以戊句开头（重叠）");
    }

    [TestMethod]
    public void Chunk_SingleLongSentence_HardCutsWithinMaxChars()
    {
        // 无句末标点的超长文本 → 单句硬切，每块 ≤ maxChars，拼接还原原文
        var text = new string('甲', 2500);
        var chunks = TextChunker.Chunk(text, maxChars: 1000, overlapChars: 128);
        Assert.IsTrue(chunks.Count >= 3);
        foreach (var c in chunks)
            Assert.IsTrue(c.Length <= 1000);
        Assert.AreEqual(text, string.Concat(chunks), "硬切块应能无损拼接回原文");
    }

    [TestMethod]
    public void Chunk_IsDeterministic()
    {
        var text = "第一句比较长，讲的是天气。第二句讲的是心情！第三句换行了\n第四句很短。";
        var a = TextChunker.Chunk(text, maxChars: 20, overlapChars: 8);
        var b = TextChunker.Chunk(text, maxChars: 20, overlapChars: 8);
        CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
    }

    // ═══════════════════ SessionChunkIndexer ═══════════════════

    [TestMethod]
    public async Task IndexMessageAsync_UserMessage_WritesChunksWithIncrementalSeqAndCorrectBytes()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            var embeddingService = new FakeEmbeddingService();
            var indexer = new SessionChunkIndexer(
                embeddingService, factory, NullLogger<SessionChunkIndexer>.Instance);

            var content = BuildLongContent(30); // 30 句 × 121 字符 ≈ 3630 字符 → 多块
            await indexer.IndexMessageAsync(
                workspaceId: "ws-1", sessionId: "session-1", messageId: "msg-user-1",
                role: "user", content: content);

            var expectedChunks = TextChunker.Chunk(content);
            Assert.IsTrue(expectedChunks.Count > 1, "长内容应切为多块");

            await using var db = factory.CreateDbContext();
            var rows = await db.SessionChunkVectors
                .Where(v => v.MessageId == "msg-user-1")
                .OrderBy(v => v.ChunkSeq)
                .ToListAsync();

            Assert.AreEqual(expectedChunks.Count, rows.Count, "写入块数应与切块结果一致");
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.AreEqual(i, rows[i].ChunkSeq, $"ChunkSeq 应从 0 递增，第 {i} 块不符");
                Assert.AreEqual("ws-1", rows[i].WorkspaceId);
                Assert.AreEqual("session-1", rows[i].SessionId);
                Assert.AreEqual("user", rows[i].Role);
                Assert.AreEqual(expectedChunks[i], rows[i].SourceText);

                // 字节正确：1024 维 × 4B = 4096B，且与 fake 服务向量一致
                Assert.IsNotNull(rows[i].Embedding);
                Assert.AreEqual(1024 * 4, rows[i].Embedding!.Length);
                var expectedVector = await embeddingService.GenerateEmbeddingAsync(expectedChunks[i]);
                var expectedBytes = VectorSimilarity.FloatsToBytes(expectedVector);
                CollectionAssert.AreEqual(expectedBytes, rows[i].Embedding, $"第 {i} 块向量字节不符");
            }
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    [TestMethod]
    public async Task IndexMessageAsync_AssistantRole_Indexes_ToolAndSystemSkipped()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            var indexer = new SessionChunkIndexer(
                new FakeEmbeddingService(), factory, NullLogger<SessionChunkIndexer>.Instance);

            var content = BuildLongContent(5);
            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-assistant", "assistant", content);
            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-tool", "tool", content);
            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-system", "system", content);

            await using var db = factory.CreateDbContext();
            Assert.IsTrue(await db.SessionChunkVectors.AnyAsync(v => v.MessageId == "msg-assistant"), "assistant 消息应被索引");
            Assert.IsFalse(await db.SessionChunkVectors.AnyAsync(v => v.MessageId == "msg-tool"), "tool 角色应跳过");
            Assert.IsFalse(await db.SessionChunkVectors.AnyAsync(v => v.MessageId == "msg-system"), "system 角色应跳过");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    [TestMethod]
    public async Task IndexMessageAsync_ShortOrBlankContent_Skips()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            var indexer = new SessionChunkIndexer(
                new FakeEmbeddingService(), factory, NullLogger<SessionChunkIndexer>.Instance);

            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-short", "user", "太短了");
            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-blank", "user", "   ");
            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-null", "user", null);

            await using var db = factory.CreateDbContext();
            Assert.AreEqual(0, await db.SessionChunkVectors.CountAsync(), "短内容/空白/null 不应产生任何块");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    [TestMethod]
    public async Task IndexMessageAsync_SameMessageTwice_DoesNotThrow_AndKeepsRows()
    {
        var dbPath = await InitializeFileDbAsync();
        try
        {
            var factory = new TestDbContextFactory(CreateFileOptions(dbPath));
            var indexer = new SessionChunkIndexer(
                new FakeEmbeddingService(), factory, NullLogger<SessionChunkIndexer>.Instance);

            var content = BuildLongContent(30);
            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-dup", "user", content);
            await indexer.IndexMessageAsync("ws-1", "session-1", "msg-dup", "user", content);

            await using var db = factory.CreateDbContext();
            var rows = await db.SessionChunkVectors
                .Where(v => v.MessageId == "msg-dup")
                .OrderBy(v => v.ChunkSeq)
                .ToListAsync();
            var expectedChunks = TextChunker.Chunk(content);
            Assert.AreEqual(expectedChunks.Count, rows.Count, "二次索引不应重复写入（幂等）");
            CollectionAssert.AreEqual(
                Enumerable.Range(0, rows.Count).ToArray(),
                rows.Select(r => r.ChunkSeq).ToArray(),
                "ChunkSeq 应保持 0..n-1");
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    [TestMethod]
    public async Task IndexMessageAsync_MessageExistsInMessagesTable_CopiesHashAndGeneration()
    {
        var (dbPath, libraryFactory, memoryFactory) = await InitializeDualDbAsync();
        try
        {
            var content = BuildLongContent(30);
            const string seededHash = "a1b2c3d4e5f60718293a4b5c6d7e8f901a2b3c4d5e6f708192a3b4c5d6e7f8091";
            const int seededGeneration = 3;

            // 预置 Sessions + Messages 行（含 CanonicalContentHash/ContextGeneration），
            // 模拟"消息先落库、后索引"的生产时序（Messages 表 SessionId 有外键，需先建 Session）。
            await using (var seedDb = memoryFactory.CreateDbContext())
            {
                seedDb.Sessions.Add(new SessionEntity { SessionId = "session-1", WorkspaceId = "ws-1" });
                seedDb.Messages.Add(new MessageEntity
                {
                    MessageId = "msg-hash-1",
                    SessionId = "session-1",
                    Role = "user",
                    Content = content,
                    CanonicalContentHash = seededHash,
                    ContextGeneration = seededGeneration,
                });
                await seedDb.SaveChangesAsync();
            }

            var indexer = new SessionChunkIndexer(
                new FakeEmbeddingService(), libraryFactory, NullLogger<SessionChunkIndexer>.Instance,
                memoryFactory);

            await indexer.IndexMessageAsync(
                workspaceId: "ws-1", sessionId: "session-1", messageId: "msg-hash-1",
                role: "user", content: content);

            await using var verifyDb = libraryFactory.CreateDbContext();
            var rows = await verifyDb.SessionChunkVectors
                .Where(v => v.MessageId == "msg-hash-1")
                .OrderBy(v => v.ChunkSeq)
                .ToListAsync();

            Assert.IsTrue(rows.Count > 0, "长内容应切为多块并写入");
            foreach (var row in rows)
            {
                Assert.AreEqual(seededHash, row.CanonicalContentHash,
                    "Messages 表存在 hash 时，chunk 应复用表值而非现算");
                Assert.AreEqual(seededGeneration, row.ContextGeneration,
                    "Messages 表存在 generation 时，chunk 应复用表值");
            }
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    [TestMethod]
    public async Task IndexMessageAsync_MessageMissingFromMessagesTable_FallsBackToComputedHash()
    {
        var (dbPath, libraryFactory, memoryFactory) = await InitializeDualDbAsync();
        try
        {
            var indexer = new SessionChunkIndexer(
                new FakeEmbeddingService(), libraryFactory, NullLogger<SessionChunkIndexer>.Instance,
                memoryFactory);

            var content = BuildLongContent(30);
            await indexer.IndexMessageAsync(
                workspaceId: "ws-1", sessionId: "session-1", messageId: "msg-nohash-1",
                role: "user", content: content);

            var expectedHash = CompositionSnapshot.Sha256Hex(content);

            await using var verifyDb = libraryFactory.CreateDbContext();
            var rows = await verifyDb.SessionChunkVectors
                .Where(v => v.MessageId == "msg-nohash-1")
                .OrderBy(v => v.ChunkSeq)
                .ToListAsync();

            Assert.IsTrue(rows.Count > 0, "长内容应切为多块并写入");
            foreach (var row in rows)
            {
                Assert.AreEqual(expectedHash, row.CanonicalContentHash,
                    "Messages 表查不到该消息时，hash 应现算兜底保证非空");
                Assert.IsNull(row.ContextGeneration,
                    "Messages 表查不到该消息时，generation 应为 null");
            }
        }
        finally
        {
            CleanupDbFile(dbPath);
        }
    }

    // ═══════════════════ Test Infrastructure ═══════════════════

    private static DbContextOptions<MemoryLibraryDbContext> CreateFileOptions(string dbPath)
        => new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .EnableSensitiveDataLogging()
            .Options;

    private static DbContextOptions<MemoryDbContext> CreateMemoryFileOptions(string dbPath)
        => new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .EnableSensitiveDataLogging()
            .Options;

    private static string CreateTempDbPath()
        => Path.Combine(Path.GetTempPath(), $"pudding-session-chunk-index-{Guid.NewGuid():N}.db");

    /// <summary>通过 MemoryLibraryDbInitializer 建表（与生产启动路径一致），必须用文件库。</summary>
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

    /// <summary>
    /// P1-2 T2 专用：同时初始化 MemoryLibraryDbContext 与 MemoryDbContext（同库同路径），
    /// 前者建 SessionChunkVectors 表、后者建 Messages 表——两个初始化器需先后执行且幂等。
    /// </summary>
    private static async Task<(string DbPath, TestDbContextFactory LibraryFactory, TestMemoryDbContextFactory MemoryFactory)>
        InitializeDualDbAsync()
    {
        var dbPath = CreateTempDbPath();
        var libraryFactory = new TestDbContextFactory(CreateFileOptions(dbPath));
        var memoryFactory = new TestMemoryDbContextFactory(CreateMemoryFileOptions(dbPath));
        await MemoryLibraryDbInitializer.InitializeAsync(libraryFactory);
        await MemoryDbInitializer.InitializeAsync(memoryFactory);
        return (dbPath, libraryFactory, memoryFactory);
    }

    private sealed class TestMemoryDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        private readonly DbContextOptions<MemoryDbContext> _options;

        public TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        {
            _options = options;
        }

        public MemoryDbContext CreateDbContext() => new(_options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryDbContext(_options));
    }

    /// <summary>固定 1024 维的确定性 fake embedding（按文本内容哈希生成，保证"字节正确"可验证）。</summary>
    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult(CreateVector(text));

        public Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult(texts.Select(CreateVector).ToArray());

        private static float[] CreateVector(string text)
        {
            var v = new float[1024];
            var hash = 17;
            foreach (var c in text)
                hash = (hash * 31 + c) % 1_000_003;
            v[0] = hash / 1_000_003f;
            v[1] = text.Length / 10_000f;
            v[^1] = 0.125f;
            return v;
        }
    }

    /// <summary>构造 n 句长内容（每句 120 字 + 句号 ≈ 121 字符），保证默认 maxChars=1024 下切为多块。</summary>
    private static string BuildLongContent(int sentenceCount)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < sentenceCount; i++)
        {
            sb.Append(new string((char)('a' + i % 26), 120));
            sb.Append('。');
        }
        return sb.ToString();
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
