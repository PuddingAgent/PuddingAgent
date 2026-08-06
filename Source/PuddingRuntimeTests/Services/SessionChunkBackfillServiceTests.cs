using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// WP-L2d：SessionChunkVectors 存量回填 job。
/// fake ISessionChunkIndexer（记录调用）+ 临时文件 platform 库（EnsureCreated 建表）
/// + 临时文件 memory 库（MemoryLibraryDbInitializer 建表，与生产启动路径一致）。
/// </summary>
[TestClass]
public sealed class SessionChunkBackfillServiceTests
{
    // ── a) 角色过滤：仅 user/assistant 被索引 ──────────────────────────

    [TestMethod]
    public async Task RunAsync_MixedRoles_OnlyUserAndAssistantIndexed()
    {
        var platformPath = CreateTempDbPath("platform");
        var memoryPath = CreateTempDbPath("memory");
        try
        {
            var platformFactory = new TestPlatformDbFactory(CreatePlatformOptions(platformPath));
            await InitializePlatformDbAsync(platformFactory);
            var memoryFactory = new TestMemoryDbFactory(CreateMemoryOptions(memoryPath));
            await MemoryLibraryDbInitializer.InitializeAsync(memoryFactory);

            await SeedMessagesAsync(platformFactory,
                ("msg-user", "user"),
                ("msg-assistant", "assistant"),
                ("msg-tool", "tool"),
                ("msg-system", "system"));

            var indexer = new FakeIndexer();
            var service = CreateService(platformFactory, memoryFactory, indexer, enabled: true);

            await service.RunAsync();

            var ids = indexer.Calls.Select(c => c.MessageId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "msg-assistant", "msg-user" }, ids, "tool/system 应被跳过");
        }
        finally
        {
            CleanupDbFile(platformPath);
            CleanupDbFile(memoryPath);
        }
    }

    // ── b) 二次运行：已索引 MessageId 全部跳过 ────────────────────────

    [TestMethod]
    public async Task RunAsync_AlreadyIndexedMessageIds_SkipsOnSecondRun()
    {
        var platformPath = CreateTempDbPath("platform");
        var memoryPath = CreateTempDbPath("memory");
        try
        {
            var platformFactory = new TestPlatformDbFactory(CreatePlatformOptions(platformPath));
            await InitializePlatformDbAsync(platformFactory);
            var memoryFactory = new TestMemoryDbFactory(CreateMemoryOptions(memoryPath));
            await MemoryLibraryDbInitializer.InitializeAsync(memoryFactory);

            await SeedMessagesAsync(platformFactory,
                ("msg-1", "user"), ("msg-2", "assistant"), ("msg-3", "user"));

            var indexer = new FakeIndexer();
            var service = CreateService(platformFactory, memoryFactory, indexer, enabled: true);

            await service.RunAsync();
            Assert.AreEqual(3, indexer.Calls.Count, "首次运行应索引全部 user/assistant 消息");

            // 模拟"已索引"：为每个 MessageId 预置 SessionChunkVectors 行
            await using (var memoryDb = memoryFactory.CreateDbContext())
            {
                foreach (var msgId in new[] { "msg-1", "msg-2", "msg-3" })
                {
                    memoryDb.SessionChunkVectors.Add(new SessionChunkVectorEntity
                    {
                        WorkspaceId = "ws-1",
                        SessionId = "session-1",
                        MessageId = msgId,
                        ChunkSeq = 0,
                        Role = "user",
                        SourceText = "已索引占位块",
                    });
                }
                await memoryDb.SaveChangesAsync();
            }

            indexer.Calls.Clear();
            await service.RunAsync();
            Assert.AreEqual(0, indexer.Calls.Count, "二次运行应跳过全部已索引消息");
        }
        finally
        {
            CleanupDbFile(platformPath);
            CleanupDbFile(memoryPath);
        }
    }

    // ── c) 键集分页：超过 BatchSize 的消息全部被处理 ──────────────────

    [TestMethod]
    public async Task RunAsync_MoreMessagesThanBatchSize_AllIndexedViaKeysetPagination()
    {
        var platformPath = CreateTempDbPath("platform");
        var memoryPath = CreateTempDbPath("memory");
        try
        {
            var platformFactory = new TestPlatformDbFactory(CreatePlatformOptions(platformPath));
            await InitializePlatformDbAsync(platformFactory);
            var memoryFactory = new TestMemoryDbFactory(CreateMemoryOptions(memoryPath));
            await MemoryLibraryDbInitializer.InitializeAsync(memoryFactory);

            await SeedMessagesAsync(platformFactory,
                ("m-01", "user"), ("m-02", "assistant"), ("m-03", "user"),
                ("m-04", "assistant"), ("m-05", "user"));

            var indexer = new FakeIndexer();
            // BatchSize=2 → 5 条消息需要 3 批，验证键集分页跨批完整覆盖
            var service = CreateService(platformFactory, memoryFactory, indexer, enabled: true, batchSize: 2);

            await service.RunAsync();

            Assert.AreEqual(5, indexer.Calls.Count, "全部消息应被处理（无遗漏、无重复）");
            Assert.AreEqual(5, indexer.Calls.Select(c => c.MessageId).Distinct().Count(), "MessageId 不应重复");
        }
        finally
        {
            CleanupDbFile(platformPath);
            CleanupDbFile(memoryPath);
        }
    }

    // ── d) 禁用开关：零调用 ───────────────────────────────────────────

    [TestMethod]
    public async Task RunAsync_Disabled_NoIndexerCalls()
    {
        var platformPath = CreateTempDbPath("platform");
        var memoryPath = CreateTempDbPath("memory");
        try
        {
            var platformFactory = new TestPlatformDbFactory(CreatePlatformOptions(platformPath));
            await InitializePlatformDbAsync(platformFactory);
            var memoryFactory = new TestMemoryDbFactory(CreateMemoryOptions(memoryPath));
            await MemoryLibraryDbInitializer.InitializeAsync(memoryFactory);

            await SeedMessagesAsync(platformFactory, ("msg-1", "user"));

            var indexer = new FakeIndexer();
            var service = CreateService(platformFactory, memoryFactory, indexer, enabled: false);

            await service.RunAsync();

            Assert.AreEqual(0, indexer.Calls.Count, "Enabled=false 时不应有任何索引调用");
        }
        finally
        {
            CleanupDbFile(platformPath);
            CleanupDbFile(memoryPath);
        }
    }

    // ── e) Hosted service 启动：回填不能阻塞宿主 Ready ────────────────

    [TestMethod]
    public async Task StartAsync_BackfillStillRunning_ReturnsWithoutBlockingHostStartup()
    {
        var platformPath = CreateTempDbPath("platform");
        var memoryPath = CreateTempDbPath("memory");
        try
        {
            var platformFactory = new TestPlatformDbFactory(CreatePlatformOptions(platformPath));
            await InitializePlatformDbAsync(platformFactory);
            var memoryFactory = new TestMemoryDbFactory(CreateMemoryOptions(memoryPath));
            await MemoryLibraryDbInitializer.InitializeAsync(memoryFactory);
            await SeedMessagesAsync(platformFactory, ("msg-blocking", "user"));

            var indexer = new BlockingIndexer();
            var service = CreateService(platformFactory, memoryFactory, indexer, enabled: true);

            await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await indexer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsFalse(indexer.Completed, "回填仍在运行时，宿主 StartAsync 应已经返回");

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            CleanupDbFile(platformPath);
            CleanupDbFile(memoryPath);
        }
    }

    // ── Test Infrastructure ────────────────────────────────────────────

    private static SessionChunkBackfillService CreateService(
        TestPlatformDbFactory platformFactory,
        TestMemoryDbFactory memoryFactory,
        ISessionChunkIndexer indexer,
        bool enabled,
        int batchSize = 50)
        => new(
            platformFactory,
            memoryFactory,
            indexer,
            Options.Create(new SessionChunkBackfillOptions
            {
                Enabled = enabled,
                BatchSize = batchSize,
                DelayMs = 0,
            }),
            NullLogger<SessionChunkBackfillService>.Instance);

    private static DbContextOptions<PlatformDbContext> CreatePlatformOptions(string dbPath)
        => new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

    private static DbContextOptions<MemoryLibraryDbContext> CreateMemoryOptions(string dbPath)
        => new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

    private static string CreateTempDbPath(string tag)
        => Path.Combine(Path.GetTempPath(), $"pudding-backfill-{tag}-{Guid.NewGuid():N}.db");

    private static async Task InitializePlatformDbAsync(TestPlatformDbFactory factory)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    private static async Task SeedMessagesAsync(
        TestPlatformDbFactory factory,
        params (string MessageId, string Role)[] messages)
    {
        await using var db = factory.CreateDbContext();
        var seq = 0;
        foreach (var (messageId, role) in messages)
        {
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = messageId,
                SessionId = "session-1",
                WorkspaceId = "ws-1",
                Role = role,
                Content = $"这是用于回填测试的消息内容，足够长以便通过索引器的最小长度过滤：{messageId}",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + seq++,
            });
        }
        await db.SaveChangesAsync();
    }

    private sealed class TestPlatformDbFactory : IDbContextFactory<PlatformDbContext>
    {
        private readonly DbContextOptions<PlatformDbContext> _options;

        public TestPlatformDbFactory(DbContextOptions<PlatformDbContext> options)
        {
            _options = options;
        }

        public PlatformDbContext CreateDbContext() => new(_options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PlatformDbContext(_options));
    }

    private sealed class TestMemoryDbFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestMemoryDbFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }

    /// <summary>记录调用的 fake 索引器（不真正嵌入）。</summary>
    private sealed class FakeIndexer : ISessionChunkIndexer
    {
        public List<(string MessageId, string Role)> Calls { get; } = new();

        public Task IndexMessageAsync(
            string workspaceId, string sessionId, string messageId, string role, string? content,
            CancellationToken ct = default)
        {
            Calls.Add((messageId, role));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingIndexer : ISessionChunkIndexer
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Completed { get; private set; }

        public async Task IndexMessageAsync(
            string workspaceId, string sessionId, string messageId, string role, string? content,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                Completed = true;
            }
        }
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
