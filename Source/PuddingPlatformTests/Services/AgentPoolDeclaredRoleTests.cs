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

    /// <summary>让 <c>GetByIdAsync</c> 抛错的装饰器，其余成员直接委派。</summary>
    private sealed class ReadFailingStore(IAgentPoolEntryStore inner) : IAgentPoolEntryStore
    {
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
            => inner.LoadAllAsync(ct);

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
