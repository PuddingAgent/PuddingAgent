using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 持久子代理池写路径验收（S5a：「DB 为准、缓存跟随」）。
/// <para>
/// 这组用例要锁的不是「调用成功」，而是<b>池的每一次状态变更都以库为真相</b>：
/// 抢占、收尾、休眠、销毁四条写路径，库里的条件更新返回 0 行时内存必须原地不动。
/// 若实现退化成「无条件写内存、顺手写一下库」，这里就会变红——而那正是
/// 「内存显示 sleeping、库里仍 busy」这类分叉的来源。
/// </para>
/// <para>
/// 每个用例独立建一个临时库（与 <c>AgentPoolEntryStoreTests</c> 同一条建表路径）：
/// 共享库会让容量与总数这类全局断言互相污染。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolWriteThroughTests
{
    // ═══════════════════════════════════════════════════════
    // 1. 执行：库先 busy，收尾后 sleeping + task_count++
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_WithStore_WritesBusyThenSleepingAndIncrementsTaskCount()
    {
        await using var db = await PoolDb.CreateAsync();
        var manager = new TestSubAgentManager { DelayMs = 300 };
        var pool = CreatePool(manager, db);

        var created = await pool.CreateAsync("agent-a", CreateSpawnRequest());

        var before = await db.Store.GetBySubSessionAsync(created.SubSessionId, default);
        Assert.IsNotNull(before, "CreateAsync 必须先落一行");
        Assert.AreEqual(AgentPoolEntity.StatusIdle, before.Status);
        Assert.IsNull(before.ActiveExecutionId);

        var exec = pool.ExecuteAsync("agent-a", CreateSpawnRequest("fix bug"));

        // 必须抓到中间态 busy：只看最终 sleeping 无法区分
        // 「真的先写过 busy」与「根本没有抢占这一步」。
        var busy = await WaitForStatusAsync(db, created.SubSessionId, AgentPoolEntity.StatusBusy);
        Assert.IsNotNull(busy.ActiveExecutionId, "置 busy 时必须同时写入执行令牌");

        var result = await exec;
        Assert.IsTrue(result.Success);

        var after = await db.Store.GetBySubSessionAsync(created.SubSessionId, default);
        Assert.IsNotNull(after);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, after.Status, "收尾后库里应回到 sleeping");
        Assert.AreEqual(1, after.TaskCount, "收尾必须累加 task_count");
        Assert.IsNull(after.ActiveExecutionId, "收尾必须清空执行令牌");
        Assert.IsTrue(after.LastSuccess, "成功收尾必须写 last_success=true");

        var snapshot = await pool.GetAsync("agent-a");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, snapshot.Status);
        Assert.AreEqual(1, snapshot.TaskCount, "缓存必须跟随库");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 执行：库抢不到（已被别的执行占着）→ 不进入执行链
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_WhenStoreAcquireReturnsZero_DoesNotEnterExecutionChain()
    {
        await using var db = await PoolDb.CreateAsync();
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, db);

        var created = await pool.CreateAsync("agent-a", CreateSpawnRequest());

        // 直接改库把这一行占住：池的内存视图看不到这个变化（它仍是 Idle）。
        // 因此能拦住它的只可能是 DB 条件更新，而不是内存里的 Busy 检查。
        await db.SetRawAsync(created.SubSessionId, AgentPoolEntity.StatusBusy, "foreign-exec");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pool.ExecuteAsync("agent-a", CreateSpawnRequest("must not run")));

        Console.WriteLine($"[evidence] acquire-zero reject: {ex.Message}");

        Assert.AreEqual(0, manager.ExecuteCallCount,
            "库抢不到时不得调用 manager（否则会真的建子会话并消费父级预算）");

        var row = await db.ReadRawAsync(created.SubSessionId);
        Assert.AreEqual(AgentPoolEntity.StatusBusy, row.Status, "被拒绝的执行不得改动库里的状态");
        Assert.AreEqual("foreign-exec", row.ActiveExecutionId, "被拒绝的执行不得覆盖别人的令牌");
        Assert.AreEqual(0, row.TaskCount);

        var snapshot = await pool.GetAsync("agent-a");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Idle, snapshot.Status,
            "拒绝必须发生在改内存之前：内存停在 Busy 会把这一条永久卡死");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 收尾：令牌在库里已失效 → 库与内存都不得被改
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Finish_WhenExecutionIdNoLongerMatches_DropsWithoutTouchingStoreOrMemory()
    {
        await using var db = await PoolDb.CreateAsync();
        var manager = new TestSubAgentManager { DelayMs = 400 };
        var pool = CreatePool(manager, db);

        var created = await pool.CreateAsync("agent-a", CreateSpawnRequest());

        var exec = pool.ExecuteAsync("agent-a", CreateSpawnRequest("slow task"));
        var busy = await WaitForStatusAsync(db, created.SubSessionId, AgentPoolEntity.StatusBusy);

        // 模拟执行途中条目被 sleep 清空令牌：库里的这一行已不归本次执行。
        var cleared = await db.Store.TrySetStatusAsync(
            busy.PoolId,
            AgentPoolEntity.StatusBusy,
            AgentPoolEntity.StatusSleeping,
            statusReason: null,
            clearExecution: true,
            now: DateTimeOffset.UtcNow,
            default);
        Assert.AreEqual(1, cleared, "前置条件：库里必须能清掉令牌");

        var result = await exec;
        Assert.IsTrue(result.Success, "manager 本身是成功的，被丢弃的是这次「收尾写回」");

        var row = await db.Store.GetBySubSessionAsync(created.SubSessionId, default);
        Assert.IsNotNull(row);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, row.Status);
        Assert.AreEqual(0, row.TaskCount, "库判定收尾不成立时 task_count 不得累加");
        Assert.IsNull(row.LastSuccess, "库判定收尾不成立时 last_success 不得被写");
        Assert.IsNull(row.ActiveExecutionId);

        var snapshot = await pool.GetAsync("agent-a");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(0, snapshot.TaskCount,
            "库拒绝时内存也不得改：内存是缓存不是事实源，分叉要到下一次派活才暴露");
        Assert.IsNull(snapshot.LastSuccess);
    }

    // ═══════════════════════════════════════════════════════
    // 4. 休眠：写库 sleeping 并清空执行令牌
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Sleep_WithStore_WritesSleepingAndClearsExecution()
    {
        await using var db = await PoolDb.CreateAsync();
        var pool = CreatePool(new TestSubAgentManager(), db);

        var created = await pool.CreateAsync("agent-a", CreateSpawnRequest());
        await pool.ExecuteAsync("agent-a", CreateSpawnRequest("first task"));

        // 造一个「sleeping 但残留令牌」的行：只有这种前置状态才能证明
        // clearExecution 真的生效（已经是 sleeping 且无令牌时，清不清都看不出差别）。
        var stored = await db.Store.GetBySubSessionAsync(created.SubSessionId, default);
        Assert.IsNotNull(stored);
        await db.SetRawAsync(created.SubSessionId, AgentPoolEntity.StatusSleeping, "exec-leftover");

        var slept = await pool.SleepAsync("agent-a");

        Console.WriteLine($"[evidence] sleep returned {slept}");

        Assert.IsTrue(slept, "内存与库同为 sleeping 时，休眠必须成功");
        var row = await db.Store.GetBySubSessionAsync(created.SubSessionId, default);
        Assert.IsNotNull(row);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, row.Status);
        Assert.IsNull(row.ActiveExecutionId, "SleepAsync 必须清空执行令牌（clearExecution: true）");
        Assert.IsNull(row.StatusReason);
    }

    // ═══════════════════════════════════════════════════════
    // 5. 销毁：写库 dead + deleted_at
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Destroy_WithStore_MarksDeadAndSetsDeletedAt()
    {
        await using var db = await PoolDb.CreateAsync();
        var pool = CreatePool(new TestSubAgentManager(), db);

        var created = await pool.CreateAsync("agent-a", CreateSpawnRequest());

        var destroyed = await pool.DestroyAsync("agent-a");

        Console.WriteLine($"[evidence] destroy returned {destroyed}");

        Assert.IsTrue(destroyed);
        Assert.AreEqual(0, pool.Count, "销毁后内存里不得留下条目");
        Assert.IsNull(await pool.GetAsync("agent-a"));

        var row = await db.ReadRawAsync(created.SubSessionId);
        Assert.AreEqual(AgentPoolEntity.StatusDead, row.Status, "销毁必须把库里的状态写成 dead");
        Assert.IsNotNull(row.DeletedAt, "置 dead 必须同时写 deleted_at，否则唯一索引仍被占住");
        Assert.IsNull(await db.Store.GetBySubSessionAsync(created.SubSessionId, default),
            "软删后条目按复用键不可见");
    }

    // ═══════════════════════════════════════════════════════
    // 6. 重启：pool_id 从库里带回来
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Create_ThenReload_ReusesSamePoolId()
    {
        await using var db = await PoolDb.CreateAsync();
        var first = CreatePool(new TestSubAgentManager(), db);
        var created = await first.CreateAsync("agent-a", CreateSpawnRequest());

        var persisted = await db.Store.GetBySubSessionAsync(created.SubSessionId, default);
        Assert.IsNotNull(persisted, "创建必须落库，pool_id 是后续一切条件写的主键");

        // 新实例 = 一次进程重启：内存视图只能从库重建。
        var reloaded = CreatePool(new TestSubAgentManager(), db);
        var loaded = await reloaded.GetSnapshotAsync();
        Assert.HasCount(1, loaded);
        Assert.AreEqual("agent-a", loaded[0].Name);

        // PoolId 是内部字段，用行为间接证明它被带回来了：
        // 只有 pool_id 与库里那一行对得上，这条条件写才会命中。
        var slept = await reloaded.SleepAsync("agent-a");
        Assert.IsTrue(slept, "重启后必须能凭库里带回来的 pool_id 写库");

        var after = await db.Store.GetByIdAsync(persisted.PoolId, default);
        Assert.IsNotNull(after);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, after.Status);

        // 对照：同一个谓词换成一个不存在的 pool_id 就是 0 行。
        // 这说明上面那条 1 行不是「无条件更新」的假绿。
        var bogus = await db.Store.TrySetStatusAsync(
            "not-a-real-pool-id",
            AgentPoolEntity.StatusSleeping,
            AgentPoolEntity.StatusDead,
            statusReason: null,
            clearExecution: true,
            now: DateTimeOffset.UtcNow,
            default);
        Assert.AreEqual(0, bogus, "不存在的 pool_id 必须写不到任何行");

        Console.WriteLine($"[evidence] poolId={persisted.PoolId}, slept={slept}, bogus={bogus}");
    }

    // ── 测试脚手架 ──────────────────────────────────────────

    private static SubAgentPool CreatePool(TestSubAgentManager manager, PoolDb db)
        => new(
            manager,
            new ConfigurationBuilder().Build(),
            NullLogger<SubAgentPool>.Instance,
            db.Store);

    private static SubAgentSpawnRequest CreateSpawnRequest(string task = "test task") => new()
    {
        ParentSessionId = "parent-session",
        WorkspaceId = "default",
        TaskDescription = task,
        LlmConfig = new LlmConfig
        {
            ModelId = "test-model",
            KeyVaultId = "test-key",
            Endpoint = "https://test.example.com/v1",
        },
        LlmProfile = new LlmInvocationProfile
        {
            ProviderId = "test-provider",
            ProfileId = "subagent.conscious",
            ModelId = "test-model",
            Role = "conscious",
        },
        TemplateId = "workspace-task-agent",
        RoleInPlan = "worker",
    };

    /// <summary>
    /// 等到库里出现期望状态。用真实库轮询而不是睡固定时长：
    /// 固定延时在慢机器上会随机失败，而且失败信息只会说「状态不对」。
    /// </summary>
    private static async Task<AgentPoolEntrySnapshot> WaitForStatusAsync(
        PoolDb db,
        string subSessionId,
        string expected,
        int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        AgentPoolEntrySnapshot? last = null;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            last = await db.Store.GetBySubSessionAsync(subSessionId, default);
            if (last?.Status == expected)
                return last;
            await Task.Delay(10);
        }

        Assert.Fail(
            $"Expected store status '{expected}' for {subSessionId} within {timeoutMs}ms, " +
            $"last observed '{last?.Status ?? "<missing>"}'");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// 单个用例的独立环境：一个临时目录里的真实 SQLite 文件库 + 真实 store。
    /// 刻意不用内存库：多连接（池写库 / 用例直读）在 private cache 的内存库上互相看不见表。
    /// </summary>
    private sealed class PoolDb : IAsyncDisposable
    {
        private PoolDb(string directory, DbContextOptions<PlatformDbContext> options)
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

        public static async Task<PoolDb> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-writethrough-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "platform.db")}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                // 用生产同一条建表路径（raw DDL），列名/索引语义必须与线上存量库一致。
                await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(db);
            }

            return new PoolDb(directory, options);
        }

        public PlatformDbContext CreateDbContext() => new(Options);

        /// <summary>
        /// 绕过 store 直改列：模拟「另一个写者」造成的前置状态（例如休眠已清掉令牌）。
        /// store 的条件原语刻意不提供「无条件置成任意状态」，所以这种构造只能用 EF 做。
        /// </summary>
        public async Task SetRawAsync(string subSessionId, string status, string? activeExecutionId)
        {
            await using var db = CreateDbContext();
            var row = await db.AgentPool.SingleAsync(x => x.SubSessionId == subSessionId);
            row.Status = status;
            row.ActiveExecutionId = activeExecutionId;
            await db.SaveChangesAsync();
        }

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
