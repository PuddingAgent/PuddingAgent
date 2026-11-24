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
/// 「条目声明的模型路由」这条链路（2026-11-24 / <c>1281c39</c>）：
/// <c>agent_pool.model_id</c> → 存储快照（<see cref="AgentPoolEntrySnapshot"/>）→ 池 →
/// 派活前的路由继承。
/// <para>
/// 为什么单独立一组用例：这一列原先<b>写完没有任何读者</b>——界面能设、PATCH 能写、
/// 列表能显示，但执行路径从不读它。所以「能读到」必须有测试锁住，
/// 否则下一次重构把某个字段删掉，也不会有任何红灯。
/// </para>
/// <para>
/// 刻意用真实 SQLite 而不是 fake store：<see cref="AgentPoolEntrySnapshot"/> 的
/// <c>ModelId</c> 是由 <c>Map()</c> 从 EF 实体带出来的，fake store 绕过 Map，
/// 就测不到「列真的被映射了」这件事本身。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolDeclaredModelTests
{
    private const string AdvisorRoute = "fastrouter/claude-opus-5-5";

    private static readonly DateTimeOffset BaseTime = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════
    // 1. 存储层：Map() 必须把 model_id 带进快照
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task LoadAll_BringsModelIdFromDatabaseColumn()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", AdvisorRoute), 0, default);

        var rows = await db.Store.LoadAllAsync(default);

        Assert.HasCount(1, rows);
        Console.WriteLine($"[evidence] loaded modelId={rows[0].ModelId ?? "<null>"}");
        Assert.AreEqual(AdvisorRoute, rows[0].ModelId,
            "Map() 必须把 agent_pool.model_id 带进快照——否则执行路径永远读不到声明模型");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 池：能按名字读回声明路由
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredModelId_ReturnsStoredRoute()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", AdvisorRoute), 0, default);

        var pool = CreatePool(db.Store);
        await pool.EnsureReadyAsync();

        var declared = await pool.GetDeclaredModelIdAsync("agent-a");

        Console.WriteLine($"[evidence] declared={declared ?? "<null>"}");
        Assert.AreEqual(AdvisorRoute, declared,
            "池派活要继承这个值；读不到就等于「界面上设 Claude、实际跑 deepseek」");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 未声明 → null（交给平台默认，不硬失败）
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredModelId_ReturnsNull_WhenEntryDeclaresNothing()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", modelId: null), 0, default);

        var pool = CreatePool(db.Store);
        await pool.EnsureReadyAsync();

        Assert.IsNull(await pool.GetDeclaredModelIdAsync("agent-a"),
            "没声明必须返回 null（走平台默认），不能编一个默认值假装是声明");
    }

    // ═══════════════════════════════════════════════════════
    // 4. 未知名字 → null，不抛
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredModelId_ReturnsNull_ForUnknownName()
    {
        await using var db = await PoolDb.CreateAsync();

        var pool = CreatePool(db.Store);
        await pool.EnsureReadyAsync();

        Assert.IsNull(await pool.GetDeclaredModelIdAsync("nobody"),
            "条目不存在返回 null：「没有声明」与「读不到」在调用方是同一个处置");
    }

    // ═══════════════════════════════════════════════════════
    // 5. 纯内存模式（无 store）→ null
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredModelId_ReturnsNull_InMemoryMode()
    {
        var pool = new SubAgentPool(
            new TestSubAgentManager(),
            new ConfigurationBuilder().Build(),
            NullLogger<SubAgentPool>.Instance);

        await pool.CreateAsync("agent-a", CreateSpawnRequest());

        Assert.IsNull(await pool.GetDeclaredModelIdAsync("agent-a"),
            "没有持久化列就没有可声明的模型：纯内存模式必须返回 null 而不是抛");
    }

    // ═══════════════════════════════════════════════════════
    // 脚手架
    // ═══════════════════════════════════════════════════════

    private static SubAgentPool CreatePool(IAgentPoolEntryStore store) =>
        new(new TestSubAgentManager(),
            new ConfigurationBuilder().Build(),
            NullLogger<SubAgentPool>.Instance,
            store);

    private static AgentPoolInsertRequest InsertRequest(
        string poolId,
        string name,
        string subSessionId,
        string? modelId) => new(
            PoolId: poolId,
            Name: name,
            SubSessionId: subSessionId,
            TemplateId: "workspace-task-agent",
            Role: "advisor",
            OwnerWorkspaceId: "default",
            OwnerSessionId: "parent-session",
            OwnerAgentId: "agent-1",
            CreatedAt: BaseTime,
            ModelId: modelId);

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

    /// <summary>真实临时 SQLite + 真实 store（与 <c>AgentPoolEntryStoreTests</c> 同一套建表路径）。</summary>
    private sealed class PoolDb : IAsyncDisposable
    {
        private PoolDb(string directory, DbContextOptions<PlatformDbContext> options)
        {
            Directory = directory;
            Store = new AgentPoolEntryStore(
                new TestDbContextFactory(options),
                NullLogger<AgentPoolEntryStore>.Instance);
        }

        public string Directory { get; }
        public IAgentPoolEntryStore Store { get; }

        public static async Task<PoolDb> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-declared-model-" + Guid.NewGuid().ToString("N"));
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
