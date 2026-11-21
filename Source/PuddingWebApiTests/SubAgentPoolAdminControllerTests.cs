using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingHost.Controllers;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingWebApiTests;

/// <summary>
/// <see cref="SubAgentPoolAdminController"/> 的契约测试：这是前端子代理视图唯一的读出口，
/// 因此三件事必须被锁死。
/// <para>
/// ① <b>空池是 200 + 空数组，不是 404</b>：界面对「没有条目」要显示空状态与创建引导，
/// 对「端点不存在」要报部署故障，两者不能长得一样。用 404 表达空列表会把故障伪装成空状态。
/// </para>
/// <para>
/// ② <b>字段映射逐列正确</b>：本端点不做任何计算，它的全部价值就是「库里的值原样到前端」，
/// 因此每一列都单独断言，而不是只看条数。
/// </para>
/// <para>
/// ③ <b>只读 + 管理态鉴权</b>：本切片不实现任何写操作，也不能被匿名调用；
/// 这两点由特性反射断言锁住，避免后续切片无意间放宽。
/// </para>
/// <para>
/// 这里直接构造控制器而不是起整套 <c>WebApplicationFactory</c>：被测的是控制器契约，
/// 起整个宿主会把 SQLite 内存库、后台 worker、鉴权中间件全部拖进来，
/// 失败时很难判断到底是哪一层坏了。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentPoolAdminControllerTests
{
    [TestMethod]
    public async Task Get_EmptyPool_Returns200WithEmptyArray_Not404()
    {
        await using var scope = await CreateDatabaseAsync();
        var controller = new SubAgentPoolAdminController(scope.Factory);

        var result = await controller.Get(CancellationToken.None);

        // 关键断言：必须是 Ok（200），而不是 NotFound。空列表 ≠ 端点不存在。
        Assert.IsInstanceOfType<OkObjectResult>(
            result.Result,
            "空池必须返回 200 + []；用 404 表达空列表会让界面把「没有条目」误判成「端点不存在」");

        var payload = ((OkObjectResult)result.Result!).Value as IReadOnlyList<SubAgentPoolEntryDto>;
        Assert.IsNotNull(payload, "响应体必须是池条目数组");
        Assert.AreEqual(0, payload!.Count, "空池必须返回空数组");
    }

    [TestMethod]
    public async Task Get_ReturnsEntries_MapsEveryField_ExcludesDeleted_AndOrdersByLastUsedDesc()
    {
        await using var scope = await CreateDatabaseAsync();

        var recent = new AgentPoolEntity
        {
            PoolId = "pool-recent",
            OwnerWorkspaceId = "default",
            Name = "frontend-ui",
            SubSessionId = "sub-recent",
            TemplateId = "global:frontend",
            Role = "coding",
            ModelId = "deepseek/deepseek-flash",
            AvatarKey = "avatar-fox",
            Responsibility = "负责前端组件与样式",
            OwnerSessionId = "owner-session-1",
            OwnerAgentId = "owner-agent-1",
            Status = AgentPoolEntity.StatusIdle,
            TaskCount = 7,
            LastSuccess = true,
            CreatedAt = "2026-11-01T00:00:00Z",
            LastUsedAt = "2026-11-20T10:00:00Z",
            Version = 3,
        };
        var neverUsed = new AgentPoolEntity
        {
            PoolId = "pool-never-used",
            OwnerWorkspaceId = "default",
            Name = "research",
            SubSessionId = "sub-never-used",
            TemplateId = "global:research",
            Status = AgentPoolEntity.StatusSleeping,
            TaskCount = 0,
            LastSuccess = null,
            CreatedAt = "2026-11-02T00:00:00Z",
            LastUsedAt = string.Empty,
        };
        var deleted = new AgentPoolEntity
        {
            PoolId = "pool-deleted",
            OwnerWorkspaceId = "default",
            Name = "retired",
            SubSessionId = "sub-deleted",
            TemplateId = "global:general",
            Status = AgentPoolEntity.StatusDead,
            CreatedAt = "2026-10-01T00:00:00Z",
            LastUsedAt = "2026-11-21T09:00:00Z",
            DeletedAt = "2026-11-21T09:30:00Z",
        };

        await using (var seed = scope.Factory.CreateDbContext())
        {
            seed.AgentPool.AddRange(recent, neverUsed, deleted);
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory);
        var result = await controller.Get(CancellationToken.None);

        var payload = ((OkObjectResult)result.Result!).Value as IReadOnlyList<SubAgentPoolEntryDto>;
        Assert.IsNotNull(payload);

        // 软删的墓碑行不返回；剩下两条按 last_used_at DESC 排序，空串排最后。
        Assert.AreEqual(2, payload!.Count, "已软删（deleted_at 非空）的条目不得出现在列表里");

        var first = payload[0];
        Assert.AreEqual("pool-recent", first.PoolId);
        Assert.AreEqual("frontend-ui", first.Name);
        Assert.AreEqual("coding", first.Role);
        Assert.AreEqual("负责前端组件与样式", first.Responsibility);
        Assert.AreEqual("avatar-fox", first.AvatarKey);
        Assert.AreEqual("deepseek/deepseek-flash", first.ModelId);
        Assert.AreEqual("global:frontend", first.TemplateId);
        Assert.AreEqual(AgentPoolEntity.StatusIdle, first.Status);
        Assert.AreEqual(7, first.TaskCount);
        Assert.AreEqual("2026-11-20T10:00:00Z", first.LastUsedAt);
        Assert.AreEqual(true, first.LastSuccess);
        Assert.AreEqual("sub-recent", first.SubSessionId);
        Assert.AreEqual("2026-11-01T00:00:00Z", first.CreatedAt);

        var second = payload[1];
        Assert.AreEqual("pool-never-used", second.PoolId, "last_used_at 为空（空串）的条目必须排在有值之后");
        Assert.IsNull(second.LastSuccess, "last_success 的三态必须原样透出：NULL = 从未执行过");
        Assert.IsNull(second.Role);
        Assert.IsNull(second.Responsibility);
        Assert.IsNull(second.AvatarKey);
        Assert.IsNull(second.ModelId);
        Assert.AreEqual(string.Empty, second.LastUsedAt);
    }

    [TestMethod]
    public void Controller_IsReadOnly_AndRequiresAdminJwt()
    {
        var type = typeof(SubAgentPoolAdminController);

        var route = type.GetCustomAttribute<RouteAttribute>();
        Assert.IsNotNull(route, "控制器必须有显式路由");
        Assert.AreEqual("api/admin/sub-agent-pool", route!.Template);

        var authorize = type.GetCustomAttribute<AuthorizeAttribute>();
        Assert.IsNotNull(authorize, "池列表是运维面数据，必须要求登录态 Admin JWT");
        Assert.AreEqual("admin", authorize!.Roles);
        Assert.AreEqual("Bearer", authorize.AuthenticationSchemes);

        // 只读切片：任何写 HTTP 动词出现在本控制器上都说明越界了。
        var writeVerbs = new[]
        {
            typeof(HttpPostAttribute),
            typeof(HttpPutAttribute),
            typeof(HttpPatchAttribute),
            typeof(HttpDeleteAttribute),
        };
        var writeActions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => writeVerbs.Contains(a.GetType())))
            .Select(m => m.Name)
            .ToList();
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            writeActions,
            $"本切片只读，不得出现写动作；实际: [{string.Join(", ", writeActions)}]");
    }

    private static async Task<TestDatabaseScope> CreateDatabaseAsync()
    {
        // 单连接内存库：整个用例期间同一个库，控制器与夹具各用独立 context。
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestDatabaseScope(connection, new TestDbContextFactory(options));
    }

    /// <summary>只负责产出 context 的工厂：控制器需要短生命周期的 context，而不是单例。</summary>
    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TestDatabaseScope(
        SqliteConnection connection,
        IDbContextFactory<PlatformDbContext> factory) : IAsyncDisposable
    {
        public IDbContextFactory<PlatformDbContext> Factory { get; } = factory;

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
