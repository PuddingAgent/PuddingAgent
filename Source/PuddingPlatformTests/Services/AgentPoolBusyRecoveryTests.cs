using System.Globalization;
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
/// 重启后恢复「残留 busy」条目的验收（S5c）。
/// <para>
/// 这组用例要锁的<b>不是</b>「复位 SQL 跑过了」，而是两件更容易失效的事：
/// ① 判据是 <c>sub_agent_runs</c> 里还有没有非终态 run，而不是「调用得够早」——
/// 后者依赖 <c>BackgroundService.StartAsync</c> 不 await <c>ExecuteAsync</c> 主体这种约定，
/// 一旦启动顺序变了就会把正在跑的条目当成僵尸条目重置掉；
/// ② 恢复后条目真的能派活（内存视图与库两侧的门都必须放行）——只改库不改内存的话，
/// <c>ExecuteAsync</c> 的内存前置检查仍会报「当前忙」，用户看到的缺陷一模一样。
/// </para>
/// <para>
/// 每个用例独立建一个临时文件库（与 <c>AgentPoolWriteThroughTests</c> 同一条建库路径，
/// 额外用 <c>EnsureCreated</c> 补出 <c>sub_agent_runs</c>：恢复判据要读那张表）。
/// 共享库会让「只有某一条被处理」这类全局断言互相污染。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolBusyRecoveryTests
{
    /// <summary>
    /// 固定的「过去」时刻：必须早于当前时间，否则「复位把 last_used_at 推到 now」这条断言的
    /// 观测方向就反了（写死常量时要自己拿当前时间比一下，不要凭日期看起来像今天就下结论）。
    /// </summary>
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow.AddDays(-1);

    // ═══════════════════════════════════════════════════════
    // 1. 没有对应 run 的 busy ⇒ 复位成 sleeping，且恢复后能派活
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Recovery_ResetsBusyEntryWithoutActiveRun()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.AddPoolEntryAsync(
            name: "agent-busy",
            subSessionId: "parent-session-sub-1111aaaa",
            status: AgentPoolEntity.StatusBusy,
            activeExecutionId: "exec-from-crashed-process");

        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, db);

        await pool.EnsureReadyAsync();

        var row = await db.Store.GetBySubSessionAsync("parent-session-sub-1111aaaa", default);
        Assert.IsNotNull(row);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, row.Status,
            "库里没有非终态 run ⇒ 这条 busy 是上次进程崩溃留下的，必须复位");
        Assert.AreEqual(AgentPoolEntity.StatusReasonRecoveredBusy, row.StatusReason,
            "复位必须留下可诊断的原因，而不是一个与正常休眠无法区分的状态");
        Assert.IsNull(row.ActiveExecutionId, "复位必须清空执行令牌，否则旧执行的迟到收尾会写回本行");

        var memory = await pool.GetAsync("agent-busy");
        Assert.IsNotNull(memory);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, memory.Status,
            "内存视图必须跟随库：只改库的话，ExecuteAsync 的内存前置检查仍会报「当前忙」");

        // 本任务的核心断言：恢复后这条条目必须真的能派活（两侧的门都放行）。
        var result = await pool.ExecuteAsync("agent-busy", CreateSpawnRequest("after recovery"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, manager.ExecuteCallCount, "恢复后必须真的进入执行链");

        var after = await db.Store.GetBySubSessionAsync("parent-session-sub-1111aaaa", default);
        Assert.IsNotNull(after);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, after.Status, "派活成功后按既有语义回到 sleeping");
        Assert.AreEqual(1, after.TaskCount, "派活成功必须走完整收尾（库的 task_count 累加）");
        Assert.IsNull(after.ActiveExecutionId);

        Console.WriteLine(
            $"[evidence] recovered: status={row.Status}, reason={row.StatusReason}, " +
            $"memory={memory.Status}, execCalls={manager.ExecuteCallCount}, taskCount={after.TaskCount}");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 有非终态 run 的 busy ⇒ 不得重置（否则两个执行同时驱动一个子会话）
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Recovery_KeepsBusyEntryWithRunningRun()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.AddPoolEntryAsync(
            name: "agent-live",
            subSessionId: "parent-session-sub-2222bbbb",
            status: AgentPoolEntity.StatusBusy,
            activeExecutionId: "exec-still-running");
        await db.AddRunAsync("parent-session-sub-2222bbbb", SubAgentRunEntity.RunningStatus);

        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, db);

        await pool.EnsureReadyAsync();

        var row = await db.Store.GetBySubSessionAsync("parent-session-sub-2222bbbb", default);
        Assert.IsNotNull(row);
        Assert.AreEqual(AgentPoolEntity.StatusBusy, row.Status,
            "run 表里还有非终态 run ⇒ 这不是 stale；重置它会让两个执行同时驱动同一个子会话");
        Assert.AreEqual("exec-still-running", row.ActiveExecutionId, "未重置的行必须原样保留执行令牌");
        Assert.IsNull(row.StatusReason, "未重置的行不得被写上 recovered_busy");
        Assert.AreEqual(BaseTime, row.LastUsedAt, "未重置的行不得推进 last_used_at（那会打乱 LRU）");

        var memory = await pool.GetAsync("agent-live");
        Assert.IsNotNull(memory);
        Assert.AreEqual(PooledSubAgentStatus.Busy, memory.Status,
            "内存里也必须继续显示 busy：库没放行，内存先放行就等于绕过了判据");

        // 对照证明「没被重置」是真实行为而不是空断言：派活仍必须被拒。
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pool.ExecuteAsync("agent-live", CreateSpawnRequest("must not run")));
        StringAssert.Contains(ex.Message, "busy");
        Assert.AreEqual(0, manager.ExecuteCallCount, "真正在跑的条目不得被这次恢复间接放行");

        Console.WriteLine($"[evidence] kept busy: status={row.Status}, token={row.ActiveExecutionId}, reject={ex.Message}");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 只处理 busy —— idle / sleeping 一个字段都不许动
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Recovery_DoesNotTouchIdleOrSleepingEntries()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.AddPoolEntryAsync(
            name: "agent-idle",
            subSessionId: "parent-session-sub-3333cccc",
            status: AgentPoolEntity.StatusIdle,
            activeExecutionId: null);
        await db.AddPoolEntryAsync(
            name: "agent-sleeping",
            subSessionId: "parent-session-sub-4444dddd",
            status: AgentPoolEntity.StatusSleeping,
            activeExecutionId: null,
            statusReason: "idle_ttl");
        await db.AddPoolEntryAsync(
            name: "agent-busy",
            subSessionId: "parent-session-sub-5555eeee",
            status: AgentPoolEntity.StatusBusy,
            activeExecutionId: "exec-stale");

        var pool = CreatePool(new TestSubAgentManager(), db);

        await pool.EnsureReadyAsync();

        var idle = await db.Store.GetBySubSessionAsync("parent-session-sub-3333cccc", default);
        Assert.IsNotNull(idle);
        Assert.AreEqual(AgentPoolEntity.StatusIdle, idle.Status, "idle 本来就可用，不该被复位逻辑碰到");
        Assert.IsNull(idle.StatusReason);
        Assert.AreEqual(BaseTime, idle.LastUsedAt, "未被处理的条目不得推进 last_used_at");

        var sleeping = await db.Store.GetBySubSessionAsync("parent-session-sub-4444dddd", default);
        Assert.IsNotNull(sleeping);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, sleeping.Status);
        Assert.AreEqual("idle_ttl", sleeping.StatusReason,
            "已有原因不得被 recovered_busy 覆盖：那会把「为什么休眠」这段证据抹掉");
        Assert.AreEqual(BaseTime, sleeping.LastUsedAt);

        var busy = await db.Store.GetBySubSessionAsync("parent-session-sub-5555eeee", default);
        Assert.IsNotNull(busy);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, busy.Status, "只有 busy 条目会被处理");
        Assert.AreEqual(AgentPoolEntity.StatusReasonRecoveredBusy, busy.StatusReason);
        Assert.IsNull(busy.ActiveExecutionId);
        Assert.IsTrue(busy.LastUsedAt > BaseTime,
            "被救回的条目必须推进 last_used_at，否则它会立刻成为下一次 LRU 淘汰的首选");

        Console.WriteLine(
            $"[evidence] idle={idle.Status}/{idle.StatusReason}, " +
            $"sleeping={sleeping.Status}/{sleeping.StatusReason}, busy→{busy.Status}/{busy.StatusReason}");
    }

    // ═══════════════════════════════════════════════════════
    // 4. 幂等：再启动一次、同一实例再调一次，结果一致且不报错
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Recovery_IsIdempotent()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.AddPoolEntryAsync(
            name: "agent-busy",
            subSessionId: "parent-session-sub-6666ffff",
            status: AgentPoolEntity.StatusBusy,
            activeExecutionId: "exec-stale");

        var first = CreatePool(new TestSubAgentManager(), db);
        await first.EnsureReadyAsync();
        var afterFirst = await db.Store.GetBySubSessionAsync("parent-session-sub-6666ffff", default);
        Assert.IsNotNull(afterFirst);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, afterFirst.Status, "前置条件：第一次启动必须完成了复位");

        // 第二个池实例 = 再一次进程重启；同一实例再调一次 = 一次性加载不得重复复位。
        var second = CreatePool(new TestSubAgentManager(), db);
        await second.EnsureReadyAsync();
        await second.EnsureReadyAsync();

        var afterSecond = await db.Store.GetBySubSessionAsync("parent-session-sub-6666ffff", default);
        Assert.IsNotNull(afterSecond);
        Assert.AreEqual(AgentPoolEntity.StatusSleeping, afterSecond.Status);
        Assert.AreEqual(AgentPoolEntity.StatusReasonRecoveredBusy, afterSecond.StatusReason,
            "第二次恢复不得把原因改写成别的东西");
        Assert.IsNull(afterSecond.ActiveExecutionId);
        Assert.AreEqual(afterFirst.LastUsedAt, afterSecond.LastUsedAt,
            "条目已经不是 busy 时，复位不得再推进 last_used_at（否则每次重启都会把它从 LRU 里「洗白」）");

        var memory = await second.GetAsync("agent-busy");
        Assert.IsNotNull(memory);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, memory.Status);
        Assert.AreEqual(0, memory.TaskCount,
            "复位不清 task_count：那条执行确实发生过，抹掉会让「复用价值」指标偏低");

        Console.WriteLine($"[evidence] lastUsedAt={afterSecond.LastUsedAt:O}, status={afterSecond.Status}, reason={afterSecond.StatusReason}");
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
    /// 单个用例的独立环境：临时目录里的真实 SQLite 文件库 + 真实 store。
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
                Path.GetTempPath(), "pudding-agent-pool-busy-recovery-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "platform.db")}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                // ① EF 模型建全套表：恢复判据要读 sub_agent_runs，只建 agent_pool 的库跑不出真实行为。
                await db.Database.EnsureCreatedAsync();
                // ② 生产同一条池表建表路径（raw DDL + 三个索引），两条路径必须产出同一套 schema。
                await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(db);
            }

            return new PoolDb(directory, options);
        }

        public PlatformDbContext CreateDbContext() => new(Options);

        /// <summary>用 EF 直插一条池条目：store 刻意不提供「无条件造任意状态」，前置状态只能这样构造。</summary>
        public async Task AddPoolEntryAsync(
            string name,
            string subSessionId,
            string status,
            string? activeExecutionId,
            string? statusReason = null,
            DateTimeOffset? lastUsedAt = null)
        {
            await using var db = CreateDbContext();
            db.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = Guid.NewGuid().ToString("N"),
                OwnerWorkspaceId = "default",
                Name = name,
                SubSessionId = subSessionId,
                TemplateId = "workspace-task-agent",
                Role = "worker",
                OwnerSessionId = "parent-session",
                OwnerAgentId = "agent-1",
                ActiveExecutionId = activeExecutionId,
                Status = status,
                StatusReason = statusReason,
                TaskCount = 0,
                LastSuccess = null,
                CreatedAt = FormatTime(BaseTime),
                LastUsedAt = FormatTime(lastUsedAt ?? BaseTime),
            });
            await db.SaveChangesAsync();
        }

        /// <summary>插一条 run 归档索引行：池恢复的判据就是它。</summary>
        public async Task AddRunAsync(string subSessionId, string status)
        {
            await using var db = CreateDbContext();
            db.SubAgentRuns.Add(new SubAgentRunEntity
            {
                RunId = "run-" + Guid.NewGuid().ToString("N"),
                ParentSessionId = "parent-session",
                SubSessionId = subSessionId,
                WorkspaceId = "default",
                AgentInstanceId = "pooled-agent",
                TemplateId = "workspace-task-agent",
                Status = status,
                StartedAt = FormatTime(BaseTime.AddMinutes(-5)),
                CompletedAt = status == SubAgentRunEntity.RunningStatus ? null : FormatTime(BaseTime),
                ArchivePath = Path.Combine(Directory, "runs", "run-fixture"),
            });
            await db.SaveChangesAsync();
        }

        private static string FormatTime(DateTimeOffset value) =>
            value.ToString("O", CultureInfo.InvariantCulture);

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
