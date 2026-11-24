using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PuddingCode.Abstractions;
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
        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

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

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
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
    public void Controller_RoutesAreWhitelisted_AndRequiresAdminJwt()
    {
        var type = typeof(SubAgentPoolAdminController);

        var route = type.GetCustomAttribute<RouteAttribute>();
        Assert.IsNotNull(route, "控制器必须有显式路由");
        Assert.AreEqual("api/admin/sub-agent-pool", route!.Template);

        var authorize = type.GetCustomAttribute<AuthorizeAttribute>();
        Assert.IsNotNull(authorize, "池列表是运维面数据，必须要求登录态 Admin JWT");
        Assert.AreEqual("admin", authorize!.Roles);
        Assert.AreEqual("Bearer", authorize.AuthenticationSchemes);

        // 读不限：GET 有几个都不管（读接口没有副作用，锁死它们只会碍事）。
        // 写动作精确匹配：每新增一个写动作必须同时往这张表加一行 ——
        // 否则「临时调试时加的删除接口忘了删」这类事就没人拦得住。
        var writeRoutes = new HashSet<string>
        {
            "DELETE|{name}",
            "POST|{name}/sleep",
            "PATCH|{name}",
        };

        var actualWrites = new List<string>();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => !m.IsSpecialName))
        {
            var verb = method.GetCustomAttributes()
                .Select(a => a switch
                {
                    HttpGetAttribute g => ((string?)"GET", (string?)g.Template),
                    HttpPostAttribute p => ((string?)"POST", (string?)p.Template),
                    HttpPutAttribute u => ((string?)"PUT", (string?)u.Template),
                    HttpPatchAttribute pa => ((string?)"PATCH", (string?)pa.Template),
                    HttpDeleteAttribute d => ((string?)"DELETE", (string?)d.Template),
                    _ => ((string?)null, (string?)null),
                })
                .FirstOrDefault(t => t.Item1 is not null);

            Assert.IsNotNull(verb.Item1, $"动作 {method.Name} 必须显式声明 HTTP 动词");

            if (verb.Item1 == "GET")
            {
                continue;
            }

            actualWrites.Add($"{verb.Item1}|{verb.Item2}");
        }

        CollectionAssert.AreEquivalent(
            writeRoutes.ToList(),
            actualWrites,
            $"写动作路由集合必须与白名单完全一致（多一个或漏一个都算越界）；实际: [{string.Join("; ", actualWrites)}]");
    }

    [TestMethod]
    public async Task Update_ChangesDisplayFields_Returns204()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-edit",
                OwnerWorkspaceId = "default",
                Name = "frontend-ui",
                SubSessionId = "sub-edit",
                TemplateId = "global:task",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.Update(
            "frontend-ui",
            new UpdateSubAgentPoolEntryRequest { Responsibility = "新的备注" },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "frontend-ui");
        Assert.AreEqual("新的备注", saved.Responsibility);
    }

    [TestMethod]
    public async Task Update_DoesNotClearFieldThatWasNotProvided()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-keep",
                OwnerWorkspaceId = "default",
                Name = "keeper",
                SubSessionId = "sub-keep",
                TemplateId = "global:task",
                AvatarKey = "avatar-fox",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        // 只改备注，不传 avatarKey —— 头像必须保持不变，不能被 null 当成「清空」。
        var result = await controller.Update(
            "keeper",
            new UpdateSubAgentPoolEntryRequest { Responsibility = "只改备注" },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "keeper");
        Assert.AreEqual("只改备注", saved.Responsibility);
        Assert.AreEqual("avatar-fox", saved.AvatarKey, "没传的字段不得被清空");
    }

    [TestMethod]
    public async Task Update_MissingEntry_Returns404()
    {
        await using var scope = await CreateDatabaseAsync();
        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

        var result = await controller.Update(
            "ghost",
            new UpdateSubAgentPoolEntryRequest { Responsibility = "x" },
            CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
    }

    [TestMethod]
    public async Task Update_OverlongResponsibility_Returns400()
    {
        await using var scope = await CreateDatabaseAsync();
        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

        var result = await controller.Update(
            "frontend-ui",
            new UpdateSubAgentPoolEntryRequest { Responsibility = new string('长', 201) },
            CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public async Task Update_SetsModelRoute_Returns204()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-model",
                OwnerWorkspaceId = "default",
                Name = "advisor",
                SubSessionId = "sub-model",
                TemplateId = "global:task",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.Update(
            "advisor",
            new UpdateSubAgentPoolEntryRequest { ModelId = "fastrouter-claude/claude-opus-5-5" },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "advisor");
        Assert.AreEqual("fastrouter-claude/claude-opus-5-5", saved.ModelId);
    }

    [TestMethod]
    public async Task Update_EmptyModelId_ClearsToNull_NotEmptyString()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-clear",
                OwnerWorkspaceId = "default",
                Name = "scout",
                SubSessionId = "sub-clear",
                TemplateId = "global:task",
                ModelId = "deepseek/deepseek-flash",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.Update(
            "scout",
            new UpdateSubAgentPoolEntryRequest { ModelId = string.Empty },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "scout");
        // 「跟随派发方」在库里只能有一种表示：空串会被读回当成「设了一个空路由」，
        // 于是同一个语义出现两种存储写法，读侧就必须两套判定。这里锁死 NULL。
        Assert.IsNull(saved.ModelId, "空串必须归一化成 NULL，不得落成空字符串");
    }

    [TestMethod]
    public async Task Update_ModelIdWithoutProviderSlash_Returns400()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-bad",
                OwnerWorkspaceId = "default",
                Name = "reviewer",
                SubSessionId = "sub-bad",
                TemplateId = "global:task",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

        // 裸 modelId 在多个 provider 注册同名模型时会解析失败（实测过），
        // 所以入口就拒掉，不要让它存进去到派发时才炸。
        foreach (var bad in new[] { "deepseek-flash", "/x", "a/", "a/b/c" })
        {
            var result = await controller.Update(
                "reviewer",
                new UpdateSubAgentPoolEntryRequest { ModelId = bad },
                CancellationToken.None);

            Assert.IsInstanceOfType<BadRequestObjectResult>(result, $"'{bad}' 应被拒绝");
        }

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "reviewer");
        Assert.IsNull(saved.ModelId, "被拒的请求不得写入任何值");
    }

    [TestMethod]
    public async Task Update_OmittedModelId_DoesNotClearExisting()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-keep-model",
                OwnerWorkspaceId = "default",
                Name = "keeper-model",
                SubSessionId = "sub-keep-model",
                TemplateId = "global:task",
                ModelId = "deepseek/deepseek-flash",
                AvatarKey = "avatar-fox",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.Update(
            "keeper-model",
            new UpdateSubAgentPoolEntryRequest { Responsibility = "只改备注" },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "keeper-model");
        Assert.AreEqual(
            "deepseek/deepseek-flash", saved.ModelId, "没传 modelId 不得被清空");
        Assert.AreEqual("avatar-fox", saved.AvatarKey);
    }

    [TestMethod]
    public async Task Delete_WhenPoolDestroys_Returns204_AndForwardsName()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)Array.Empty<PooledSubAgent>());
        pool.Setup(p => p.DestroyAsync("frontend-ui", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Delete("frontend-ui", CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result, "销毁成功必须返回 204，不返回被删条目的正文");
        pool.Verify(
            p => p.DestroyAsync("frontend-ui", It.IsAny<CancellationToken>()),
            Times.Once,
            "必须把列表里的 name 原样传给池，不得在控制器里改写它");
    }

    [TestMethod]
    public async Task Delete_WhenPoolRejects_Returns409_NotSilentSuccess()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)Array.Empty<PooledSubAgent>());
        pool.Setup(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Delete("ghost", CancellationToken.None);

        var conflict = result as ObjectResult;
        Assert.IsNotNull(conflict, "池拒绝时必须给出可见的冲突响应，不得静默当作已删除");
        Assert.AreEqual(StatusCodes.Status409Conflict, conflict!.StatusCode);
    }

    [TestMethod]
    public async Task Delete_WhenPoolNotReady_Returns503_NotConflict()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PoolNotReadyException("load failed", PoolReadiness.Failed));
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Delete("frontend-ui", CancellationToken.None);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(
            StatusCodes.Status503ServiceUnavailable,
            status!.StatusCode,
            "池未就绪是「暂时不可用」，不能报成 409 —— 后者会让人去查条目归属，方向就错了");
        pool.Verify(
            p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "未就绪时不得尝试销毁：内存里看不到条目时 DestroyAsync 会误报 false");
    }

    [TestMethod]
    public async Task Delete_BlankName_Returns400_AndDoesNotTouchPool()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Delete("   ", CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        pool.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Sleep_WhenPoolSleeps_Returns204_AndForwardsName()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)Array.Empty<PooledSubAgent>());
        pool.Setup(p => p.SleepAsync("frontend-ui", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Sleep("frontend-ui", CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result, "休眠成功必须返回 204，与销毁同语义同码");
        pool.Verify(
            p => p.SleepAsync("frontend-ui", It.IsAny<CancellationToken>()),
            Times.Once,
            "休眠必须把列表里的 name 原样传给池");
    }

    [TestMethod]
    public async Task Sleep_WhenPoolRejects_Returns409_NotSilentSuccess()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)Array.Empty<PooledSubAgent>());
        pool.Setup(p => p.SleepAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Sleep("ghost", CancellationToken.None);

        var conflict = result as ObjectResult;
        Assert.IsNotNull(conflict, "池拒绝时必须给出可见的冲突响应，不得静默当作已休眠");
        Assert.AreEqual(StatusCodes.Status409Conflict, conflict!.StatusCode);
    }

    [TestMethod]
    public async Task Sleep_WhenPoolNotReady_Returns503_AndDoesNotCallSleep()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PoolNotReadyException("load failed", PoolReadiness.Failed));
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Sleep("frontend-ui", CancellationToken.None);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(
            StatusCodes.Status503ServiceUnavailable,
            status!.StatusCode,
            "池未就绪是「暂时不可用」，不能报成 409");
        pool.Verify(
            p => p.SleepAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "未就绪时不得尝试休眠：内存里看不到条目时 SleepAsync 会误报 false");
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
