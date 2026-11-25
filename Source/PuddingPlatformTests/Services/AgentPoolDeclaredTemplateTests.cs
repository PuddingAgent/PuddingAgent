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
/// 「条目声明的模板」在派发时以 <b>库</b> 为准，不以启动快照为准。
/// <para>
/// <b>缺陷来源：</b><c>GetDeclaredTemplateIdAsync</c> 曾经直接返回
/// <see cref="PooledSubAgent.TemplateId"/>（启动时 <c>LoadAllAsync</c> 的一次性投影），
/// 理由写在注释里：「面板 PATCH 只能改 role / model_id / responsibility / avatar_key，
/// 没有 template 字段，所以模板列在条目生命周期内不变」。
/// </para>
/// <para>
/// 那句话在当时是正确的，但它是一个<b>跨文件假设</b>：一端是
/// <c>SubAgentPoolAdminController.Update</c> 的字段白名单，一端是池内缓存，
/// 两者之间没有任何编译期或测试约束。模板一旦进了白名单，派发会继续用旧值，
/// 而结果里看不出差异——角色已经为这种形状付过一次代价
/// （见 <see cref="AgentPoolDeclaredRoleTests"/>）。所以这里用真实 SQLite 把
/// 「库改了、派发必须看到新值」钉住。
/// </para>
/// <para>
/// <b>为什么用真实 SQLite：</b>缺陷形状的关键一步是「写库不经过池」，必须由 EF 真的写进去；
/// fake store 只能测「我让它返回什么它就返回什么」，测不到池有没有去读库。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolDeclaredTemplateTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════
    // 1. 核心：库里改了模板，同进程下一单必须读到新值
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredTemplate_ReadsDatabase_NotStartupSnapshot()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", template: "workspace-task-agent"),
            0,
            default);

        var pool = CreatePool(db.Store);
        await pool.EnsureReadyAsync();

        // 前置条件：内存快照此刻就是建条目时那一份。若这里不成立，
        // 后面的断言会因为「快照本来就是新值」而假绿。
        Assert.AreEqual(
            "workspace-task-agent",
            pool.List().Single(e => e.Name == "agent-a").TemplateId,
            "前置条件：池加载时应带回创建时的模板");

        // 模拟「写库不经过池」：模板列被直接改掉（今天没有 PATCH 入口，
        // 但这正是将来加上入口后的形状，也是运维手工订正的形状）。
        await db.UpdateTemplateAsync("agent-a", "code-agent");

        var inherited = await pool.GetDeclaredTemplateIdAsync("agent-a");

        Console.WriteLine($"[evidence] inherited template={inherited ?? "<null>"}");
        Assert.AreEqual(
            "code-agent",
            inherited,
            "派发必须以库里的模板为准；沿用启动快照会让「改了模板」在本次进程内无效");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 读库失败不阻断派活：退回快照，而不是抛
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredTemplate_FallsBackToSnapshot_WhenStoreReadFails()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", template: "code-agent"), 0, default);

        var pool = CreatePool(db.Store);
        await pool.EnsureReadyAsync();

        // 就绪之后再注入故障：加载本身必须已经成功，否则测的是「池没加载」。
        db.Factory.Fail = true;

        var inherited = await pool.GetDeclaredTemplateIdAsync("agent-a");

        Assert.AreEqual(
            "code-agent",
            inherited,
            "读库失败时退回快照：继承是增强，不该把库抖动升级成派活失败");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 库里已无该行（软删）：同样退回快照，由 ExecuteAsync 统一拒绝
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredTemplate_FallsBackToSnapshot_WhenRowSoftDeleted()
    {
        await using var db = await PoolDb.CreateAsync();
        await db.Store.TryInsertAsync(
            InsertRequest("pool-a", "agent-a", "sub-1111aaaa", template: "code-agent"), 0, default);

        var pool = CreatePool(db.Store);
        await pool.EnsureReadyAsync();
        await db.SoftDeleteAsync("agent-a");

        var inherited = await pool.GetDeclaredTemplateIdAsync("agent-a");

        Assert.AreEqual(
            "code-agent",
            inherited,
            "库行已软删时退回快照：条目是否可派由 ExecuteAsync 的抢占谓词判定，不在这里猜");
    }

    // ═══════════════════════════════════════════════════════
    // 夹具
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
        string template) => new(
            PoolId: poolId,
            Name: name,
            SubSessionId: subSessionId,
            TemplateId: template,
            Role: null,
            OwnerWorkspaceId: "default",
            OwnerSessionId: "parent-session",
            OwnerAgentId: "agent-1",
            CreatedAt: BaseTime);

    /// <summary>真实临时 SQLite + 真实 store（与角色/模型继承用例同一套建表路径）。</summary>
    private sealed class PoolDb : IAsyncDisposable
    {
        private PoolDb(string directory, ToggleDbContextFactory factory)
        {
            Directory = directory;
            Factory = factory;
            Store = new AgentPoolEntryStore(factory, NullLogger<AgentPoolEntryStore>.Instance);
        }

        public string Directory { get; }

        public ToggleDbContextFactory Factory { get; }

        public IAgentPoolEntryStore Store { get; }

        /// <summary>直接改 <c>agent_pool.template_id</c>，刻意<b>不经过池</b>。</summary>
        public async Task UpdateTemplateAsync(string name, string template)
        {
            await using var db = new PlatformDbContext(Factory.Options);
            var entity = await db.AgentPool.SingleAsync(e => e.Name == name);
            entity.TemplateId = template;
            await db.SaveChangesAsync();
        }

        /// <summary>软删：让 <c>GetByIdAsync</c> 的 <c>DeletedAt == null</c> 谓词查不到该行。</summary>
        public async Task SoftDeleteAsync(string name)
        {
            await using var db = new PlatformDbContext(Factory.Options);
            var entity = await db.AgentPool.SingleAsync(e => e.Name == name);
            entity.DeletedAt = BaseTime.ToString("O");
            await db.SaveChangesAsync();
        }

        public static async Task<PoolDb> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-declared-template-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "platform.db")}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(db);
            }

            return new PoolDb(directory, new ToggleDbContextFactory(options));
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

    /// <summary>可注入读故障的工厂：<see cref="Fail"/> 打开后每次取上下文都会抛。</summary>
    private sealed class ToggleDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public DbContextOptions<PlatformDbContext> Options { get; } = options;

        public bool Fail { get; set; }

        public PlatformDbContext CreateDbContext()
        {
            if (Fail)
                throw new InvalidOperationException("injected store read failure");

            return new PlatformDbContext(Options);
        }

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
