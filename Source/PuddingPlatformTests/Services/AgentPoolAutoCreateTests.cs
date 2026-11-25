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
/// 自动建员（<c>ExecuteAsync</c> 遇到未知名字时原地建条目）的<b>池层</b>守卫。
/// <para>
/// 为什么要单独锁这一层：在此之前所有变异都只能打在 <c>AgentPoolEntryStore</c> 上，
/// 池层调用点本身没有任何用例守护。于是「把一次性的插入+抢占改回两步提交」这种回退
/// 只会在存储层用例里保持全绿——而真正产生产生无主行的正是池层的调用形状。
/// </para>
/// <para>
/// 两个用例分工：
/// <list type="number">
/// <item>真实 SQLite：自动建出来的行，其模型 / 备注 / 头像 / 角色必须与显式创建一致。
/// 之前这些字段只在 <see cref="SubAgentPool.CreateAsync"/> 那一侧写过，自动建员那份漏了，
/// 结果「派活时自动长出来的成员」在界面上没有模型、没有备注、没有头像。</item>
/// <item>记录型 store：自动建员只允许调用「插入+抢占」这一个复合动作；
/// 库拒绝时既不得退回两步提交，也不得进入执行链。</item>
/// </list>
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolAutoCreateTests
{
    // ═══════════════════════════════════════════════════════
    // 1. 自动建员落库的字段与显式创建同源
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_WhenEntryMissing_PersistsDisplayFieldsAndOwnership()
    {
        await using var db = await PoolDb.CreateAsync();
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, db);

        // 池里没有 auto-a：这条路径就是「派活时自动建员」。
        var request = CreateSpawnRequest() with
        {
            Responsibility = "owns the review gate",
            AvatarKey = "owl",
        };

        var result = await pool.ExecuteAsync("auto-a", request);

        Assert.IsTrue(result.Success, "自动建员成功时执行本身必须成功");
        Assert.AreEqual(1, manager.ExecuteCallCount, "自动建员必须真的把任务派出去");

        var row = await db.SingleByNameAsync("auto-a");

        Assert.AreEqual("test-provider/test-model", row.ModelId,
            "自动建员必须写下成员声明的模型路由：否则界面上这个成员没有模型，"
            + "而显式创建出来的同位置却有——同一个表两种口径");
        Assert.AreEqual("owns the review gate", row.Responsibility,
            "职责必须在建员时落库，否则只能事后靠 PATCH 补");
        Assert.AreEqual("owl", row.AvatarKey, "头像键同理");

        Assert.AreEqual("worker", row.Role, "角色取自本次派发的 RoleInPlan");
        Assert.AreEqual("workspace-task-agent", row.TemplateId);
        Assert.AreEqual("parent-session", row.OwnerSessionId, "归属必须是父会话，否则抢占谓词恒假");
        Assert.AreEqual("default", row.OwnerWorkspaceId);

        Assert.AreEqual(AgentPoolEntity.StatusSleeping, row.Status, "执行收尾后应回到 sleeping");
        Assert.AreEqual(1, row.TaskCount, "收尾必须累加 task_count");
        Assert.IsNull(row.ActiveExecutionId, "收尾必须清空执行令牌");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 库拒绝时：不退回两步提交、不进入执行链
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_WhenSingleStepInsertRejected_DoesNotFallBackToTwoStep()
    {
        var store = new RecordingStore(insertAndAcquireResult: 0);
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, store, capacity: 5);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pool.ExecuteAsync("auto-b", CreateSpawnRequest()));

        Console.WriteLine($"[evidence] auto-create reject: {ex.Message}");

        Assert.AreEqual(1, store.InsertAndAcquireCalls,
            "自动建员必须用「插入+抢占」复合动作");
        Assert.AreEqual(0, store.InsertCalls,
            "不得退回两步提交：插入与抢占之间那段窗口里，库里有一行 idle、内存里没有条目，"
            + "抢占失败时这行没人收尾却仍占着唯一索引，同名之后的派发会永久撞索引");
        Assert.AreEqual(0, store.AcquireCalls,
            "复合动作已经包含抢占，不得再单独抢一次");

        Assert.AreEqual(0, manager.ExecuteCallCount,
            "库没抢到就不得进入执行链（否则会真的建子会话并消费父级预算）");

        var sent = store.LastInsertAndAcquireRequest;
        Assert.IsNotNull(sent);
        Assert.AreEqual("parent-session", sent.OwnerSessionId, "归属必须随插入一起提交");
        Assert.AreEqual("default", sent.OwnerWorkspaceId);
        Assert.AreEqual("test-provider/test-model", sent.ModelId,
            "池层传下去的插入请求必须带模型路由（字段映射只在 BuildInsertRequest 一处）");
        Assert.AreEqual("owns the review gate", sent.Responsibility,
            "职责字段必须出现在池层构造的插入请求里");
        Assert.AreEqual("owl", sent.AvatarKey, "头像键同理：字段映射只应有一处，漏一个就是两种口径");
    }

    // ── 测试脚手架 ──────────────────────────────────────────

    private static SubAgentPool CreatePool(
        TestSubAgentManager manager,
        PoolDb db,
        int capacity = 0) => CreatePoolCore(manager, db.Store, capacity);

    private static SubAgentPool CreatePool(
        TestSubAgentManager manager,
        IAgentPoolEntryStore store,
        int capacity = 0) => CreatePoolCore(manager, store, capacity);

    private static SubAgentPool CreatePoolCore(
        TestSubAgentManager manager,
        IAgentPoolEntryStore store,
        int capacity)
    {
        var settings = new Dictionary<string, string?>();
        if (capacity > 0)
            settings["SubAgentPool:MaxCapacity"] = capacity.ToString();

        return new SubAgentPool(
            manager,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<SubAgentPool>.Instance,
            store);
    }

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
        Responsibility = "owns the review gate",
        AvatarKey = "owl",
    };

    /// <summary>
    /// 只记录调用形状、不落库的 store：用来区分「一次复合动作」与「两次提交」。
    /// 读路径返回空集，让池把 <c>auto-b</c> 当成未知名字走自动建员分支。
    /// </summary>
    private sealed class RecordingStore(int insertAndAcquireResult) : IAgentPoolEntryStore
    {
        public int InsertCalls { get; private set; }
        public int InsertAndAcquireCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public AgentPoolInsertRequest? LastInsertAndAcquireRequest { get; private set; }

        public Task<IReadOnlyList<AgentPoolEntrySnapshot>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentPoolEntrySnapshot>>([]);

        public Task<int> ResetStaleBusyAsync(DateTimeOffset now, CancellationToken ct)
            => Task.FromResult(0);

        public Task<int> TryInsertAsync(AgentPoolInsertRequest request, int capacity, CancellationToken ct)
        {
            InsertCalls++;
            return Task.FromResult(1);
        }

        public Task<int> TryInsertAndAcquireAsync(AgentPoolInsertRequest request, int capacity,
            string executionId, DateTimeOffset now, CancellationToken ct)
        {
            InsertAndAcquireCalls++;
            LastInsertAndAcquireRequest = request;
            return Task.FromResult(insertAndAcquireResult);
        }

        public Task<int> TryAcquireAsync(string poolId, string ownerSessionId, string ownerWorkspaceId,
            string executionId, DateTimeOffset now, CancellationToken ct)
        {
            AcquireCalls++;
            return Task.FromResult(1);
        }

        public Task<int> TryCompleteAsync(string poolId, string subSessionId, string executionId,
            bool success, DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("本条用例不应进入收尾路径");

        public Task<int> TrySetStatusAsync(string poolId, string expectedStatus, string newStatus,
            string? statusReason, bool clearExecution, DateTimeOffset now, CancellationToken ct,
            string? expectedOwnerSessionId = null, string? expectedOwnerWorkspaceId = null)
            => Task.FromResult(0);

        public Task<string?> TryEvictOldestSleepingAsync(DateTimeOffset now, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<AgentPoolEntrySnapshot?> GetByIdAsync(string poolId, CancellationToken ct)
            => Task.FromResult<AgentPoolEntrySnapshot?>(null);

        public Task<AgentPoolEntrySnapshot?> GetBySubSessionAsync(string subSessionId, CancellationToken ct)
            => Task.FromResult<AgentPoolEntrySnapshot?>(null);
    }

    /// <summary>
    /// 单个用例的独立临时 SQLite 文件库（与 <c>AgentPoolWriteThroughTests</c> 同一建表路径）：
    /// 共享库会让容量与总数这类全局断言互相污染。
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
                Path.GetTempPath(), "pudding-agent-pool-autocreate-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "platform.db")}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(db);
            }

            return new PoolDb(directory, options);
        }

        public PlatformDbContext CreateDbContext() => new(Options);

        /// <summary>
        /// 按名字直读一行（含软删行）。
        /// 刻意不用 <c>GetBySubSessionAsync</c>：这里要断言的是「插入时写了哪些列」，
        /// 走 store 的读投影会只看得到它愿意暴露的那部分列。
        /// </summary>
        public async Task<AgentPoolEntity> SingleByNameAsync(string name)
        {
            await using var db = CreateDbContext();
            return await db.AgentPool.AsNoTracking().SingleAsync(x => x.Name == name);
        }

        public ValueTask DisposeAsync()
        {
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
