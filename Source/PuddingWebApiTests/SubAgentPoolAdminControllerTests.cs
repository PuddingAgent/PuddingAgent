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

    /// <summary>
    /// 容量端点返回池上限，且<b>不碰就绪门</b>。
    /// <para>
    /// 第二条断言才是这条用例的价值所在：如果实现改成先 <c>GetSnapshotAsync</c> 再读容量，
    /// 那么 Core 刚启动、池未 Ready 时界面就看不到容量 —— 而「看不到上限」正是本端点要解决的问题。
    /// </para>
    /// </summary>
    [TestMethod]
    public void GetCapacity_ReturnsPoolMaxCapacity_WithoutTouchingReadinessGate()
    {
        var pool = new Mock<ISubAgentPool>();
        pool.SetupGet(p => p.MaxCapacity).Returns(23);
        var controller = new SubAgentPoolAdminController(
            Mock.Of<IDbContextFactory<PlatformDbContext>>(),
            pool.Object);

        var result = controller.GetCapacity();

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, "容量端点必须返回 200");
        var dto = ok!.Value as SubAgentPoolCapacityDto;
        Assert.IsNotNull(dto, "响应体必须是容量 DTO");
        Assert.AreEqual(23, dto!.Capacity, "容量必须原样透出池实现上报的上限");

        pool.Verify(
            p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "容量端点不得读快照：未就绪时它也要能回答，否则解决不了「看不到上限」这个问题");
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
        Assert.AreEqual("owner-session-1", first.OwnerSessionId, "归属会话必须透出：它决定哪个会话能驱动该条目，不是纯展示字段");
        Assert.AreEqual("2026-11-01T00:00:00Z", first.CreatedAt);

        var second = payload[1];
        Assert.AreEqual("pool-never-used", second.PoolId, "last_used_at 为空（空串）的条目必须排在有值之后");
        Assert.IsNull(second.LastSuccess, "last_success 的三态必须原样透出：NULL = 从未执行过");
        Assert.IsNull(second.Role);
        Assert.IsNull(second.Responsibility);
        Assert.IsNull(second.AvatarKey);
        Assert.IsNull(second.ModelId);
        Assert.AreEqual(string.Empty, second.LastUsedAt);
        Assert.IsNull(second.OwnerSessionId, "未记录归属时必须是 null，不得用空串或占位值冒充");
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
    public async Task Update_SetsRole_Returns204()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-role",
                OwnerWorkspaceId = "default",
                Name = "legacy",
                SubSessionId = "sub-role",
                TemplateId = "global:task",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.Update(
            "legacy",
            new UpdateSubAgentPoolEntryRequest { Role = "verifier" },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "legacy");
        Assert.AreEqual("verifier", saved.Role, "role 必须真的落库，而不是只通过校验");
    }

    [TestMethod]
    public async Task Update_EmptyRole_ClearsToNull_NotEmptyString()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-role-clear",
                OwnerWorkspaceId = "default",
                Name = "clearer",
                SubSessionId = "sub-role-clear",
                TemplateId = "global:task",
                Role = "old-role",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.Update(
            "clearer",
            new UpdateSubAgentPoolEntryRequest { Role = string.Empty },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "clearer");
        // 空串与 NULL 必须是同一件事，否则读回时要处理两种「都没设」。
        Assert.IsNull(saved.Role, "空字符串必须归一化成 NULL，不能存成空串");
    }

    [TestMethod]
    public async Task Update_OmittedRole_DoesNotClearExisting()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(new AgentPoolEntity
            {
                PoolId = "pool-role-keep",
                OwnerWorkspaceId = "default",
                Name = "rolekeeper",
                SubSessionId = "sub-role-keep",
                TemplateId = "global:task",
                Role = "scout",
                Status = AgentPoolEntity.StatusIdle,
                CreatedAt = "2026-11-01T00:00:00Z",
            });
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        // 只改备注，不传 role —— role 必须保持不变。
        var result = await controller.Update(
            "rolekeeper",
            new UpdateSubAgentPoolEntryRequest { Responsibility = "只改备注" },
            CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);

        await using var verify = await scope.Factory.CreateDbContextAsync();
        var saved = await verify.AgentPool.SingleAsync(e => e.Name == "rolekeeper");
        Assert.AreEqual("scout", saved.Role, "没传的 role 不得被清空");
    }

    [TestMethod]
    public async Task Update_OverlongRole_Returns400()
    {
        await using var scope = await CreateDatabaseAsync();
        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

        var result = await controller.Update(
            "frontend-ui",
            new UpdateSubAgentPoolEntryRequest { Role = new string('r', 65) },
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
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)new[] { PoolEntry("frontend-ui") });
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
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)new[] { PoolEntry("ghost") });
        pool.Setup(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Delete("ghost", CancellationToken.None);

        var conflict = result as ObjectResult;
        Assert.IsNotNull(conflict, "池拒绝时必须给出可见的冲突响应，不得静默当作已删除");
        Assert.AreEqual(StatusCodes.Status409Conflict, conflict!.StatusCode);
    }

    /// <summary>
    /// 快照里没有这个名字 ⇒ 404，且<b>不得</b>再去调 <c>DestroyAsync</c>。
    /// <para>
    /// 这是名字拼错与「存在但本进程删不掉」的分水岭：前者人只改一个字，后者要换进程操作。
    /// 若默认实现把两者都报成 409，用户会去查归属，而实际只是拼错了名字。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Delete_WhenNameMissingFromSnapshot_Returns404_AndDoesNotCallDestroy()
    {
        await using var scope = await CreateDatabaseAsync();
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)new[] { PoolEntry("frontend-ui") });
        var controller = new SubAgentPoolAdminController(scope.Factory, pool.Object);

        var result = await controller.Delete("frontnd-ui", CancellationToken.None);

        var notFound = result as ObjectResult;
        Assert.IsNotNull(notFound, "快照里没有该名字时必须给出可见响应");
        Assert.AreEqual(
            StatusCodes.Status404NotFound,
            notFound!.StatusCode,
            "库（真源）里没有这个条目就是 404；报成 409 会让人去查归属，而实际只是名字拼错");
        pool.Verify(
            p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "名字都不在快照里，不得再去尝试销毁（那只会得到又一个无法解释的 false）");
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

    [TestMethod]
    public async Task GetRuns_ReturnsOnlyThatEntriesRuns_OrderedNewestFirst()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.AddRange(
                PoolRow("pool-a", "alpha", "sub-alpha"),
                PoolRow("pool-b", "beta", "sub-beta"));
            seed.SubAgentRuns.AddRange(
                RunRow("run-a1", "sub-alpha", "completed", rounds: 3, tools: 7, durationMs: 1200),
                RunRow("run-a2", "sub-alpha", "failed", rounds: 1, tools: 0, durationMs: 300, error: "boom"),
                RunRow("run-b1", "sub-beta", "completed", rounds: 9, tools: 20, durationMs: 9000));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.GetRuns("alpha", 20, CancellationToken.None);

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var payload = ((OkObjectResult)result.Result!).Value as SubAgentRunsPageDto;
        Assert.IsNotNull(payload, "响应体必须是运行历史信封");

        // 归集键是 sub_session_id：另一条池条目的 run 绝不能串进来。
        Assert.AreEqual(2, payload!.Items.Count, "只应返回该条目自己会话下的 run");
        Assert.AreEqual(2, payload.Total, "total 是全量计数，此处恰好全量可见");
        Assert.IsFalse(payload.Truncated, "未截断时不得报告截断");

        // 自增 Id 大的在前 —— 这是「最近优先」的判据，不依赖 started_at 文本排序。
        Assert.AreEqual("run-a2", payload.Items[0].RunId, "最近创建的 run 必须排在首位");
        Assert.AreEqual("failed", payload.Items[0].Status);
        Assert.AreEqual(1, payload.Items[0].TotalRounds);
        Assert.AreEqual(0, payload.Items[0].TotalToolCalls);
        Assert.AreEqual(300L, payload.Items[0].TotalDurationMs);
        Assert.AreEqual("boom", payload.Items[0].ErrorMessage);
        Assert.AreEqual("workspace-task-agent", payload.Items[0].TemplateId);

        Assert.AreEqual("run-a1", payload.Items[1].RunId);
        Assert.AreEqual("completed", payload.Items[1].Status);
        Assert.AreEqual(1200L, payload.Items[1].TotalDurationMs);
        Assert.IsNull(payload.Items[1].ErrorMessage, "成功运行不得平白长出失败原因");
    }

    [TestMethod]
    public async Task GetRuns_EntryWithNoRuns_Returns200WithEmptyArray_Not404()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(PoolRow("pool-new", "newcomer", "sub-newcomer"));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.GetRuns("newcomer", 20, CancellationToken.None);

        // 关键：新成员「从没干过活」不能表现成错误。
        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var payload = ((OkObjectResult)result.Result!).Value as SubAgentRunsPageDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(0, payload!.Items.Count, "没有运行历史时必须返回空数组");
        Assert.AreEqual(0, payload.Total);
        Assert.IsFalse(payload.Truncated, "零条历史不可能是「被截断」");
    }

    [TestMethod]
    public async Task GetRuns_UnknownOrDeletedEntry_Returns404()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            // 软删后的墓碑行：名字看得见（库里有行），但它已经不在团队里。
            seed.AgentPool.Add(PoolRow(
                "pool-gone", "ghost", "sub-ghost", deletedAt: "2026-11-25T00:00:00+00:00"));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

        var missing = await controller.GetRuns("nobody", 20, CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status404NotFound, (missing.Result as ObjectResult)?.StatusCode);

        // 软删条目与「名字拼错」对外应同一语义：都不在团队里。
        var deleted = await controller.GetRuns("ghost", 20, CancellationToken.None);
        Assert.AreEqual(
            StatusCodes.Status404NotFound,
            (deleted.Result as ObjectResult)?.StatusCode,
            "已软删的条目必须当作不存在，而不是返回它的历史");
    }

    [TestMethod]
    public async Task GetRuns_ClampsLimit_AndBlankNameReturns400()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(PoolRow("pool-c", "chatter", "sub-chatter"));
            seed.SubAgentRuns.AddRange(
                RunRow("run-c1", "sub-chatter", "completed"),
                RunRow("run-c2", "sub-chatter", "completed"),
                RunRow("run-c3", "sub-chatter", "completed"));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

        var one = await controller.GetRuns("chatter", 1, CancellationToken.None);
        var onePayload = ((OkObjectResult)one.Result!).Value as SubAgentRunsPageDto;
        Assert.AreEqual(1, onePayload!.Items.Count, "limit 必须真的生效");
        Assert.AreEqual("run-c3", onePayload.Items[0].RunId, "截断保留的是最新的那些");

        // limit=0 被夹到 1 而不是「返回全部」：负数/零不能变成「不限量」。
        var zero = await controller.GetRuns("chatter", 0, CancellationToken.None);
        var zeroPayload = ((OkObjectResult)zero.Result!).Value as SubAgentRunsPageDto;
        Assert.AreEqual(1, zeroPayload!.Items.Count, "limit=0 必须夹到 1，不得退化成不限量");

        var blank = await controller.GetRuns("   ", 20, CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status400BadRequest, (blank.Result as ObjectResult)?.StatusCode);
    }

    /// <summary>
    /// 截断必须被显式告知：total 是全量，items 是本次返回。
    /// <para>
    /// 这个用例的判据是「两个数字不同」，而不仅是「条数正确」——
    /// 只断言条数的用例在 truncated 恒为 false 时仍然是绿的，
    /// 而那正是这个缺陷当初的形态：界面显示 20 条，看著就像只有 20 条。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task GetRuns_ReportsTotalAndTruncated_WhenLimitCutsHistory()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(PoolRow("pool-t", "talker", "sub-talker"));
            seed.SubAgentRuns.AddRange(
                RunRow("run-t1", "sub-talker", "completed"),
                RunRow("run-t2", "sub-talker", "completed"),
                RunRow("run-t3", "sub-talker", "completed"),
                RunRow("run-t4", "sub-talker", "completed"),
                RunRow("run-t5", "sub-talker", "completed"));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());

        var cut = await controller.GetRuns("talker", 2, CancellationToken.None);
        var cutPayload = ((OkObjectResult)cut.Result!).Value as SubAgentRunsPageDto;
        Assert.AreEqual(2, cutPayload!.Items.Count, "items 受 limit 约束");
        Assert.AreEqual(5, cutPayload.Total, "total 必须是全量，不能被 limit 影响");
        Assert.IsTrue(cutPayload.Truncated, "5 条只返回 2 条时必须报告截断");

        // 对照组：limit 恰好盖住全量时不得误报截断（恒为 true 也是缺陷）。
        var full = await controller.GetRuns("talker", 5, CancellationToken.None);
        var fullPayload = ((OkObjectResult)full.Result!).Value as SubAgentRunsPageDto;
        Assert.AreEqual(5, fullPayload!.Items.Count);
        Assert.AreEqual(5, fullPayload.Total);
        Assert.IsFalse(fullPayload.Truncated, "刚好返回全量时不得报告截断");
    }

    /// <summary>
    /// 花费汇总：已计价与未计价必须分开数。
    /// <para>
    /// 这个用例存在的理由是一个真实且重大的计量缺陷：资源池缺价格时账本写
    /// <c>TotalCost = 0</c>，与「真的免费」在库里完全一样（实测 claude-opus 24M token 全记为 $0）。
    /// 如果汇总只返回金额，界面就会把「价格未知」显示成「花费为零」——
    /// 那是把一个已知缺陷放大成看板数字。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task GetRuns_SeparatesPricedFromUnpriced_AndZeroTokenRowsCountAsNeither()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(PoolRow("pool-s", "spender", "sub-spender"));
            seed.SubAgentRuns.Add(RunRow("run-s1", "sub-spender", "completed"));
            seed.TokenUsageEvents.AddRange(
                LedgerRow("sub-spender", tokens: 1000, cost: 0.5m),
                LedgerRow("sub-spender", tokens: 2000, cost: 0m),
                LedgerRow("sub-spender", tokens: 0, cost: 0m),
                LedgerRow("sub-other", tokens: 999999, cost: 99m));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.GetRuns("spender", 20, CancellationToken.None);
        var payload = ((OkObjectResult)result.Result!).Value as SubAgentRunsPageDto;

        Assert.IsNotNull(payload?.Spend, "有账本行时必须返回汇总，而不是 null");
        var spend = payload!.Spend!;
        Assert.AreEqual(3000L, spend.TotalTokens, "token 合计必须包含未计价行，但不得包含别的子会话");
        Assert.AreEqual(0.5m, spend.PricedCostUsd, "金额只能是已计价行的和；未计价行不得被当成 0 元汇进总额");
        Assert.AreEqual(1, spend.PricedCalls);
        Assert.AreEqual(1, spend.UnpricedCalls, "缺价且有用量的行必须被单独数出来（TotalTokens=0 的失败请求不算）");
    }

    /// <summary>
    /// 没有任何账本行时返回 <c>null</c>，而不是四个 0。
    /// <para>
    /// null 与 0 是两个事实：「从未产生过账本」 vs 「有记录但一条都没计价」。
    /// 前端据此决定不显示汇总行，还是显示「全部未计价」警告。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task GetRuns_SpendIsNull_WhenNoLedgerRowsExist()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.Add(PoolRow("pool-n", "quiet", "sub-quiet"));
            seed.SubAgentRuns.Add(RunRow("run-n1", "sub-quiet", "completed"));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var result = await controller.GetRuns("quiet", 20, CancellationToken.None);
        var payload = ((OkObjectResult)result.Result!).Value as SubAgentRunsPageDto;

        Assert.IsNull(payload?.Spend, "零账本行必须返回 null，用四个 0 无法区分「没记录」与「没计价」");
    }

    /// <summary>
    /// 列表也带花费，且与运行历史同口径。
    /// <para>
    /// 这是「谁最贵」排行榜的数据源：若列表不带花费，前端只能对每个成员各发一次 <c>/runs</c>，
    /// 五个成员就是五次往返；而两处若各写一份聚合公式，同一位成员在两个界面
    /// 显示不同金额只是时间问题 —— 本用例同时锁这两件事。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task List_IncludesSpend_PerEntry_AndMatchesRunsEndpoint()
    {
        await using var scope = await CreateDatabaseAsync();
        await using (var seed = await scope.Factory.CreateDbContextAsync())
        {
            seed.AgentPool.AddRange(
                PoolRow("pool-s", "spender", "sub-spender"),
                PoolRow("pool-q", "quiet", "sub-quiet"));
            seed.SubAgentRuns.Add(RunRow("run-s1", "sub-spender", "completed"));
            seed.TokenUsageEvents.AddRange(
                LedgerRow("sub-spender", tokens: 1000, cost: 0.5m),
                LedgerRow("sub-spender", tokens: 2000, cost: 0m),
                LedgerRow("sub-spender", tokens: 0, cost: 0m),
                LedgerRow("sub-other", tokens: 999999, cost: 99m));
            await seed.SaveChangesAsync();
        }

        var controller = new SubAgentPoolAdminController(scope.Factory, Mock.Of<ISubAgentPool>());
        var listResult = await controller.Get(CancellationToken.None);
        var entries = ((OkObjectResult)listResult.Result!).Value as IReadOnlyList<SubAgentPoolEntryDto>;
        Assert.IsNotNull(entries);

        var spender = entries!.Single(e => e.Name == "spender");
        var quiet = entries.Single(e => e.Name == "quiet");

        Assert.IsNotNull(spender.Spend, "有账本行的条目必须带出汇总");
        Assert.AreEqual(3000L, spender.Spend!.TotalTokens, "别的子会话的账本行不得串进来");
        Assert.AreEqual(0.5m, spender.Spend.PricedCostUsd);
        Assert.AreEqual(1, spender.Spend.PricedCalls);
        Assert.AreEqual(1, spender.Spend.UnpricedCalls, "缺价且有用量的行必须被单独数出来");

        Assert.IsNull(quiet.Spend, "零账本行必须是 null，而不是四个 0");
        Assert.IsTrue(
            entries.All(e => e.Spend?.PricedCostUsd != 99m),
            "未进入池的 sub-other 不得出现在任何条目上");

        // 同一份账本，两个端点必须给出同一组数字（共用 SummarizeSpend）。
        var pool = new Mock<ISubAgentPool>();
        pool.Setup(p => p.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PooledSubAgent>)new[] { PoolEntry("spender") });
        var runsController = new SubAgentPoolAdminController(scope.Factory, pool.Object);
        var runsResult = await runsController.GetRuns("spender", 20, CancellationToken.None);
        var page = ((OkObjectResult)runsResult.Result!).Value as SubAgentRunsPageDto;
        Assert.IsNotNull(page?.Spend, "运行历史侧应当也有汇总");

        Assert.AreEqual(spender.Spend.TotalTokens, page!.Spend!.TotalTokens, "列表与运行历史必须同口径");
        Assert.AreEqual(spender.Spend.PricedCostUsd, page.Spend.PricedCostUsd);
        Assert.AreEqual(spender.Spend.PricedCalls, page.Spend.PricedCalls);
        Assert.AreEqual(spender.Spend.UnpricedCalls, page.Spend.UnpricedCalls);
    }

    /// <summary>造一条池条目：只填必填列，展示列留空。</summary>
    private static AgentPoolEntity PoolRow(
        string poolId,
        string name,
        string subSessionId,
        string? deletedAt = null) => new()
    {
        PoolId = poolId,
        OwnerWorkspaceId = "default",
        Name = name,
        SubSessionId = subSessionId,
        TemplateId = "workspace-task-agent",
        Status = AgentPoolEntity.StatusIdle,
        CreatedAt = "2026-11-25T00:00:00+00:00",
        LastUsedAt = "2026-11-25T00:00:00+00:00",
        DeletedAt = deletedAt,
    };

    /// <summary>造一条运行索引行：文件归档不重要，本测试只看数据库索引。</summary>
    private static SubAgentRunEntity RunRow(
        string runId,
        string subSessionId,
        string status,
        int rounds = 0,
        int tools = 0,
        long durationMs = 0,
        string? error = null) => new()
    {
        RunId = runId,
        ParentSessionId = "parent-session",
        SubSessionId = subSessionId,
        WorkspaceId = "default",
        AgentInstanceId = "default.global_general-assistant.6a8",
        TemplateId = "workspace-task-agent",
        Status = status,
        StartedAt = "2026-11-25T00:00:00+00:00",
        ArchivePath = $"/archive/{runId}",
        ErrorMessage = error,
        TotalRounds = rounds,
        TotalToolCalls = tools,
        TotalDurationMs = durationMs,
    };

    /// <summary>
    /// 造一条账本行。只填汇总用到的列与必填列。
    /// <para>
    /// <c>SourceId</c> 有唯一索引 <c>(SourceType, SourceId)</c>，所以每条必须不同；
    /// 用递增序号而不是 Guid，失败时的日志更可读。
    /// </para>
    /// </summary>
    private static TokenUsageEventEntity LedgerRow(string subAgentId, long tokens, decimal cost) => new()
    {
        SourceType = "agent_llm",
        SourceId = $"ledger-{Interlocked.Increment(ref _ledgerSeq)}",
        SubAgentId = subAgentId,
        YearMonth = "2026-11",
        OccurredAtUtc = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero),
        PromptTokens = tokens,
        TotalTokens = tokens,
        TotalCost = cost,
        CreatedAtUtc = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero),
    };

    private static int _ledgerSeq;

    /// <summary>构造一个内存快照条目：只有 <c>Name</c> 参与判定，其余字段填得能通过必填校验。</summary>
    private static PooledSubAgent PoolEntry(string name) => new()
    {
        PoolId = $"poolid-{name}",
        Name = name,
        SubSessionId = $"sub-{name}",
        TemplateId = "workspace-task-agent",
        CreatedAt = DateTimeOffset.UtcNow,
        LastUsedAt = DateTimeOffset.UtcNow,
        Status = PooledSubAgentStatus.Idle,
        TaskCount = 0,
    };

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
