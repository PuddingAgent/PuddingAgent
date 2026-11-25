using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SubAgentPoolTests
{
    private static LlmConfig CreateLlmConfig() => new()
    {
        ModelId = "test-model",
        KeyVaultId = "test-key",
        Endpoint = "https://test.example.com/v1",
    };

    private static LlmInvocationProfile CreateLlmProfile() => new()
    {
        ProviderId = "test-provider",
        ProfileId = "subagent.conscious",
        ModelId = "test-model",
        Role = "conscious",
    };

    private static SubAgentSpawnRequest CreateSpawnRequest(string task = "test task") => new()
    {
        ParentSessionId = "parent-session",
        WorkspaceId = "default",
        TaskDescription = task,
        LlmConfig = CreateLlmConfig(),
        LlmProfile = CreateLlmProfile(),
        TemplateId = "workspace-task-agent",
    };

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); Assert.Fail($"Expected {typeof(TException).Name}"); }
        catch (TException) { }
    }

    private static SubAgentPool CreatePool(
        TestSubAgentManager? manager = null,
        int? maxCapacity = null)
    {
        var configBuilder = new ConfigurationBuilder();
        if (maxCapacity.HasValue)
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SubAgentPool:MaxCapacity"] = maxCapacity.Value.ToString(),
            });
        }
        return new SubAgentPool(
            manager ?? new TestSubAgentManager(),
            configBuilder.Build(),
            NullLogger<SubAgentPool>.Instance);
    }

    // ═══════════════════════════════════════════════════════════
    // CreateAsync
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task CreateAsync_CreatesIdleSubAgent()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);
        var result = await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        Assert.AreEqual("dev-agent", result.Name);
        Assert.AreEqual(PooledSubAgentStatus.Idle, result.Status);
        Assert.AreEqual(0, result.TaskCount);
        Assert.IsNull(result.LastSuccess);
        Assert.IsNotNull(result.SubSessionId);
        Assert.AreEqual(0, manager.SpawnCallCount,
            "Pool create must reserve an identity without launching an asynchronous run.");
    }

    [TestMethod]
    public async Task CreateAsync_IncrementsCount()
    {
        var pool = CreatePool();
        Assert.AreEqual(0, pool.Count);

        await pool.CreateAsync("agent-1", CreateSpawnRequest());
        Assert.AreEqual(1, pool.Count);

        await pool.CreateAsync("agent-2", CreateSpawnRequest());
        Assert.AreEqual(2, pool.Count);
    }

    [TestMethod]
    public async Task CreateAsync_ThrowsOnDuplicateName()
    {
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        await AssertThrowsAsync<InvalidOperationException>(
            () => pool.CreateAsync("dev-agent", CreateSpawnRequest()));
    }

    [TestMethod]
    public async Task CreateAsync_ThrowsOnEmptyName()
    {
        var pool = CreatePool();

        await AssertThrowsAsync<ArgumentException>(
            () => pool.CreateAsync("", CreateSpawnRequest()));
        await AssertThrowsAsync<ArgumentException>(
            () => pool.CreateAsync("  ", CreateSpawnRequest()));
    }

    [TestMethod]
    public async Task CreateAsync_ThrowsWhenPoolFull()
    {
        var pool = CreatePool(maxCapacity: 2);
        await pool.CreateAsync("agent-1", CreateSpawnRequest());
        await pool.CreateAsync("agent-2", CreateSpawnRequest());

        await AssertThrowsAsync<InvalidOperationException>(
            () => pool.CreateAsync("agent-3", CreateSpawnRequest()));
    }

    // ═══════════════════════════════════════════════════════════
    // GetAsync
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetAsync_ReturnsSnapshot()
    {
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        var result = await pool.GetAsync("dev-agent");
        Assert.IsNotNull(result);
        Assert.AreEqual("dev-agent", result!.Name);
        Assert.AreEqual(PooledSubAgentStatus.Idle, result.Status);
    }

    [TestMethod]
    public async Task GetAsync_ReturnsNullForUnknown()
    {
        var pool = CreatePool();
        var result = await pool.GetAsync("nonexistent");
        Assert.IsNull(result);
    }

    // ═══════════════════════════════════════════════════════════
    // ExecuteAsync
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_RunsTaskOnExistingAgent()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);

        await pool.CreateAsync("dev-agent", CreateSpawnRequest());
        var result = await pool.ExecuteAsync("dev-agent", CreateSpawnRequest("fix bug"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("ok: fix bug", result.Reply);

        var snapshot = await pool.GetAsync("dev-agent");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, snapshot!.Status);
        Assert.AreEqual(1, snapshot.TaskCount);
        Assert.AreEqual(0, manager.SpawnCallCount,
            "Pool create must not launch a hidden asynchronous run before ExecuteSyncAsync.");
        Assert.AreEqual(snapshot.SubSessionId, manager.LastExecuteRequest?.ReuseSubSessionId);
        Assert.IsTrue(snapshot.LastSuccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_AutoCreatesWhenNotExists()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);

        var result = await pool.ExecuteAsync("auto-agent", CreateSpawnRequest("auto task"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("ok: auto task", result.Reply);

        var snapshot = await pool.GetAsync("auto-agent");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, snapshot!.Status);
        Assert.AreEqual(1, snapshot.TaskCount);
        Assert.AreEqual(0, manager.SpawnCallCount,
            "Auto-create must not launch a hidden asynchronous run before ExecuteSyncAsync.");
        Assert.AreEqual(snapshot.SubSessionId, manager.LastExecuteRequest?.ReuseSubSessionId);
    }

    [TestMethod]
    public async Task ExecuteAsync_InheritsStoredRoleWhenRequestOmitsIt()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);

        // 建员时声明角色，派活时不传：执行器必须看到成员保存的那个角色。
        await pool.CreateAsync("dev-agent", CreateSpawnRequest() with { RoleInPlan = "verifier" });
        await pool.ExecuteAsync("dev-agent", CreateSpawnRequest("check") with { RoleInPlan = null });

        Assert.AreEqual("verifier", manager.LastExecuteRequest?.RoleInPlan,
            "未显式传角色时必须继承成员保存的声明，否则「这个成员永远是审阅者」这类约定形同不存在");
    }

    [TestMethod]
    public async Task ExecuteAsync_ExplicitRoleOverridesStoredOne()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);

        await pool.CreateAsync("dev-agent", CreateSpawnRequest() with { RoleInPlan = "verifier" });
        await pool.ExecuteAsync("dev-agent", CreateSpawnRequest("one-off") with { RoleInPlan = "implementer" });

        Assert.AreEqual("implementer", manager.LastExecuteRequest?.RoleInPlan,
            "单次派活显式传的角色必须覆盖成员声明（允许例外），而不是被成员配置吃掉");
    }

    [TestMethod]
    public async Task ExecuteAsync_StaleCompletion_AfterDestroyAndRecreate_DoesNotTouchNewEntry()
    {
        // D-P0-3 回归：同名条目在「置 Busy」与「收尾」之间被 destroy → create 换成新对象后，
        // 旧执行的收尾必须被丢弃。判据是引用相等 + 执行令牌，而不是名字。
        var manager = new TestSubAgentManager { DelayMs = 800 };
        var pool = CreatePool(manager);
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        var firstRun = pool.ExecuteAsync("dev-agent", CreateSpawnRequest("first run"));

        // 等它进入 manager 的延时窗口（此时条目已 Busy 并写下执行令牌）
        await Task.Delay(250);

        await pool.DestroyAsync("dev-agent");
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        // 让第一次执行收尾：它可能因取消而抛，也可能正常返回；两种都不该污染新条目
        try { await firstRun; }
        catch (Exception) { /* 取消导致的失败不是本用例的断言对象 */ }

        var snapshot = await pool.GetAsync("dev-agent");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(0, snapshot!.TaskCount, "迟到收尾不得给新条目累加 task_count");
        Assert.IsNull(snapshot.LastSuccess, "迟到收尾不得覆盖新条目的 last_success");
        Assert.AreEqual(PooledSubAgentStatus.Idle, snapshot.Status, "迟到收尾不得把新条目打回 Sleeping");
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenOwnerSessionDiffers_ThrowsAndKeepsEntryIdle()
    {
        // D-P0-1 回归：归属校验必须在进入执行链之前拒绝，且不得改变条目状态。
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        var foreignRequest = CreateSpawnRequest("cross-parent") with
        {
            ParentSessionId = "another-parent-session",
        };

        PoolOwnershipMismatchException? caught = null;
        try { await pool.ExecuteAsync("dev-agent", foreignRequest); }
        catch (PoolOwnershipMismatchException ex) { caught = ex; }

        Assert.IsNotNull(caught, "跨父会话执行必须被拒绝");
        Assert.AreEqual(PoolOwnershipMismatchException.OwnerSessionReason, caught!.ReasonCode);
        Assert.IsNull(manager.LastExecuteRequest, "归属不符时不得进入执行链（不得建 run）");

        var snapshot = await pool.GetAsync("dev-agent");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Idle, snapshot!.Status, "被拒后条目必须仍是 Idle");
        Assert.AreEqual(0, snapshot.TaskCount, "被拒后不得累加 task_count");
        Assert.IsNull(snapshot.LastSuccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenOwnerWorkspaceDiffers_ThrowsAndKeepsEntryIdle()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        var foreignRequest = CreateSpawnRequest("cross-workspace") with
        {
            WorkspaceId = "another-workspace",
        };

        PoolOwnershipMismatchException? caught = null;
        try { await pool.ExecuteAsync("dev-agent", foreignRequest); }
        catch (PoolOwnershipMismatchException ex) { caught = ex; }

        Assert.IsNotNull(caught, "跨工作区执行必须被拒绝");
        Assert.AreEqual(PoolOwnershipMismatchException.OwnerWorkspaceReason, caught!.ReasonCode);
        Assert.IsNull(manager.LastExecuteRequest);

        var snapshot = await pool.GetAsync("dev-agent");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Idle, snapshot!.Status);
        Assert.AreEqual(0, snapshot.TaskCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_ThrowsWhenAgentBusy()
    {
        var manager = new TestSubAgentManager { DelayMs = 2000 };
        var pool = CreatePool(manager);

        await pool.CreateAsync("slow-agent", CreateSpawnRequest());

        // 启动第一个任务（不等待）
        var task1 = pool.ExecuteAsync("slow-agent", CreateSpawnRequest("task1"));

        // 立即尝试第二个任务 — 应抛出
        await AssertThrowsAsync<InvalidOperationException>(
            () => pool.ExecuteAsync("slow-agent", CreateSpawnRequest("task2")));

        await task1; // 等待 task1 完成以清理
    }

    [TestMethod]
    public async Task ExecuteAsync_AutoCreatesAfterDestroy()
    {
        // After Destroy removes the entry from pool, ExecuteAsync auto-creates.
        // This is by design: auto-create provides a simpler API without explicit CreateAsync.
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());
        await pool.DestroyAsync("dev-agent");
        Assert.AreEqual(0, pool.Count);

        // Execute after destroy → auto-create
        var result = await pool.ExecuteAsync("dev-agent", CreateSpawnRequest("task after destroy"));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, pool.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_TracksTaskCountAcrossMultipleExecutions()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);

        await pool.CreateAsync("worker", CreateSpawnRequest());
        await pool.ExecuteAsync("worker", CreateSpawnRequest("task-1"));
        await pool.ExecuteAsync("worker", CreateSpawnRequest("task-2"));
        await pool.ExecuteAsync("worker", CreateSpawnRequest("task-3"));

        var snapshot = await pool.GetAsync("worker");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(3, snapshot!.TaskCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_ThrowsOnEmptyName()
    {
        var pool = CreatePool();

        await AssertThrowsAsync<ArgumentException>(
            () => pool.ExecuteAsync("", CreateSpawnRequest()));
    }

    // ═══════════════════════════════════════════════════════════
    // SleepAsync
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SleepAsync_SetsStatusToSleeping()
    {
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        var result = await pool.SleepAsync("dev-agent");
        Assert.IsTrue(result);

        var snapshot = await pool.GetAsync("dev-agent");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, snapshot!.Status);
    }

    [TestMethod]
    public async Task SleepAsync_ReturnsFalseForUnknown()
    {
        var pool = CreatePool();
        var result = await pool.SleepAsync("nonexistent");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task SleepAsync_ReturnsFalseForDead()
    {
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());
        await pool.DestroyAsync("dev-agent");

        var result = await pool.SleepAsync("dev-agent");
        Assert.IsFalse(result);
    }

    // ═══════════════════════════════════════════════════════════
    // DestroyAsync
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task DestroyAsync_RemovesFromPool()
    {
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());
        Assert.AreEqual(1, pool.Count);

        var result = await pool.DestroyAsync("dev-agent");
        Assert.IsTrue(result);
        Assert.AreEqual(0, pool.Count);

        var snapshot = await pool.GetAsync("dev-agent");
        Assert.IsNull(snapshot);
    }

    [TestMethod]
    public async Task DestroyAsync_ReturnsFalseForUnknown()
    {
        var pool = CreatePool();
        var result = await pool.DestroyAsync("nonexistent");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DestroyAsync_RemovesAgentFromList()
    {
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());
        await pool.DestroyAsync("dev-agent");

        var allAgents = pool.List();
        var deadAgent = allAgents.FirstOrDefault(a => a.Name == "dev-agent");
        Assert.IsNull(deadAgent);
    }

    // ═══════════════════════════════════════════════════════════
    // List
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task List_ReturnsEmptyWhenNoAgents()
    {
        var pool = CreatePool();
        var agents = pool.List();
        Assert.AreEqual(0, agents.Count);
    }

    [TestMethod]
    public async Task List_ReturnsAllAgentsOrderedByLastUsed()
    {
        var pool = CreatePool();
        await pool.CreateAsync("agent-a", CreateSpawnRequest());
        await Task.Delay(10);
        await pool.CreateAsync("agent-b", CreateSpawnRequest());

        var agents = pool.List();
        Assert.IsTrue(agents.Count >= 2);
        // 最近创建的在前
        Assert.AreEqual("agent-b", agents[0].Name);
    }

    // ═══════════════════════════════════════════════════════════
    // EvictLRU
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetDeclaredTemplateIdAsync_ReturnsCreatedTemplate()
    {
        // 模板决定提示词、工具与能力上限：派发若要继承它，就得先能读到它，
        // 否则池成员「是谁」就只剩名字。
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest() with { TemplateId = "code-agent" });

        Assert.AreEqual("code-agent", await pool.GetDeclaredTemplateIdAsync("dev-agent"));
    }

    [TestMethod]
    public async Task GetDeclaredTemplateIdAsync_ReturnsNullForUnknownEntry()
    {
        // 「条目不存在」与「读不到」在调用方是同一个处置（用工具默认模板），所以返回 null 而不是抛。
        var pool = CreatePool();
        Assert.IsNull(await pool.GetDeclaredTemplateIdAsync("ghost"));
    }

    [TestMethod]
    public async Task EvictLRU_ReturnsNullWhenNoSleeping()
    {
        var pool = CreatePool();
        var result = await pool.EvictLeastRecentlyUsedAsync();
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task EvictLRU_EvictsOldestSleeping()
    {
        var pool = CreatePool();
        await pool.CreateAsync("agent-a", CreateSpawnRequest());
        await pool.SleepAsync("agent-a");
        // agent-b 闲置，仍是 Idle（不是 Sleeping，不会被淘汰）

        var result = await pool.EvictLeastRecentlyUsedAsync();
        Assert.IsNotNull(result);
        Assert.AreEqual("agent-a", result);
    }

    // ═══════════════════════════════════════════════════════════
    // MaxCapacity
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public void MaxCapacity_DefaultIs15()
    {
        var pool = CreatePool();
        Assert.AreEqual(15, pool.MaxCapacity);
    }

    [TestMethod]
    public void MaxCapacity_ReadsFromConfiguration()
    {
        var pool = CreatePool(maxCapacity: 5);
        Assert.AreEqual(5, pool.MaxCapacity);
    }

    // ═══════════════════════════════════════════════════════════
    // ExecuteAsync failure handling
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExecuteAsync_RecordsFailureWhenManagerThrows()
    {
        var manager = new TestSubAgentManager { ShouldFail = true };
        var pool = CreatePool(manager);

        await pool.CreateAsync("bad-agent", CreateSpawnRequest());

        await AssertThrowsAsync<InvalidOperationException>(
            () => pool.ExecuteAsync("bad-agent", CreateSpawnRequest("will fail")));

        var snapshot = await pool.GetAsync("bad-agent");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, snapshot!.Status);
        Assert.AreEqual(1, snapshot.TaskCount);
        Assert.IsFalse(snapshot.LastSuccess);
    }

// ═══════════════════════════════════════════════════════════
// Test doubles
// ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════
    // Busy 条目的休眠/销毁必须先真取消执行（缺陷 D1 同型）
    // ═══════════════════════════════════════

    [TestMethod]
    public async Task SleepAsync_BusyEntry_CancelsInFlightRun()
    {
        var manager = new TestSubAgentManager { DelayMs = 250 };
        var pool = CreatePool(manager);
        var created = await pool.CreateAsync("agent-a", CreateSpawnRequest());

        var exec = pool.ExecuteAsync("agent-a", CreateSpawnRequest());
        var becameBusy = false;
        for (var i = 0; i < 200; i++)
        {
            if ((await pool.GetAsync("agent-a"))?.Status == PooledSubAgentStatus.Busy)
            {
                becameBusy = true;
                break;
            }
            await Task.Delay(5);
        }
        Assert.IsTrue(becameBusy, "precondition: the pooled entry must reach Busy");

        await pool.SleepAsync("agent-a");
        await exec;

        Assert.IsTrue(manager.CancelledSubSessionIds.Contains(created.SubSessionId),
            "SleepAsync on a Busy entry must cancel the in-flight run; " +
            "changing Status alone leaves the loop spending while the log claims 'force-sleeping'");

        // 取消必须带上本次执行的令牌：subSessionId 在池复用下无法区分代次，
        // 只按它取消时，A 单已收尾、B 单接了同一子会话，迟到的休眠请求会把 B 打死
        // （2026-11-25 隔离实验复现）。池的职责是「传对代次」，比对在 SubAgentManager。
        Assert.IsTrue(manager.CancelExecutionRequests.Count > 0,
            "休眠取消必须走带代次的入口，而不是只传 subSessionId");
        var cancelRequest = manager.CancelExecutionRequests[^1];
        Assert.AreEqual(created.SubSessionId, cancelRequest.SubSessionId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(cancelRequest.ExpectedExecutionId),
            "休眠取消必须带执行令牌，否则迟到的取消会命中下一单");
    }

    /// <summary>
    /// 旧行为：SleepAsync 发完取消就置 Sleeping 并清空令牌，条目立刻又变得可被抢占，
    /// 而 CancelAsync 只是「请求取消」——执行链可能还在跑，两次派发会并行驱动同一个子会话。
    /// 这里固定住新的契约：忙碌条目在旧执行确认退出前不得接受第二次派发。
    /// </summary>
    [TestMethod]
    public async Task SleepAsync_BusyEntry_RejectsConcurrentDispatchUntilRunExits()
    {
        // 测试替身的 CancelAsync 是空实现（只记录），所以「取消」不会真的提前结束执行；
        // 这正好是旧行为最危险的场景：取消已发出、执行仍在跑。
        var manager = new TestSubAgentManager { DelayMs = 400 };
        var pool = CreatePool(manager);
        await pool.CreateAsync("agent-a", CreateSpawnRequest());

        var exec = pool.ExecuteAsync("agent-a", CreateSpawnRequest());
        var becameBusy = false;
        for (var i = 0; i < 200; i++)
        {
            if ((await pool.GetAsync("agent-a"))?.Status == PooledSubAgentStatus.Busy)
            {
                becameBusy = true;
                break;
            }
            await Task.Delay(5);
        }
        Assert.IsTrue(becameBusy, "precondition: the pooled entry must reach Busy");

        var sleep = pool.SleepAsync("agent-a");

        // 留出时间让 SleepAsync 发出取消并进入等待（调度器、DB 都不存在时的最小观察窗）。
        await Task.Delay(150);

        await AssertThrowsAsync<InvalidOperationException>(
            () => pool.ExecuteAsync("agent-a", CreateSpawnRequest()));

        var slept = await sleep;
        Assert.IsTrue(slept, "执行退出后休眠就应该成功");

        // 返回 true 必须意味着那次执行的收尾已经落账：否则「已休眠」是谎报，
        // 而且 task_count 会永久少计一笔。
        var afterSleep = await pool.GetAsync("agent-a");
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, afterSleep!.Status);
        Assert.AreEqual(1, afterSleep.TaskCount,
            "SleepAsync 返回 true 时，旧执行的收尾必须已经生效（否则两次派发被合法化）");

        await exec;
    }

    [TestMethod]
    public async Task DestroyAsync_BusyEntry_CancelsInFlightRun()
    {
        var manager = new TestSubAgentManager { DelayMs = 250 };
        var pool = CreatePool(manager);
        var created = await pool.CreateAsync("agent-a", CreateSpawnRequest());

        var exec = pool.ExecuteAsync("agent-a", CreateSpawnRequest());
        var becameBusy = false;
        for (var i = 0; i < 200; i++)
        {
            if ((await pool.GetAsync("agent-a"))?.Status == PooledSubAgentStatus.Busy)
            {
                becameBusy = true;
                break;
            }
            await Task.Delay(5);
        }
        Assert.IsTrue(becameBusy, "precondition: the pooled entry must reach Busy");

        await pool.DestroyAsync("agent-a");
        await exec;

        Assert.IsTrue(manager.CancelledSubSessionIds.Contains(created.SubSessionId),
            "DestroyAsync on a Busy entry must cancel the in-flight run; " +
            "a dead pool entry does not stop a running loop");
    }

    [TestMethod]
    public async Task SleepAsync_IdleEntry_DoesNotCancelAnything()
    {
        var manager = new TestSubAgentManager();
        var pool = CreatePool(manager);
        await pool.CreateAsync("agent-a", CreateSpawnRequest());

        await pool.SleepAsync("agent-a");

        Assert.AreEqual(0, manager.CancelledSubSessionIds.Count,
            "an idle entry has nothing to cancel; cancelling anyway would emit spurious events");
    }

    // ═══════════════════════════════════════════
    // 归属范围（P1-1）：池按会话隔离，普通入口不得跨会话
    // ═══════════════════════════════════════════

    private static SubAgentSpawnRequest CreateSpawnRequestForSession(string sessionId) => new()
    {
        ParentSessionId = sessionId,
        WorkspaceId = "default",
        TaskDescription = "test task",
        LlmConfig = CreateLlmConfig(),
        LlmProfile = CreateLlmProfile(),
        TemplateId = "workspace-task-agent",
    };

    [TestMethod]
    public async Task ListOwnedBy_ReturnsOnlyThatSessionEntries_AndSnapshotCarriesOwner()
    {
        var pool = CreatePool();
        await pool.CreateAsync("mine", CreateSpawnRequestForSession("sess-a"));
        await pool.CreateAsync("theirs", CreateSpawnRequestForSession("sess-b"));

        var mine = pool.ListOwnedBy(new PoolOwnerScope("sess-a", "default"));

        Assert.AreEqual(1, mine.Count);
        Assert.AreEqual("mine", mine[0].Name);
        // 快照必须带出归属：否则工具层无法判断「是不是我的」，只能回头用全局入口。
        Assert.AreEqual("sess-a", mine[0].OwnerSessionId);
        Assert.AreEqual("default", mine[0].OwnerWorkspaceId);
    }

    [TestMethod]
    public async Task ListOwnedBy_Unscoped_ReturnsAllEntries_ForManagementPlane()
    {
        var pool = CreatePool();
        await pool.CreateAsync("mine", CreateSpawnRequestForSession("sess-a"));
        await pool.CreateAsync("theirs", CreateSpawnRequestForSession("sess-b"));

        var all = pool.ListOwnedBy(PoolOwnerScope.Unscoped);

        Assert.AreEqual(2, all.Count, "管理面跨会话，必须看得到全部");
    }

    [TestMethod]
    public async Task SleepAsync_CrossSession_Throws_AndLeavesStatusUntouched()
    {
        var pool = CreatePool();
        await pool.CreateAsync("theirs", CreateSpawnRequestForSession("sess-b"));

        await AssertThrowsAsync<PoolOwnershipMismatchException>(() =>
            pool.SleepAsync("theirs", new PoolOwnerScope("sess-a", "default")));

        var entry = await pool.GetAsync("theirs");
        Assert.IsNotNull(entry, "被拒的休眠不得把条目弄丢");
        Assert.AreEqual(PooledSubAgentStatus.Idle, entry.Status,
            "归属断言必须先于状态变更：否则拒绝时已把别人的条目标成 sleeping");
    }

    [TestMethod]
    public async Task DestroyAsync_CrossSession_Throws_AndKeepsEntry()
    {
        var pool = CreatePool();
        await pool.CreateAsync("theirs", CreateSpawnRequestForSession("sess-b"));

        await AssertThrowsAsync<PoolOwnershipMismatchException>(() =>
            pool.DestroyAsync("theirs", new PoolOwnerScope("sess-a", "default")));

        Assert.IsNotNull(await pool.GetAsync("theirs"), "被拒的销毁不得删掉别人的条目");
        Assert.AreEqual(1, pool.Count);
    }

    [TestMethod]
    public async Task DestroyAsync_CrossWorkspace_Throws()
    {
        var pool = CreatePool();
        await pool.CreateAsync("mine", CreateSpawnRequestForSession("sess-a"));

        await AssertThrowsAsync<PoolOwnershipMismatchException>(() =>
            pool.DestroyAsync("mine", new PoolOwnerScope("sess-a", "other-workspace")));
    }

    [TestMethod]
    public async Task ScopedOperations_OnOwnEntry_Succeed()
    {
        var pool = CreatePool();
        await pool.CreateAsync("mine", CreateSpawnRequestForSession("sess-a"));
        var scope = new PoolOwnerScope("sess-a", "default");

        Assert.IsTrue(await pool.SleepAsync("mine", scope));
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, (await pool.GetAsync("mine"))!.Status);

        Assert.IsTrue(await pool.DestroyAsync("mine", scope));
        Assert.AreEqual(0, pool.Count);
    }
}

public sealed class TestSubAgentManager : ISubAgentManager
{
    public List<string> CancelledSubSessionIds { get; } = new();

    /// <summary>
    /// 每次取消请求带上来的执行标识（<c>expectedExecutionId</c>），与 subSessionId 成对。
    /// <para>
    /// 池的职责是「传对代次」，比对职责在 SubAgentManager。因此桩不模拟命中/不命中，
    /// 只如实记录收到什么——测试断言池有没有把本次执行的令牌带下来。
    /// </para>
    /// </summary>
    public List<(string SubSessionId, string? ExpectedExecutionId)> CancelExecutionRequests { get; } = new();
    public int DelayMs { get; set; }
    public bool ShouldFail { get; set; }
    private int _spawnCounter;
    private int _spawnCallCount;
    private int _executeCallCount;

    public int SpawnCallCount => Volatile.Read(ref _spawnCallCount);

    /// <summary>
    /// 真正进入执行链的次数。用于区分「被前置门（归属 / 库抢占）拦住」与
    /// 「拦了但已经建了 run」——后者会消耗预算并在会话投影里留下一条失败记录。
    /// </summary>
    public int ExecuteCallCount => Volatile.Read(ref _executeCallCount);
    public SubAgentSpawnRequest? LastExecuteRequest { get; private set; }

    public Task<SubAgentSpawnResult> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _spawnCallCount);
        var id = Interlocked.Increment(ref _spawnCounter);
        return Task.FromResult(new SubAgentSpawnResult
        {
            SubSessionId = $"pool-sub-{id}",
            Success = true,
        });
    }

    public async Task<SubAgentExecuteResult> ExecuteSyncAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _executeCallCount);
        LastExecuteRequest = request;
        if (ShouldFail)
            throw new InvalidOperationException("Simulated execution failure");

        if (DelayMs > 0)
            await Task.Delay(DelayMs, ct);

        return new SubAgentExecuteResult
        {
            SubSessionId = request.ReuseSubSessionId ?? $"exec-sub-{Interlocked.Increment(ref _spawnCounter)}",
            Success = true,
            Reply = $"ok: {request.TaskDescription}",
        };
    }

    public Task<int> CancelAllAsync(string parentSessionId, CancellationToken ct = default)
        => Task.FromResult(0);
    public Task<bool> CancelAsync(string subSessionId, string reason, CancellationToken ct = default)
    {
        CancelledSubSessionIds.Add(subSessionId);
        return Task.FromResult(true);
    }
    public Task<bool> CancelExecutionAsync(
        string subSessionId, string? expectedExecutionId, string reason, CancellationToken ct = default)
    {
        CancelExecutionRequests.Add((subSessionId, expectedExecutionId));
        CancelledSubSessionIds.Add(subSessionId);
        return Task.FromResult(true);
    }
    public Task<PuddingCode.SubAgents.SubAgentSteerResult> SteerAsync(string subSessionId, string message, string? createdBy = null, CancellationToken ct = default)
        => Task.FromResult(new PuddingCode.SubAgents.SubAgentSteerResult(PuddingCode.SubAgents.SubAgentSteerOutcome.NotRunning, Detail: "test double"));
    public Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(string parentSessionId, SubAgentQueryFilter? filter = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SubAgentStatus>>(Array.Empty<SubAgentStatus>());
    public Task<int> GetRunningCountAsync(string parentSessionId, CancellationToken ct = default)
        => Task.FromResult(0);
    public Task<SubAgentStatus?> GetStatusAsync(string subSessionId, CancellationToken ct = default)
        => Task.FromResult<SubAgentStatus?>(null);
    public Task<IReadOnlyList<SubAgentStatus>> GrepAsync(string parentSessionId, string keyword, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SubAgentStatus>>(Array.Empty<SubAgentStatus>());
    public Task<IReadOnlyList<SubAgentStatus>> GetRecentAsync(string parentSessionId, int days, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SubAgentStatus>>(Array.Empty<SubAgentStatus>());
    public Task<SubAgentStats> GetStatsAsync(string parentSessionId, CancellationToken ct = default)
        => Task.FromResult(new SubAgentStats());
    public string? TryGetRunId(string subSessionId) => null;
    public Task<int> CleanupAsync(
        string parentSessionId,
        SubAgentCleanupFilter filter,
        CancellationToken ct = default)
        => Task.FromResult(0);
}
