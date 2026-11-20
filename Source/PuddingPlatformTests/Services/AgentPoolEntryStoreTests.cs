using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatformTests.Services;

/// <summary>
/// <see cref="AgentPoolEntryStore"/> 的存储语义验收（S2）。
/// <para>
/// 每个用例的断言都针对「一条并发语义」而不是「一次调用成功」：
/// 池的真实风险是重复认领（两个调度者驱动同一条子代理）、迟到收尾覆盖新执行、
/// 超量淘汰、以及半写行占死唯一索引。所以这些用例刻意去构造<b>不该成功</b>的调用，
/// 并要求它返回 0 行且不改动任何列——如果实现退化成「先查后改」，这里就会变红。
/// </para>
/// <para>
/// 每个用例独立建一个临时库：共享库会让「容量」与「总数」这类全局断言互相污染，
/// 一旦串行执行顺序变化就随机失败。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolEntryStoreTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TryInsert_AtCapacity_ReturnsZeroAndLeavesNoRow()
    {
        await using var scope = await TestScope.CreateAsync();

        var first = await scope.Store.TryInsertAsync(Entry("pool-1"), capacity: 1, default);
        // 第二条换掉 pool_id / name / sub_session_id：若实现先写后判（或回滚不到位），
        // 残留行会同时占住 name 与 sub_session 两个唯一索引，这里就能看出来。
        var second = await scope.Store.TryInsertAsync(Entry("pool-2"), capacity: 1, default);

        Console.WriteLine($"[evidence] insert#1={first}, insert#2={second}");

        Assert.AreEqual(1, first, "池未满时插入必须返回 1 行");
        Assert.AreEqual(0, second, "池满（cap=1 且已有 1 条）时插入必须返回 0");

        var all = await scope.Store.LoadAllAsync(default);
        Console.WriteLine($"[evidence] rows after rejected insert = {all.Count}");

        Assert.HasCount(1, all, "被容量拒绝的插入不得留下半写行");
        Assert.IsNull(await scope.Store.GetByIdAsync("pool-2", default), "被拒绝的 pool-2 不应存在");
    }

    [TestMethod]
    public async Task TryAcquire_WhenOwnerSessionDiffers_ReturnsZeroAndKeepsIdle()
    {
        await using var scope = await TestScope.CreateAsync();
        await InsertAsync(scope, Entry("pool-1", ownerSessionId: "session-a"));

        var affected = await scope.Store.TryAcquireAsync(
            "pool-1", ownerSessionId: "session-b", ownerWorkspaceId: "default",
            executionId: "exec-1", now: BaseTime.AddMinutes(1), default);

        Console.WriteLine($"[evidence] acquire by foreign session => {affected}");

        Assert.AreEqual(0, affected, "归属不匹配的会话不得抢到条目");

        var snapshot = await scope.Store.GetByIdAsync("pool-1", default);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(AgentPoolEntity.StatusIdle, snapshot.Status, "抢失败后状态必须仍是 idle");
        Assert.IsNull(snapshot.ActiveExecutionId, "抢失败后不得写入执行令牌");
    }

    [TestMethod]
    public async Task TryAcquire_WhenAlreadyBusy_ReturnsZero()
    {
        await using var scope = await TestScope.CreateAsync();
        await InsertAsync(scope, Entry("pool-1"));

        var first = await scope.Store.TryAcquireAsync(
            "pool-1", "session-a", "default", "exec-1", BaseTime.AddMinutes(1), default);
        var second = await scope.Store.TryAcquireAsync(
            "pool-1", "session-a", "default", "exec-2", BaseTime.AddMinutes(2), default);

        Console.WriteLine($"[evidence] acquire#1={first}, acquire#2={second}");

        Assert.AreEqual(1, first, "空闲条目应能被抢到");
        Assert.AreEqual(0, second, "状态已是 busy 时第二次抢占必须失败（不能一仆二主）");

        var snapshot = await scope.Store.GetByIdAsync("pool-1", default);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(AgentPoolEntity.StatusBusy, snapshot.Status);
        Assert.AreEqual("exec-1", snapshot.ActiveExecutionId, "第二次抢占不得覆盖第一次的执行令牌");
    }

    [TestMethod]
    public async Task TryComplete_WhenExecutionIdMismatch_ReturnsZeroAndDoesNotTouchCounters()
    {
        await using var scope = await TestScope.CreateAsync();
        await InsertAsync(scope, Entry("pool-1"));
        await scope.Store.TryAcquireAsync(
            "pool-1", "session-a", "default", "exec-1", BaseTime.AddMinutes(1), default);

        // 迟到收尾：令牌对不上（模拟上一轮执行醒来时条目已被新执行占用）。
        var affected = await scope.Store.TryCompleteAsync(
            "pool-1", subSessionId: "pool-1-sub", executionId: "exec-stale",
            success: true, now: BaseTime.AddMinutes(5), default);

        Console.WriteLine($"[evidence] complete with stale token => {affected}");

        Assert.AreEqual(0, affected, "执行令牌不匹配的收尾必须失败");

        var snapshot = await scope.Store.GetByIdAsync("pool-1", default);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(AgentPoolEntity.StatusBusy, snapshot.Status, "失败的收尾不得把 busy 改成其它状态");
        Assert.AreEqual(0, snapshot.TaskCount, "失败的收尾不得累加 task_count");
        Assert.IsNull(snapshot.LastSuccess, "失败的收尾不得写 last_success");
        Assert.AreEqual("exec-1", snapshot.ActiveExecutionId, "失败的收尾不得清空当前执行令牌");
    }

    [TestMethod]
    public async Task TryComplete_MatchingExecution_IncrementsCountAndSleeps()
    {
        await using var scope = await TestScope.CreateAsync();
        await InsertAsync(scope, Entry("pool-1"));
        await scope.Store.TryAcquireAsync(
            "pool-1", "session-a", "default", "exec-1", BaseTime.AddMinutes(1), default);

        var affected = await scope.Store.TryCompleteAsync(
            "pool-1", subSessionId: "pool-1-sub", executionId: "exec-1",
            success: true, now: BaseTime.AddMinutes(7), default);

        Console.WriteLine($"[evidence] complete with matching token => {affected}");

        Assert.AreEqual(1, affected, "令牌与复用键都匹配的收尾必须成功");

        var snapshot = await scope.Store.GetByIdAsync("pool-1", default);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, snapshot.Status, "收尾后应回到 sleeping 等待复用");
        Assert.AreEqual(1, snapshot.TaskCount, "收尾必须累加 task_count");
        Assert.IsTrue(snapshot.LastSuccess, "成功收尾必须写 last_success=true");
        Assert.IsNull(snapshot.ActiveExecutionId, "收尾必须清空执行令牌");
        Assert.IsNull(snapshot.StatusReason, "成功收尾不应留下失败原因");
    }

    [TestMethod]
    public async Task TryEvictOldestSleeping_TwiceConcurrently_DeletesAtMostOneRow()
    {
        await using var scope = await TestScope.CreateAsync();
        await InsertAsync(scope, Entry("pool-1"));
        var slept = await scope.Store.TrySetStatusAsync(
            "pool-1", AgentPoolEntity.StatusIdle, AgentPoolEntity.StatusSleeping,
            statusReason: null, clearExecution: false, now: BaseTime.AddMinutes(1), default);
        Assert.AreEqual(1, slept, "前置条件：条目必须变成 sleeping");

        // 两个驱逐者同时出手：只有一条 sleeping 条目，因此总驱逐行数必须 <= 1。
        //
        // 用 Task.Run 把它们放上两个线程，是因为 SQLite 的 EF 异步实现底层是同步的
        // （SQLite 没有真正的异步 I/O），直接拿两个 Task 再 await 会在同一个线程上顺序完成，
        // 那样这个用例根本不可能并发，只能证明两次串行调用之和 <= 1。
        //
        // 诚实地说明本用例的检测边界：它锁定的是「结果不变量」（不得超量淘汰、
        // 不得报 pool_id 却没打墓碑、两次合计最多一条）；实现到底是用单条 UPDATE 子查询
        // 还是事务内「SELECT → UPDATE」，这个用例都应当绿——那两种写法在结果上等价。
        // 两者真正的区分点在调用方：只返回行数时内存无法跟随，由 AgentPoolEvictionTests 覆盖。
        var first = Task.Run(() => scope.Store.TryEvictOldestSleepingAsync(BaseTime.AddHours(1), default));
        var second = Task.Run(() => scope.Store.TryEvictOldestSleepingAsync(BaseTime.AddHours(1), default));
        var results = await Task.WhenAll(first, second);
        // 返回类型是「被淘汰的 pool_id 或 null」：非空的结果就代表这一次真的删到了行。
        var winners = results.Where(x => x is not null).ToList();

        await using var db = scope.CreateDbContext();
        var tombstonedRows = await db.AgentPool.Where(x => x.DeletedAt != null).ToListAsync();
        var rows = await db.AgentPool.CountAsync();

        Console.WriteLine($"[evidence] evict#1={results[0] ?? "<null>"}, evict#2={results[1] ?? "<null>"}, winners={winners.Count}, tombstoned={tombstonedRows.Count}, rows={rows}");

        Assert.IsLessThanOrEqualTo(1, winners.Count, $"并发两次驱逐总共最多只能删 1 条，实际 {winners.Count} 条");
        Assert.AreEqual(1, tombstonedRows.Count, "只有一条 sleeping 条目，墓碑数必须恰好为 1");
        Assert.AreEqual(winners.Count, tombstonedRows.Count,
            "只有真正删到行的那次调用才能返回 pool_id（不得报 pool_id 却没打墓碑）");
        if (winners.Count == 1)
        {
            Assert.AreEqual("pool-1", winners[0], "唯一一条 sleeping 被淘汰后，返回的必须是它的 pool_id");
            Assert.AreEqual("pool-1", tombstonedRows[0].PoolId, "墓碑必须落在被返回的那一条上");
        }

        Assert.AreEqual(1, rows, "驱逐是软删，物理行必须仍在（否则诊断与去重都丢失）");
    }

    [TestMethod]
    public async Task TryEvictOldestSleeping_WithThreeSleepingEntries_EvictsExactlyTheOldestOne()
    {
        await using var scope = await TestScope.CreateAsync();

        // 三条都能被复用，但 last_used_at 不同（02:11 / 02:12 / 02:13）：
        // 淘汰必须挑最久没用的那条，而且只挑一条。
        //
        // 「只挑一条」是本用例存在的理由：天真的写法 UPDATE ... WHERE status='sleeping'
        // （漏掉「选出唯一一条」的限定）会一次把整个池清空，而这条缺陷在只有一条 sleeping
        // 条目的用例里完全看不出来 —— 那种用例下两种写法的结果一模一样。
        for (var i = 1; i <= 3; i++)
        {
            await InsertAsync(scope, Entry($"pool-{i}"));
            Assert.AreEqual(1, await scope.Store.TryAcquireAsync(
                $"pool-{i}", "session-a", "default", $"exec-{i}", BaseTime.AddMinutes(i), default),
                $"前置条件：pool-{i} 必须能被抢到");
            // 收尾把 last_used_at 推进到 02:1i 并回到 sleeping，顺带让 task_count 非 0。
            Assert.AreEqual(1, await scope.Store.TryCompleteAsync(
                $"pool-{i}", $"pool-{i}-sub", $"exec-{i}", success: true, now: BaseTime.AddMinutes(10 + i), default),
                $"前置条件：pool-{i} 必须能正常收尾");
        }

        var evicted = await scope.Store.TryEvictOldestSleepingAsync(BaseTime.AddHours(1), default);

        await using var db = scope.CreateDbContext();
        var tombstoned = await db.AgentPool.CountAsync(x => x.DeletedAt != null);
        var remaining = await scope.Store.LoadAllAsync(default);

        Console.WriteLine($"[evidence] evicted={evicted ?? "<null>"}, tombstoned={tombstoned}, remaining={string.Join(",", remaining.Select(x => x.PoolId))}");

        Assert.AreEqual("pool-1", evicted,
            "必须返回被淘汰那条的 pool_id（last_used_at 最早的 pool-1），而不是行数或其它条目");
        Assert.AreEqual(1, tombstoned, "墓碑数必须与返回结果一致");
        Assert.HasCount(2, remaining, "三条 sleeping 淘汰一条后必须还剩两条");
        Assert.IsNull(await scope.Store.GetByIdAsync("pool-1", default), "必须淘汰 last_used_at 最早的那条（pool-1）");
        Assert.IsNotNull(await scope.Store.GetByIdAsync("pool-2", default), "较新的 pool-2 不得被误淘汰");
        Assert.IsNotNull(await scope.Store.GetByIdAsync("pool-3", default), "最新的 pool-3 不得被误淘汰");
    }

    [TestMethod]
    public async Task ResetStaleBusy_SetsSleepingReasonAndKeepsTaskCount()
    {
        await using var scope = await TestScope.CreateAsync();
        await InsertAsync(scope, Entry("pool-1"));

        // 先跑完一次正常执行，让 task_count=1，这样「复位不改 task_count」才有可观测差别。
        await scope.Store.TryAcquireAsync(
            "pool-1", "session-a", "default", "exec-1", BaseTime.AddMinutes(1), default);
        await scope.Store.TryCompleteAsync(
            "pool-1", "pool-1-sub", "exec-1", success: true, now: BaseTime.AddMinutes(2), default);

        // 再抢一次并「假装进程在此崩溃」：留下 busy + 非空执行令牌。
        var acquired = await scope.Store.TryAcquireAsync(
            "pool-1", "session-a", "default", "exec-2", BaseTime.AddMinutes(3), default);
        Assert.AreEqual(1, acquired, "前置条件：条目必须重新进入 busy");

        var reset = await scope.Store.ResetStaleBusyAsync(BaseTime.AddMinutes(30), default);
        // 第二次复位必须是 0：没有 busy 条目时不得产生「假更新」。
        var again = await scope.Store.ResetStaleBusyAsync(BaseTime.AddMinutes(31), default);

        Console.WriteLine($"[evidence] reset#1={reset}, reset#2={again}");

        Assert.AreEqual(1, reset, "存在 busy 条目时复位必须命中 1 行");
        Assert.AreEqual(0, again, "已无 busy 条目时复位必须返回 0（不得重复变更）");

        var snapshot = await scope.Store.GetByIdAsync("pool-1", default);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, snapshot.Status, "复位后应回到 sleeping");
        Assert.AreEqual("recovered_busy", snapshot.StatusReason, "复位必须写明原因，便于诊断卡死条目");
        Assert.IsNull(snapshot.ActiveExecutionId, "复位必须清空残留执行令牌");
        Assert.AreEqual(1, snapshot.TaskCount, "复位不得改动 task_count（那次执行确实发生过）");
    }

    [TestMethod]
    public async Task LoadAll_ExcludesSoftDeleted()
    {
        await using var scope = await TestScope.CreateAsync();
        await InsertAsync(scope, Entry("pool-1"));
        await InsertAsync(scope, Entry("pool-2"));

        var killed = await scope.Store.TrySetStatusAsync(
            "pool-2", AgentPoolEntity.StatusIdle, AgentPoolEntity.StatusDead,
            statusReason: "destroyed", clearExecution: false,
            now: BaseTime.AddHours(1), default);
        Assert.AreEqual(1, killed, "前置条件：pool-2 必须能被置为 dead");

        var all = await scope.Store.LoadAllAsync(default);
        Console.WriteLine($"[evidence] LoadAll count = {all.Count}, poolIds = {string.Join(",", all.Select(x => x.PoolId))}");

        Assert.HasCount(1, all, "LoadAll 必须只返回未软删的条目");
        Assert.AreEqual("pool-1", all[0].PoolId);
        Assert.IsNull(await scope.Store.GetByIdAsync("pool-2", default), "已软删条目按 id 也不可见");
        Assert.IsNull(await scope.Store.GetBySubSessionAsync("pool-2-sub", default), "已软删条目按复用键也不可见");

        await using var db = scope.CreateDbContext();
        Assert.AreEqual(2, await db.AgentPool.CountAsync(), "软删不得物理删除行（墓碑要留给诊断与去重）");
        var dead = await db.AgentPool.SingleAsync(x => x.PoolId == "pool-2");
        Assert.IsNotNull(dead.DeletedAt, "置 dead 必须同时写 deleted_at，否则唯一索引仍被占住");
    }

    // ── 测试脚手架 ──────────────────────────────────────────

    private static AgentPoolInsertRequest Entry(
        string poolId,
        string? ownerSessionId = "session-a",
        string? name = null,
        string? subSessionId = null,
        string ownerWorkspaceId = "default") => new(
            PoolId: poolId,
            Name: name ?? poolId,
            SubSessionId: subSessionId ?? $"{poolId}-sub",
            TemplateId: "workspace-task-agent",
            Role: "worker",
            OwnerWorkspaceId: ownerWorkspaceId,
            OwnerSessionId: ownerSessionId,
            OwnerAgentId: "agent-1",
            CreatedAt: BaseTime);

    private static Task<int> InsertAsync(TestScope scope, AgentPoolInsertRequest request)
        => scope.Store.TryInsertAsync(request, capacity: 0, default);

    /// <summary>
    /// 单个用例的独立环境：一个临时目录里的真实 SQLite 文件库 + 真实 DDL。
    /// <para>
    /// 刻意不用内存库：<c>TryEvictOldestSleepingAsync</c> 的并发用例需要两条真实连接，
    /// 内存库（默认 private cache）在第二条连接上会看不到表。
    /// </para>
    /// </summary>
    private sealed class TestScope : IAsyncDisposable
    {
        private TestScope(string directory, DbContextOptions<PlatformDbContext> options)
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

        public static async Task<TestScope> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "platform.db")}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                // 用生产同一条建表路径（raw DDL）而不是 EnsureCreated：
                // 断言的列名/索引语义必须与线上存量库完全一致。
                await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(db);

                // 但 ResetStaleBusyAsync 的判据要读 sub_agent_runs（「有没有非终态 run」），
                // 所以那张表必须存在。这里刻意不用 EnsureCreated 补它：EnsureCreated 在
                // 「库里已经有任何表」时什么都不做，并且会顺带把 agent_pool 改成由 EF 模型建，
                // 本 fixture 想锁的 raw DDL 路径就没了。列名与 FileSubAgentRunStoreTests 的
                // 已验证 fixture 保持一致（同一张表两种建法产出不同列集合时，查询会只在一条路径上炸）。
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "sub_agent_runs" (
                        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id              TEXT    NOT NULL UNIQUE,
                        parent_session_id   TEXT    NOT NULL,
                        parent_turn_id      TEXT,
                        parent_command_id   TEXT,
                        parent_run_id       TEXT,
                        sub_session_id      TEXT    NOT NULL,
                        workspace_id        TEXT    NOT NULL,
                        agent_instance_id   TEXT    NOT NULL,
                        template_id         TEXT    NOT NULL,
                        status              TEXT    NOT NULL DEFAULT 'running',
                        started_at          TEXT    NOT NULL,
                        completed_at        TEXT,
                        archive_path        TEXT    NOT NULL,
                        trace_id            TEXT,
                        correlation_id      TEXT,
                        error_message       TEXT,
                        task_planning_metadata_json TEXT,
                        total_rounds        INTEGER NOT NULL DEFAULT 0,
                        total_tool_calls    INTEGER NOT NULL DEFAULT 0,
                        total_duration_ms   INTEGER NOT NULL DEFAULT 0
                    );
                    """);
            }

            return new TestScope(directory, options);
        }

        /// <summary>另开一个 context（断言用），与 store 内部的短生命周期 context 互不共享。</summary>
        public PlatformDbContext CreateDbContext() => new(Options);

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
