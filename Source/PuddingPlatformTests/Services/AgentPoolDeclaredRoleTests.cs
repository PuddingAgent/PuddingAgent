using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 「条目声明的角色」在派发时以 <b>库</b> 为准，不以启动快照为准。
/// <para>
/// <b>缺陷来源（外部审计 P2，已复现）：</b>管理面板改角色走
/// <c>SubAgentPoolAdminController.Update</c> 直接写库，而池内
/// <see cref="PooledSubAgent.Role"/> 是启动时 <c>LoadAllAsync</c> 的一次性投影。
/// 派发只读内存 → 把 <c>worker</c> 改成 <c>reviewer</c> 后<b>同进程的下一单仍收到 worker</b>，
/// 要等重启重载池才生效。角色不是展示字段：它决定子代理的提示词与职责，
/// 读旧值就是用错人干活，而结果里看不出差异。
/// </para>
/// <para>
/// <b>为什么用真实 SQLite 而不是 fake store：</b>复现路径的关键一步是「面板改库、内存不变」，
/// 那必须由 EF 真的写进去才算数。用 fake store 只能测「我让它返回什么它就返回什么」，
/// 测不到<b>池有没有去读库</b>这件事本身。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolDeclaredRoleTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════
    // 1. 核心缺陷：面板改角色后，同进程下一单必须继承新值
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_InheritsRoleFromDatabase_AfterPanelChangesIt()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, db.Store);
        await pool.EnsureReadyAsync();

        // 池此刻的缓存角色来自 LoadAllAsync。
        Assert.AreEqual("worker", pool.List().Single(e => e.Name == "agent-a").Role,
            "前置条件：池加载时应带回创建时的角色");

        // 管理面板改角色 = 直接 UPDATE agent_pool.role，不经过池（这正是缺陷的形状）。
        await db.UpdateRoleAsync("agent-a", "reviewer");

        await pool.ExecuteAsync("agent-a", CreateSpawnRequest(), default);

        Console.WriteLine($"[evidence] dispatched role={manager.LastExecuteRequest?.RoleInPlan ?? "<null>"}");
        Assert.AreEqual("reviewer", manager.LastExecuteRequest?.RoleInPlan,
            "派发必须以库里的声明角色为准；沿用启动快照会让「面板改角色」在本次进程内无效");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 刷新同时回写缓存：Agent 侧的池清单也不该继续显示旧角色
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_RefreshesInMemorySnapshot_SoPoolListSeesNewRole()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var pool = CreatePool(new TestSubAgentManager(), db.Store);
        await pool.EnsureReadyAsync();
        await db.UpdateRoleAsync("agent-a", "reviewer");

        await pool.ExecuteAsync("agent-a", CreateSpawnRequest(), default);

        var cached = pool.List().Single(e => e.Name == "agent-a").Role;
        Console.WriteLine($"[evidence] cached after dispatch={cached ?? "<null>"}");
        Assert.AreEqual("reviewer", cached,
            "库里已经是 reviewer 了，内存快照还报 worker 就是「缓存冒充真源」");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 正向对照：显式指定角色本来就更优先，且不该被库覆盖
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_ExplicitRole_WinsOverDeclaredRole()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, db.Store);
        await pool.EnsureReadyAsync();
        await db.UpdateRoleAsync("agent-a", "reviewer");

        await pool.ExecuteAsync(
            "agent-a",
            CreateSpawnRequest() with { RoleInPlan = "critic" },
            default);

        Assert.AreEqual("critic", manager.LastExecuteRequest?.RoleInPlan,
            "本次显式传入的角色是一次性覆盖，不得被成员声明值盖掉");
    }

    // ═══════════════════════════════════════════════════════
    // 4. 读库失败 → 沿用缓存角色，不把库抖动升级成派活失败
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_FallsBackToCachedRole_WhenStoreReadFails()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager, new ReadFailingStore(db.Store));
        await pool.EnsureReadyAsync();

        var result = await pool.ExecuteAsync("agent-a", CreateSpawnRequest(), default);

        Assert.IsTrue(result.Success, "一次读角色失败不该让派活失败");
        Assert.AreEqual("worker", manager.LastExecuteRequest?.RoleInPlan,
            "读不到库就沿用缓存值：旧角色比「派活失败」代价小得多");
    }

    // ═══════════════════════════════════════════════════════
    // 5. 纯内存模式：没有第二个真源，缓存就是唯一真相
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Execute_InMemoryMode_InheritsCreationRole()
    {
        var manager = new TestSubAgentManager();
        var pool = new SubAgentPool(
            manager,
            new ConfigurationBuilder().Build(),
            NullLogger<SubAgentPool>.Instance);

        await pool.CreateAsync("agent-a", CreateSpawnRequest() with { RoleInPlan = "worker" });
        await pool.ExecuteAsync("agent-a", CreateSpawnRequest(), default);

        Assert.AreEqual("worker", manager.LastExecuteRequest?.RoleInPlan,
            "没有持久化列时缓存就是唯一真相，继承不能变成「一律空角色」");
    }

    // ═══════════════════════════════════════════════════════
    // 6. 生效角色必须回传：调用方从自己的入参看不出「继承了」
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 派活未指定角色时，结果里必须带上成员声明的角色。
    /// <para>
    /// 继承发生在池内部（<c>executeRequest</c> 构造处），调用方传空与传 null 都会落到继承，
    /// 所以它<b>无法</b>从自己的入参判断这一单用了谁。不回传的后果是「被当成 worker 干活，
    /// 而结果里看不出差异」——角色决定提示词与职责，不是展示字段。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Execute_ReturnsEffectiveRole_WhenInheritedFromMember()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var pool = CreatePool(new TestSubAgentManager(), db.Store);
        await pool.EnsureReadyAsync();

        var result = await pool.ExecuteAsync("agent-a", CreateSpawnRequest(), default);

        Console.WriteLine($"[evidence] result.RoleInPlan={result.RoleInPlan ?? "<null>"}");
        Assert.AreEqual("worker", result.RoleInPlan,
            "未指定角色时生效值来自成员声明，必须随结果回传，否则调用方无从确认用了谁");
    }

    /// <summary>
    /// 正向对照：显式指定的角色必须原样回传，不能被成员声明值替换。
    /// <para>没有这一条，「恒返回成员角色」的实现也能通过上一个用例。</para>
    /// </summary>
    [TestMethod]
    public async Task Execute_ReturnsExplicitRole_WhenCallerOverrides()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var pool = CreatePool(new TestSubAgentManager(), db.Store);
        await pool.EnsureReadyAsync();

        var result = await pool.ExecuteAsync(
            "agent-a",
            CreateSpawnRequest() with { RoleInPlan = "critic" },
            default);

        Assert.AreEqual("critic", result.RoleInPlan,
            "显式角色是一次性覆盖，回传的必须是生效的那个值而不是成员声明值");
    }

    /// <summary>
    /// 两边都没有角色时结果为 null，而不是空串。
    /// <para>
    /// 空串会被下游当成「有一个角色，只是空」；库里「没设」只有 null 一种表示，这里必须同口径。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Execute_ReturnsNullRole_WhenNeitherSideDeclaresIt()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: null), 0, default);

        var pool = CreatePool(new TestSubAgentManager(), db.Store);
        await pool.EnsureReadyAsync();

        var result = await pool.ExecuteAsync("agent-a", CreateSpawnRequest(), default);

        Assert.IsNull(result.RoleInPlan,
            "没有角色就该是 null；空串会让「没设」出现两种表示");
    }

    // ═══════════════════════════════════════════════════════
    // 7. 列表面也以库为准：一次派发都没有时，三个面也不许各说一套
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 面板改完角色、**还没发生任何派发**时，Agent 侧列出来的角色必须是新值。
    /// <para>
    /// 这是三个面里的第三个：库是 <c>reviewer</c>、下一次派发也会继承 <c>reviewer</c>，
    /// 唯独 Agent 自己 list 出来还是 <c>worker</c>。只刷新派发路径时，这条缺陷恰好只在
    /// 「改完角色到下一次派发之间」可见——而那正是用户去看面板/让 Agent 确认「改成功了没有」的时刻。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ListOwnedByDeclared_ReflectsPanelChange_WithoutAnyDispatch()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var pool = CreatePool(new TestSubAgentManager(), db.Store);
        await pool.EnsureReadyAsync();
        await db.UpdateRoleAsync("agent-a", "reviewer");

        var agents = await pool.ListOwnedByDeclaredAsync(
            new PoolOwnerScope("parent-session", "default"), default);

        Console.WriteLine($"[evidence] listed role={agents.Single().Role ?? "<null>"}");
        Assert.AreEqual("reviewer", agents.Single().Role,
            "列表面以库为准：一次派发都没有时也必须看到面板刚改的值");
    }

    /// <summary>
    /// 读库失败时退回缓存角色，既不变空表也不抛异常。
    /// <para>
    /// 失败点刻意放在「就绪之后」：<c>EnsureReadyAsync</c> 本身也走 <c>LoadAllAsync</c>，
    /// 一开始就让它失败测到的是「池起不来」，不是「刷新失败」。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ListOwnedByDeclared_FallsBackToCache_WhenStoreReadFails()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var store = new ReadFailingStore(db.Store);
        var pool = CreatePool(new TestSubAgentManager(), store);
        await pool.EnsureReadyAsync();

        store.FailLoadAll = true;
        var agents = await pool.ListOwnedByDeclaredAsync(
            new PoolOwnerScope("parent-session", "default"), default);

        Assert.AreEqual(1, agents.Count, "一次读库失败不该把列表面变成空表或异常");
        Assert.AreEqual("worker", agents.Single().Role, "读不到库就沿用缓存值");
    }

    /// <summary>
    /// 刷新声明列不得顺手放宽归属过滤。
    /// <para>
    /// 正向对照与负向放在同一个用例里：只断言「别人的看不见」时，一个恒返回空表的实现也能过。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ListOwnedByDeclared_KeepsScopeFiltering()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", role: "worker"), 0, default);

        var pool = CreatePool(new TestSubAgentManager(), db.Store);
        await pool.EnsureReadyAsync();

        var mine = await pool.ListOwnedByDeclaredAsync(
            new PoolOwnerScope("parent-session", "default"), default);
        var stranger = await pool.ListOwnedByDeclaredAsync(
            new PoolOwnerScope("another-session", "default"), default);

        Assert.AreEqual(1, mine.Count, "归属相符时必须能看到自己的成员");
        Assert.AreEqual(0, stranger.Count, "刷新声明列不得顺手放宽归属过滤");
    }

    // ═══════════════════════════════════════════════════════
    // 脚手架
    // ═══════════════════════════════════════════════════════

    private static SubAgentPool CreatePool(TestSubAgentManager manager, IAgentPoolEntryStore store) =>
        new(manager,
            new ConfigurationBuilder().Build(),
            NullLogger<SubAgentPool>.Instance,
            store);

    private static AgentPoolInsertRequest InsertRequest(
        string poolId,
        string name,
        string subSessionId,
        string? role) => new(
            PoolId: poolId,
            Name: name,
            SubSessionId: subSessionId,
            TemplateId: "workspace-task-agent",
            Role: role,
            OwnerWorkspaceId: "default",
            OwnerSessionId: "parent-session",
            OwnerAgentId: "agent-1",
            CreatedAt: BaseTime);

    private static SubAgentSpawnRequest CreateSpawnRequest() => new()
    {
        ParentSessionId = "parent-session",
        WorkspaceId = "default",
        TaskDescription = "test task",
        LlmConfig = new()
        {
            ModelId = "test-model",
            KeyVaultId = "test-key",
            Endpoint = "https://test.example.com/v1",
        },
        LlmProfile = new()
        {
            ProviderId = "test-provider",
            ProfileId = "subagent.conscious",
            ModelId = "test-model",
            Role = "conscious",
        },
        TemplateId = "workspace-task-agent",
    };

    /// <summary>让 <c>GetByIdAsync</c>（总是）与 <c>LoadAllAsync</c>（按开关）抛错的装饰器，其余成员直接委派。</summary>
    private sealed class ReadFailingStore(IAgentPoolEntryStore inner) : IAgentPoolEntryStore
    {
        /// <summary>
        /// 置 <c>true</c> 后 <c>LoadAllAsync</c> 也抛错。
        /// <para>只能在就绪之后打开：就绪本身也走 <c>LoadAllAsync</c>，一开始就失败测到的是「池起不来」。</para>
        /// </summary>
        public bool FailLoadAll { get; set; }

        public Task<AgentPoolEntrySnapshot?> GetByIdAsync(string poolId, CancellationToken ct)
            => throw new InvalidOperationException("simulated metadata read failure");

        public Task<int> TryInsertAsync(AgentPoolInsertRequest request, int capacity, CancellationToken ct)
            => inner.TryInsertAsync(request, capacity, ct);

        public Task<int> TryAcquireAsync(
            string poolId,
            string ownerSessionId,
            string ownerWorkspaceId,
            string executionId,
            DateTimeOffset now,
            CancellationToken ct)
            => inner.TryAcquireAsync(poolId, ownerSessionId, ownerWorkspaceId, executionId, now, ct);

        public Task<int> TryInsertAndAcquireAsync(
            AgentPoolInsertRequest request,
            int capacity,
            string executionId,
            DateTimeOffset now,
            CancellationToken ct)
            => inner.TryInsertAndAcquireAsync(request, capacity, executionId, now, ct);

        public Task<int> TryCompleteAsync(
            string poolId,
            string subSessionId,
            string executionId,
            bool success,
            DateTimeOffset now,
            CancellationToken ct)
            => inner.TryCompleteAsync(poolId, subSessionId, executionId, success, now, ct);

        public Task<int> TrySetStatusAsync(
            string poolId,
            string expectedStatus,
            string newStatus,
            string? statusReason,
            bool clearExecution,
            DateTimeOffset now,
            CancellationToken ct,
            string? expectedOwnerSessionId = null,
            string? expectedOwnerWorkspaceId = null)
            => inner.TrySetStatusAsync(
                poolId, expectedStatus, newStatus, statusReason, clearExecution, now, ct,
                expectedOwnerSessionId, expectedOwnerWorkspaceId);

        public Task<string?> TryEvictOldestSleepingAsync(DateTimeOffset now, CancellationToken ct)
            => inner.TryEvictOldestSleepingAsync(now, ct);

        public Task<int> ResetStaleBusyAsync(DateTimeOffset now, CancellationToken ct)
            => inner.ResetStaleBusyAsync(now, ct);

        public Task<IReadOnlyList<AgentPoolEntrySnapshot>> LoadAllAsync(CancellationToken ct)
            => FailLoadAll
                ? throw new InvalidOperationException("simulated listing read failure")
                : inner.LoadAllAsync(ct);

        public Task<AgentPoolEntrySnapshot?> GetBySubSessionAsync(string subSessionId, CancellationToken ct)
            => inner.GetBySubSessionAsync(subSessionId, ct);
    }

    /// <summary>真实临时 SQLite + 真实 store（与 <c>AgentPoolDeclaredModelTests</c> 同一套建表路径）。</summary>
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

        /// <summary>
        /// 模拟管理面板的写库：直接改 <c>agent_pool.role</c>，刻意<b>不经过池</b>。
        /// 这正是缺陷的形状——两条写路径各写一边，派发只读其中一边。
        /// </summary>
        public async Task UpdateRoleAsync(string name, string? role)
        {
            await using var db = new PlatformDbContext(Options);
            var entity = await db.AgentPool.SingleAsync(e => e.Name == name);
            entity.Role = role;
            await db.SaveChangesAsync();
        }

        public static async Task<PoolDb> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-declared-role-" + Guid.NewGuid().ToString("N"));
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

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响断言结论。
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
