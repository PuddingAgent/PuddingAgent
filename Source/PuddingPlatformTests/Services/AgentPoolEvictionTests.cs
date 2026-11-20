using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 淘汰（LRU eviction）的持久化语义验收：库先死、内存跟随。
/// <para>
/// 这组用例锁的是一个具体的硬伤：<c>EvictLeastRecentlyUsedAsync</c> 曾经只改内存，
/// 库里那一行仍是 <c>sleeping</c> 且未软删，于是进程重启后 <c>EnsureReadyAsync</c>
/// 从库重建视图时把「已经淘汰掉的」条目又读了回来。第 4 个用例就是这条回归。
/// </para>
/// <para>
/// 每个用例独立建一个临时文件库（与 <c>AgentPoolEntryStoreTests</c> 同一条建表路径）：
/// 共享库会让「谁是最旧」「还剩几条」这类全局断言互相污染。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolEvictionTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════
    // 1. 有 store：淘汰必须写库（dead + deleted_at）并踢出内存
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Evict_WithStore_MarksRowDeadAndRemovesFromMemory()
    {
        await using var db = await EvictionDb.CreateAsync();
        await InsertSleptAsync(db, "pool-1", BaseTime.AddMinutes(1));
        await InsertSleptAsync(db, "pool-2", BaseTime.AddMinutes(5));

        var pool = CreatePool(db);
        await pool.GetSnapshotAsync();
        Assert.AreEqual(2, pool.Count, "前置条件：两条都应从库里恢复进内存");

        var evicted = await pool.EvictLeastRecentlyUsedAsync();

        Console.WriteLine($"[evidence] evicted={evicted}");

        Assert.AreEqual("pool-1", evicted, "必须淘汰 last_used_at 更早的那条");

        // 库：被淘汰的行必须是 dead + deleted_at 非空。只改内存时这两条都会是 sleeping / null，
        // 也就是「重启后复活」的直接来源。
        var row = await db.ReadRawAsync("pool-1-sub");
        Console.WriteLine($"[evidence] persisted status={row.Status}, deletedAt={row.DeletedAt ?? "<null>"}");
        Assert.AreEqual(AgentPoolEntity.StatusDead, row.Status, "淘汰必须把库里的状态写成 dead");
        Assert.IsNotNull(row.DeletedAt, "置 dead 必须同时写 deleted_at（软删是淘汰的唯一真相）");
        Assert.IsNull(await db.Store.GetByIdAsync("pool-1", default), "软删后条目按 id 不可见");

        // 内存：只剩较新的那条。
        Assert.AreEqual(1, pool.Count, "淘汰后内存必须只剩一条");
        var remaining = await pool.GetSnapshotAsync();
        Assert.HasCount(1, remaining);
        Assert.AreEqual("pool-2", remaining[0].Name, "剩下的必须是较新的那条");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 三条 slept：选中的必须是最旧的一条
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Evict_WithStore_LeavesNewestAlive()
    {
        await using var db = await EvictionDb.CreateAsync();
        await InsertSleptAsync(db, "pool-1", BaseTime.AddMinutes(1));
        await InsertSleptAsync(db, "pool-2", BaseTime.AddMinutes(2));
        await InsertSleptAsync(db, "pool-3", BaseTime.AddMinutes(3));

        var pool = CreatePool(db);
        await pool.GetSnapshotAsync();
        Assert.AreEqual(3, pool.Count, "前置条件：三条都应从库里恢复进内存");

        var evicted = await pool.EvictLeastRecentlyUsedAsync();

        var remainingInMemory = (await pool.GetSnapshotAsync())
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var remainingInDb = (await db.Store.LoadAllAsync(default))
            .Select(x => x.PoolId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Console.WriteLine(
            $"[evidence] evicted={evicted}, memory=[{string.Join(",", remainingInMemory)}], " +
            $"db=[{string.Join(",", remainingInDb)}]");

        Assert.AreEqual("pool-1", evicted, "必须淘汰 last_used_at 最早的那条（pool-1）");
        CollectionAssert.AreEqual(
            new[] { "pool-2", "pool-3" }, remainingInMemory,
            "内存里必须只剩较新的两条");
        CollectionAssert.AreEqual(
            new[] { "pool-2", "pool-3" }, remainingInDb,
            "库与内存的剩余集合必须一致（否则两者已经分叉）");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 没有可淘汰条目：返回 null，且库与内存都不动
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Evict_WithStore_NothingToEvict_ReturnsFalseWithoutTouchingMemory()
    {
        await using var db = await EvictionDb.CreateAsync();
        // 两条都是 idle（从未被置为 sleeping）：LRU 淘汰只对 sleeping 生效。
        await InsertAsync(db, "pool-1", BaseTime.AddMinutes(1));
        await InsertAsync(db, "pool-2", BaseTime.AddMinutes(2));

        var pool = CreatePool(db);
        await pool.GetSnapshotAsync();
        Assert.AreEqual(2, pool.Count);

        var evicted = await pool.EvictLeastRecentlyUsedAsync();

        Console.WriteLine($"[evidence] evicted={evicted ?? "<null>"}");

        Assert.IsNull(evicted, "没有 sleeping 条目时淘汰必须返回 null");
        Assert.AreEqual(2, pool.Count, "没东西可淘汰时不得动内存");

        var rows = await db.Store.LoadAllAsync(default);
        Assert.HasCount(2, rows, "没东西可淘汰时库也不得被改");
        foreach (var row in rows)
        {
            Assert.AreEqual(AgentPoolEntity.StatusIdle, row.Status, $"{row.PoolId} 不应被淘汰");
            var raw = await db.ReadRawAsync(row.SubSessionId);
            Assert.IsNull(raw.DeletedAt, $"{row.PoolId} 不得被写 deleted_at");
        }
    }

    // ═══════════════════════════════════════════════════════
    // 4. 核心回归：淘汰后重启不得复活
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Evict_RestartAfterEvict_DoesNotResurrectEntry()
    {
        await using var db = await EvictionDb.CreateAsync();
        await InsertSleptAsync(db, "pool-1", BaseTime.AddMinutes(1));
        await InsertSleptAsync(db, "pool-2", BaseTime.AddMinutes(5));

        var first = CreatePool(db);
        await first.GetSnapshotAsync();
        var evicted = await first.EvictLeastRecentlyUsedAsync();
        Assert.AreEqual("pool-1", evicted, "前置条件：旧的那条必须被淘汰");

        // 同一份库、新池实例 = 一次进程重启：内存视图只能从库重建。
        // 修复前这里会把 pool-1 读回来（库里它仍是 sleeping 且未软删）。
        var restarted = CreatePool(db);
        var loaded = await restarted.GetSnapshotAsync();

        Console.WriteLine(
            $"[evidence] after restart count={loaded.Count}, names=[{string.Join(",", loaded.Select(x => x.Name))}]");

        Assert.HasCount(1, loaded, "重启后只应恢复未被淘汰的那条");
        Assert.AreEqual("pool-2", loaded[0].Name, "被淘汰的 pool-1 不得复活");
        Assert.IsFalse(loaded.Any(x => x.Name == "pool-1"), "被淘汰的 pool-1 不得以任何形式回到内存视图");
    }

    // ── 测试脚手架 ──────────────────────────────────────────

    private static SubAgentPool CreatePool(EvictionDb db)
        => new(
            new TestSubAgentManager(),
            new ConfigurationBuilder().Build(),
            NullLogger<SubAgentPool>.Instance,
            db.Store);

    private static Task<int> InsertAsync(EvictionDb db, string poolId, DateTimeOffset createdAt)
        => db.Store.TryInsertAsync(
            new AgentPoolInsertRequest(
                PoolId: poolId,
                Name: poolId,
                SubSessionId: $"{poolId}-sub",
                TemplateId: "workspace-task-agent",
                Role: "worker",
                OwnerWorkspaceId: "default",
                OwnerSessionId: "session-a",
                OwnerAgentId: "agent-1",
                CreatedAt: createdAt),
            capacity: 0,
            default);

    /// <summary>
    /// 插一条并置成 sleeping。<c>last_used_at</c> 由插入时的 <paramref name="createdAt"/> 决定：
    /// store 的状态迁移刻意不推进它（迁移不是「使用」），所以这里的插入时间就是 LRU 排序键。
    /// </summary>
    private static async Task InsertSleptAsync(EvictionDb db, string poolId, DateTimeOffset createdAt)
    {
        Assert.AreEqual(1, await InsertAsync(db, poolId, createdAt), $"前置条件：{poolId} 必须能插入");
        var slept = await db.Store.TrySetStatusAsync(
            poolId,
            AgentPoolEntity.StatusIdle,
            AgentPoolEntity.StatusSleeping,
            statusReason: null,
            clearExecution: false,
            now: createdAt.AddMinutes(1),
            default);
        Assert.AreEqual(1, slept, $"前置条件：{poolId} 必须能置为 sleeping");
    }

    /// <summary>
    /// 单个用例的独立环境：临时目录里的真实 SQLite 文件库 + 真实 store。
    /// 不用内存库：池写库与用例直读是两个连接，private cache 的内存库互相看不见表。
    /// </summary>
    private sealed class EvictionDb : IAsyncDisposable
    {
        private EvictionDb(string directory, DbContextOptions<PlatformDbContext> options)
        {
            Directory = directory;
            Options = options;
            Store = new AgentPoolEntryStore(
                new TestDbContextFactory(options),
                NullLogger<AgentPoolEntryStore>.Instance);
        }

        public string Directory { get; }

        public DbContextOptions<PlatformDbContext> Options { get; }

        public IAgentPoolEntryStore Store { get; }

        public static async Task<EvictionDb> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-eviction-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "platform.db")}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                // 与生产同一条建表路径（raw DDL），列名/索引语义必须与线上存量库一致。
                await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(db);
            }

            return new EvictionDb(directory, options);
        }

        public PlatformDbContext CreateDbContext() => new(Options);

        /// <summary>直读一行（含软删行）：store 的读接口看不见墓碑，墓碑断言只能走这里。</summary>
        public async Task<AgentPoolEntity> ReadRawAsync(string subSessionId)
        {
            await using var db = CreateDbContext();
            return await db.AgentPool.AsNoTracking().SingleAsync(x => x.SubSessionId == subSessionId);
        }

        public ValueTask DisposeAsync()
        {
            // 连接池会持有文件句柄，不先清池在 Windows 上删目录会失败。
            SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响断言结论，留给系统临时目录回收。
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
