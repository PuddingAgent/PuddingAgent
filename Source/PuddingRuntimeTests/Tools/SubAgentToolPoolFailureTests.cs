using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.SubAgents;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// S5d：池化子代理操作失败必须「可区分」。
/// <para>
/// 这些失败的下一步动作完全不同（等 / 换名字 / 腾容量 / 查日志），把五种情形渲染成同一句话，
/// 等于让模型对着同一个注定失败的动作反复重试。因此这里断言的不是「有报错」，
/// 而是「各类文案互不相同且各自的标志词只出现在自己那一条」。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentToolPoolFailureTests
{
    // 空池措辞：未就绪时**绝不能**出现——「还没加载完」与「确实没有」是相反的事实。
    private const string EmptyPoolWording = "池为空";
    private const string NoSubAgentWording = "无子代理";

    // 三类失败各自的标志词，用于「可区分」断言。
    private const string OwnershipMarker = "属于其他父会话";
    private const string BusyMarker = "正忙";
    private const string FullMarker = "池已满";

    [TestMethod]
    public async Task List_WhenPoolNotReady_ReportsNotReady_NotEmpty()
    {
        var pool = new FakeSubAgentPool
        {
            ReadinessValue = PoolReadiness.Loading,
            ListError = new PoolNotReadyException(
                "[SubAgentPool] Pool is not ready (readiness=Loading); refusing to report an empty pool.",
                PoolReadiness.Loading),
        };

        var result = await ExecuteToolAsync(
            pool,
            """{"task":"list probe","pool_name":"p1","pool_action":"list"}""");

        Assert.IsFalse(result.Success, "未就绪时 list 必须失败，不得返回成功。");
        var error = result.Error ?? string.Empty;

        StringAssert.Contains(error, "未就绪");
        // Readiness 的值必须透出，否则「等一会儿」与「彻底坏了」分不清。
        StringAssert.Contains(error, "Loading");
        StringAssert.Contains(error, "稍后重试");

        Assert.IsFalse(error.Contains(EmptyPoolWording), $"未就绪被误报成空池：{error}");
        Assert.IsFalse(error.Contains(NoSubAgentWording), $"未就绪被误报成无子代理：{error}");
    }

    [TestMethod]
    public async Task List_WhenPoolLoadFailed_ReportsRecoveryFailureInsteadOfRetry()
    {
        var pool = new FakeSubAgentPool
        {
            ReadinessValue = PoolReadiness.Failed,
            ListError = new PoolNotReadyException(
                "[SubAgentPool] Pool is not ready (readiness=Failed); refusing to report an empty pool. " +
                "Last load error: SqliteException: database is locked",
                PoolReadiness.Failed,
                new InvalidOperationException("database is locked")),
        };

        var result = await ExecuteToolAsync(
            pool,
            """{"task":"list probe","pool_name":"p1","pool_action":"list"}""");

        Assert.IsFalse(result.Success);
        var error = result.Error ?? string.Empty;

        StringAssert.Contains(error, "未就绪");
        StringAssert.Contains(error, "Failed");
        // Failed 是终态：说成「稍后重试」会让人无限等一个不会自愈的池。
        StringAssert.Contains(error, "恢复已失败");
        StringAssert.Contains(error, "database is locked");
        Assert.IsFalse(error.Contains(EmptyPoolWording), $"未就绪被误报成空池：{error}");
    }

    [TestMethod]
    public async Task Execute_WhenOwnershipMismatch_ExplainsOwnership()
    {
        var pool = new FakeSubAgentPool
        {
            ExecuteError = new PoolOwnershipMismatchException(
                "Sub-agent 'p1' belongs to parent session 'sess-other' but was requested by 'sess-parent'.",
                "p1",
                "sess-other",
                "sess-parent",
                PoolOwnershipMismatchException.OwnerSessionReason),
        };

        var result = await ExecuteToolAsync(pool, """{"task":"run it","pool_name":"p1"}""");

        Assert.IsFalse(result.Success);
        var error = result.Error ?? string.Empty;

        StringAssert.Contains(error, OwnershipMarker);
        // ReasonCode 要透出，排障时才能区分「会话不匹配」与「工作区不匹配」。
        StringAssert.Contains(error, PoolOwnershipMismatchException.OwnerSessionReason);
        StringAssert.Contains(error, "sess-other");

        // 与「忙」的文案必须可区分：忙是「等会儿同一个操作能成」，归属不匹配永远不可能成功。
        Assert.IsFalse(error.Contains(BusyMarker), $"归属不匹配被渲染成「忙」：{error}");
        Assert.IsFalse(error.Contains(FullMarker), $"归属不匹配被渲染成「池满」：{error}");
    }

    [TestMethod]
    public async Task Execute_WhenBusy_ReportsBusy()
    {
        var pool = new FakeSubAgentPool
        {
            ExecuteError = new InvalidOperationException(
                "Sub-agent 'p1' is currently busy. Wait for the current task to complete."),
        };

        var result = await ExecuteToolAsync(pool, """{"task":"run it","pool_name":"p1"}""");

        Assert.IsFalse(result.Success);
        var error = result.Error ?? string.Empty;

        StringAssert.Contains(error, BusyMarker);
        StringAssert.Contains(error, "执行上一次任务");

        Assert.IsFalse(error.Contains(OwnershipMarker), $"「忙」被渲染成归属不匹配：{error}");
        Assert.IsFalse(error.Contains(FullMarker), $"「忙」被渲染成池满：{error}");
    }

    [TestMethod]
    public async Task Create_WhenPoolFull_ReportsFull()
    {
        var pool = new FakeSubAgentPool
        {
            CreateError = new InvalidOperationException(
                "Sub-agent pool is full (capacity=4). Cannot create 'p1'. " +
                "Consider calling DestroyAsync on unused agents."),
        };

        var result = await ExecuteToolAsync(
            pool,
            """{"task":"create it","pool_name":"p1","pool_action":"create"}""");

        Assert.IsFalse(result.Success);
        var error = result.Error ?? string.Empty;

        StringAssert.Contains(error, FullMarker);
        // 容量数字要能核对，否则「满了」只能靠猜。
        StringAssert.Contains(error, "容量 4");
        StringAssert.Contains(error, "destroy");

        Assert.IsFalse(error.Contains(BusyMarker), $"池满被渲染成「忙」：{error}");
        Assert.IsFalse(error.Contains(OwnershipMarker), $"池满被渲染成归属不匹配：{error}");
    }

    [TestMethod]
    public async Task Destroy_WhenEntryMissing_ReportsNotFound()
    {
        var pool = new FakeSubAgentPool { DestroyResult = false };

        var result = await ExecuteToolAsync(
            pool,
            """{"task":"destroy it","pool_name":"ghost","pool_action":"destroy"}""");

        Assert.IsFalse(result.Success);
        var error = result.Error ?? string.Empty;

        StringAssert.Contains(error, "未命中");
        StringAssert.Contains(error, "ghost");
        StringAssert.Contains(error, "list");
        // 未命中是「确认没有」：不能说成「未就绪」或「池为空」。
        Assert.IsFalse(error.Contains("未就绪"), $"未命中被渲染成未就绪：{error}");
        Assert.IsFalse(error.Contains(EmptyPoolWording), $"未命中被渲染成空池：{error}");
    }

    /// <summary>
    /// 五种失败两两不同：本任务的核心判据。只断言「有报错」证明不了可区分，
    /// 所以这里把全部文案收齐后做集合去重（5 条必须变成 5 条）。
    /// </summary>
    [TestMethod]
    public async Task PoolFailures_ArePairwiseDistinct()
    {
        var messages = new Dictionary<string, string>
        {
            ["not-ready"] = (await ExecuteToolAsync(
                new FakeSubAgentPool
                {
                    ListError = new PoolNotReadyException("not ready", PoolReadiness.Loading),
                },
                """{"task":"t","pool_name":"p1","pool_action":"list"}""")).Error ?? string.Empty,
            ["ownership"] = (await ExecuteToolAsync(
                new FakeSubAgentPool
                {
                    ExecuteError = new PoolOwnershipMismatchException(
                        "owner mismatch", "p1", "sess-other", "sess-parent",
                        PoolOwnershipMismatchException.OwnerSessionReason),
                },
                """{"task":"t","pool_name":"p1"}""")).Error ?? string.Empty,
            ["busy"] = (await ExecuteToolAsync(
                new FakeSubAgentPool
                {
                    ExecuteError = new InvalidOperationException("Sub-agent 'p1' is currently busy."),
                },
                """{"task":"t","pool_name":"p1"}""")).Error ?? string.Empty,
            ["full"] = (await ExecuteToolAsync(
                new FakeSubAgentPool
                {
                    CreateError = new InvalidOperationException(
                        "Sub-agent pool is full (capacity=4). Cannot create 'p1'."),
                },
                """{"task":"t","pool_name":"p1","pool_action":"create"}""")).Error ?? string.Empty,
            ["missing"] = (await ExecuteToolAsync(
                new FakeSubAgentPool { DestroyResult = false },
                """{"task":"t","pool_name":"p1","pool_action":"destroy"}""")).Error ?? string.Empty,
        };

        foreach (var (kind, message) in messages)
            Assert.IsFalse(string.IsNullOrWhiteSpace(message), $"{kind} 没有产生错误文案。");

        Assert.AreEqual(
            messages.Count,
            messages.Values.Distinct(StringComparer.Ordinal).Count(),
            "五种池失败的文案必须两两不同，否则模型无法据此选择下一步：" +
            string.Join(" || ", messages.Select(kv => $"{kv.Key}={kv.Value.Split('\n')[0]}")));
    }

    // === 池管理动作不消费任务正文（2026-11-24 修）===
    // 缺陷现场：只读的 pool_action=list 也要传 task，模型只能编一个假任务才能看一眼池状态；
    // destroy / sleep 更糟——它们在旧位置先过委派深度与并发守卫，等于「想销毁条目却先被创建类守卫拒绝」。

    [TestMethod]
    public async Task List_WithoutTaskOrPoolName_Succeeds()
    {
        var pool = new FakeSubAgentPool
        {
            Entries =
            [
                new PooledSubAgent
                {
                    PoolId = "poolid-p1",
                    Name = "p1",
                    SubSessionId = "sess-parent-sub-p1",
                    TemplateId = "workspace-task-agent",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    LastUsedAt = DateTimeOffset.UnixEpoch,
                    Status = PooledSubAgentStatus.Idle,
                    TaskCount = 0,
                    // 归属必须给：本用例验的是「不要求 task」，而归属未记录的条目
                    // 现在不会出现在本会话的列表里（见 ListOwnedBy 契约）。
                    OwnerSessionId = "sess-parent",
                    OwnerWorkspaceId = "workspace_1",
                },
            ],
        };

        var result = await ExecuteToolAsync(pool, """{"pool_action":"list"}""");

        Assert.IsTrue(result.Success, $"list 不应要求 task / pool_name：{result.Error}");
        StringAssert.Contains(result.Output ?? string.Empty, "p1");
    }

    [TestMethod]
    public async Task Destroy_WithoutTask_Succeeds()
    {
        var pool = new FakeSubAgentPool { DestroyResult = true };

        var result = await ExecuteToolAsync(pool, """{"pool_name":"p1","pool_action":"destroy"}""");

        Assert.IsTrue(result.Success, $"destroy 不应要求 task：{result.Error}");
        StringAssert.Contains(result.Output ?? string.Empty, "已销毁");
    }

    // === 池管理动作必须带归属范围（P1-1，2026-11-25）===
    // 缺陷现场：list / sleep / destroy 走全局池只传名字。另一个会话因此可以看到本会话的成员，
    // 并让它休眠或销毁；而同一条目的派活又会被归属校验拒掉——两个入口对同一件事给出相反结论。

    [TestMethod]
    public async Task List_HidesOtherSessionsEntries_AndPassesSessionScope()
    {
        var pool = new FakeSubAgentPool
        {
            Entries =
            [
                OwnedEntry("mine", "sess-parent"),
                OwnedEntry("theirs", "sess-other"),
            ],
        };

        var result = await ExecuteToolAsync(pool, """{"pool_action":"list"}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output ?? string.Empty, "mine");
        Assert.IsFalse((result.Output ?? string.Empty).Contains("theirs"),
            "另一个会话的成员不得出现在本会话的列表里");
        Assert.AreEqual("sess-parent", pool.LastScope?.SessionId,
            "list 必须把执行上下文的会话作为归属范围传下去");
        Assert.AreEqual("workspace_1", pool.LastScope?.WorkspaceId);
    }

    [TestMethod]
    public async Task Destroy_PassesSessionScope()
    {
        var pool = new FakeSubAgentPool { DestroyResult = true };

        var result = await ExecuteToolAsync(pool, """{"pool_name":"p1","pool_action":"destroy"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("sess-parent", pool.LastScope?.SessionId,
            "destroy 必须带归属，否则跨会话销毁会绕过池层校验");
    }

    [TestMethod]
    public async Task Sleep_PassesSessionScope()
    {
        var pool = new FakeSubAgentPool { SleepResult = true };

        var result = await ExecuteToolAsync(pool, """{"pool_name":"p1","pool_action":"sleep"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("sess-parent", pool.LastScope?.SessionId,
            "sleep 必须带归属，否则跨会话休眠会绕过池层校验");
    }

    private static PooledSubAgent OwnedEntry(string name, string ownerSessionId) => new()
    {
        PoolId = $"poolid-{name}",
        Name = name,
        SubSessionId = $"{ownerSessionId}-sub-{name}",
        TemplateId = "workspace-task-agent",
        CreatedAt = DateTimeOffset.UnixEpoch,
        LastUsedAt = DateTimeOffset.UnixEpoch,
        Status = PooledSubAgentStatus.Idle,
        TaskCount = 0,
        OwnerSessionId = ownerSessionId,
        OwnerWorkspaceId = "workspace_1",
    };

    [TestMethod]
    public async Task Sleep_WithoutTask_Succeeds()
    {
        var pool = new FakeSubAgentPool { SleepResult = true };

        var result = await ExecuteToolAsync(pool, """{"pool_name":"p1","pool_action":"sleep"}""");

        Assert.IsTrue(result.Success, $"sleep 不应要求 task：{result.Error}");
        StringAssert.Contains(result.Output ?? string.Empty, "已休眠");
    }

    [TestMethod]
    public async Task Destroy_WithoutPoolName_AsksForPoolName()
    {
        var result = await ExecuteToolAsync(new FakeSubAgentPool(), """{"pool_action":"destroy"}""");

        Assert.IsFalse(result.Success);
        var error = result.Error ?? string.Empty;

        StringAssert.Contains(error, "pool_name");
        // 旧行为：给了 pool_action 却漏了 pool_name 会静默落进一次性派发分支，
        // 等于「写错参数什么也没发生」。现在必须明说。
        StringAssert.Contains(error, "destroy");
        Assert.IsFalse(error.Contains("必需的"), $"被 task 必填拦下（说明没走到管理分支）：{error}");
    }

    [TestMethod]
    public async Task Execute_WithoutTask_StillRequiresTask()
    {
        var result = await ExecuteToolAsync(new FakeSubAgentPool(), """{"pool_name":"p1"}""");

        Assert.IsFalse(result.Success);
        // 对照组：放宽管理动作不得顺手放宽执行路径。
        StringAssert.Contains(result.Error ?? string.Empty, "必需的");
    }

    // === 单项 cleanup 的顺序与可重试性（P1，2026-11-25）===
    // 缺陷现场（外部审计隔离复现）：入口先 DestroyAsync 再按父会话清理。清理一旦失败，条目没了，
    // 唯一稳定定位键（SubSessionId）也就没了——重试只能清 0 条却报「已清理」。
    // 同时 cleanup 落在 task 解析之后，等于「想清历史先编一个假任务」（与 list/destroy/sleep 同一类缺陷）。

    [TestMethod]
    public async Task Cleanup_IdleEntry_CleansPersistedRunsBeforeDestroyingEntry()
    {
        var calls = new List<string>();
        var pool = NewCleanupPool(calls, OwnedEntry("p1", "sess-parent"));
        var manager = NewCleanupManager(calls, cleaned: 1, runId: "run-p1");
        var store = NewArchiveStore(present: false);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup"}""");

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(
            new[] { "status", "cleanup", "destroy" },
            calls,
            "空闲条目必须 定位归档→清理→复核→销毁；先销毁会在清理失败时丢掉唯一定位键：" +
            string.Join(",", calls));
        CollectionAssert.AreEqual(new[] { "run-p1" }, store.Probed,
            "复核必须按**清理前**取到的运行归档 id 查，不能按子会话 id 猜");
        StringAssert.Contains(result.Output ?? string.Empty, "已删除运行归档 1 条");
    }

    [TestMethod]
    public async Task Cleanup_WhenRunArchiveStillPresent_FailsWithoutDestroyingEntry()
    {
        // 验收对象是运行归档：它还在 ⇒ 清理没生效。CleanupAsync 逐条吞异常、只返回计数，
        // 「0 条」既可能是没删，也可能是本来就没有 —— 所以必须真读一次归档。
        var calls = new List<string>();
        var pool = NewCleanupPool(calls, OwnedEntry("p1", "sess-parent"));
        var manager = NewCleanupManager(calls, cleaned: 0, runId: "run-p1");
        var store = NewArchiveStore(present: true);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup"}""");

        Assert.IsFalse(result.Success, "运行归档还在必须失败，不得报「已清理」。");
        var error = result.Error ?? string.Empty;
        StringAssert.Contains(error, "sess-parent-sub-p1", "错误文案必须给出重试用的稳定键。");
        StringAssert.Contains(error, "sub_agent_id", "必须告诉模型下次怎么重试。");
        StringAssert.Contains(error, "已保留", "清理失败时条目必须保留，否则重试失去入口。");
        CollectionAssert.DoesNotContain(calls, "destroy",
            "清理失败时不得销毁条目，否则再无稳定定位键。");
    }

    [TestMethod]
    public async Task Cleanup_WhenRunArchiveAlreadyGone_ReportsNothingToClean()
    {
        // 归档本来就不在（此前清过，或那次运行从未落盘）：这不是失败。
        // 把它判成失败会让模型对着一个不可收敛的动作反复重试。
        var calls = new List<string>();
        var pool = NewCleanupPool(calls, OwnedEntry("p1", "sess-parent"));
        var manager = NewCleanupManager(calls, cleaned: 0, runId: "run-p1");
        var store = NewArchiveStore(present: false);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup"}""");

        Assert.IsTrue(result.Success, result.Error);
        var output = result.Output ?? string.Empty;
        StringAssert.Contains(output, "本就不存在", "必须如实说明没有可删的归档");
        Assert.IsFalse(output.Contains("已删除运行归档"),
            "一条都没删却报「已删除」是另一个方向的谎：" + output);
    }

    [TestMethod]
    public async Task Cleanup_WithoutRunArchiveStore_FailsAsUnverifiable()
    {
        // 复核所需的服务不可用时，「查不到」不等于「删掉了」。三态里 null 走这条，
        // 报表成成功就等于把「仪器坏了」写成「结论对了」。
        var calls = new List<string>();
        var pool = NewCleanupPool(calls, OwnedEntry("p1", "sess-parent"));
        var manager = NewCleanupManager(calls, cleaned: 1, runId: "run-p1");

        var result = await ExecuteCleanupAsync(pool, manager, runArchiveStore: null,
            """{"pool_name":"p1","pool_action":"cleanup"}""");

        Assert.IsFalse(result.Success, "无法复核时不得报成功。");
        StringAssert.Contains(result.Error ?? string.Empty, "无法复核");
        CollectionAssert.DoesNotContain(calls, "destroy",
            "无法确认归档已删时不得销毁条目：那样连重试入口都没了。");
    }

    [TestMethod]
    public async Task Cleanup_AfterEntryDestroyed_StillCleansByIdFromSubAgentId()
    {
        var calls = new List<string>();
        // 条目已不在池里（上一轮先销毁后清理失败留下的现场）：只给名字定位不到任何东西。
        var pool = NewCleanupPool(calls, entry: null, destroyResult: false);
        var manager = NewCleanupManager(calls, cleaned: 1, runId: "run-p1");
        var store = NewArchiveStore(present: false);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup","sub_agent_id":"sess-parent-sub-p1"}""");

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.Contains(calls, "cleanup",
            "条目已销毁时仍必须按 sub_agent_id 清理——否则失败后永远不可重试。");
    }

    [TestMethod]
    public async Task Cleanup_RetryBySubAgentId_NeverDestroysByName()
    {
        // 退化形态：条目销毁后名字立即释放，可能已被**另一个新成员**复用。
        // 这时再按 args.PoolName 调销毁，打掉的是那个无关的新成员——而回执会写成
        // 「已清理 + 池条目已销毁」，两个事实都真，唯独死的不是目标。
        var calls = new List<string>();
        var pool = NewCleanupPool(calls, entry: null, destroyResult: true);
        var manager = NewCleanupManager(calls, cleaned: 1, runId: "run-p1");
        var store = NewArchiveStore(present: false);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup","sub_agent_id":"sess-parent-sub-p1"}""");

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.DoesNotContain(calls, "destroy",
            "本会话解析不到该条目时不得按名字销毁：名字可能已被新成员复用：" + string.Join(",", calls));
    }

    [TestMethod]
    public async Task Cleanup_WhenNameResolvesToDifferentMemberThanSubAgentId_RefusesInsteadOfPickingOne()
    {
        // 名字不是稳定键：条目销毁后名字立即释放，可被新成员复用。于是「按名字解析到的成员」
        // 与「调用方给的 sub_agent_id」可能指向两个不同成员。旧实现让名字优先：这次重试会清掉
        // 新成员的归档，并把销毁也落到新成员身上——而回执里写的是调用方拼的那个名字。
        var calls = new List<string>();
        var reused = OwnedEntry("p1", "sess-parent") with { SubSessionId = "sess-new-p1" };
        var pool = NewCleanupPool(calls, reused);
        var manager = NewCleanupManager(calls, cleaned: 1, runId: "run-new-p1");
        var store = NewArchiveStore(present: false);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup","sub_agent_id":"sess-old-p1"}""");

        Assert.IsFalse(result.Success, "两个键指向不同成员时必须拒绝，不能替调用方选一个。");
        var error = result.Error ?? string.Empty;
        StringAssert.Contains(error, "sess-old-p1", "必须回显调用方给的 id");
        StringAssert.Contains(error, "sess-new-p1", "必须回显名字当前解析到的 id");
        CollectionAssert.DoesNotContain(calls, "cleanup",
            "歧义时不得清理任何成员：" + string.Join(",", calls));
        CollectionAssert.DoesNotContain(calls, "destroy",
            "歧义时不得销毁任何成员：" + string.Join(",", calls));
        Assert.AreEqual(0, store.Probed.Count, "歧义时连归档都不该探查");
    }

    [TestMethod]
    public async Task Cleanup_WhenNameAndSubAgentIdAgree_StillProceeds()
    {
        // 对照：两个键一致时不得因为「带了 id」就多拦一次——否则正常重试也会失败。
        var calls = new List<string>();
        var pool = NewCleanupPool(calls, OwnedEntry("p1", "sess-parent"));
        var manager = NewCleanupManager(calls, cleaned: 1, runId: "run-p1");
        var store = NewArchiveStore(present: false);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup","sub_agent_id":"sess-parent-sub-p1"}""");

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.Contains(calls, "cleanup", "两个键一致时必须照常清理");
    }

    [TestMethod]
    public async Task Cleanup_BusyEntry_DestroysFirstSoRunningRunCanBeCleaned()
    {
        var calls = new List<string>();
        // 忙碌条目：清理会跳过「运行中」的记录，先清只会清 0 条。
        var busy = OwnedEntry("p1", "sess-parent") with { Status = PooledSubAgentStatus.Busy };
        var pool = NewCleanupPool(calls, busy);
        var manager = NewCleanupManager(calls, cleaned: 1, runId: "run-p1");
        var store = NewArchiveStore(present: false);

        var result = await ExecuteCleanupAsync(pool, manager, store.Object,
            """{"pool_name":"p1","pool_action":"cleanup"}""");

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(
            new[] { "destroy", "status", "cleanup" },
            calls,
            "忙碌条目必须先销毁（级联取消）再清理；归档已删时不重复销毁：" + string.Join(",", calls));
    }

    [TestMethod]
    public async Task Cleanup_UnknownName_HintStatesPoolNameStillRequired()
    {
        // 这条分支的真实形态：条目已销毁、调用方只给了旧名字。
        // 旧提示写成 pool_action="cleanup" sub_agent_id="<SubSessionId>"，读起来像「只给 id 就能重试」，
        // 而管理动作入口要求 pool_name 必填 —— 照抄会先撞「需要 pool_name」，把「怎么重试」绕成一个死胡同。
        var calls = new List<string>();
        var pool = NewCleanupPool(calls, entry: null);
        var manager = NewCleanupManager(calls, cleaned: 0, runId: "run-none");

        var result = await ExecuteCleanupAsync(pool, manager, runArchiveStore: null,
            """{"pool_name":"gone","pool_action":"cleanup"}""");

        Assert.IsFalse(result.Success, "名字解析不到且没给 id 时必须失败，不能当成「清 0 条」成功");
        var text = result.Error ?? result.Output;
        StringAssert.Contains(text, "仍需 pool_name",
            "重试提示必须点明 pool_name 必填，否则调用方会以为只给 sub_agent_id 就够");
        StringAssert.Contains(text, "sub_agent_id=\"<SubSessionId>\"",
            "提示要给出 SubSessionId 的占位形态");
        Assert.AreEqual(0, calls.Count, "这条分支不应当进入执行链（不得先清再发现定位不到）：" + string.Join(",", calls));
    }

    // ═══════════════════════════════════════════════════════
    // cleanup-bulk：无预演、删了不可恢复，范围必须写在回执里
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task CleanupBulk_ReceiptStatesScopeLimitAndIrreversibility()
    {
        SubAgentCleanupFilter? captured = null;
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.CleanupAsync(
                It.IsAny<string>(), It.IsAny<SubAgentCleanupFilter>(), It.IsAny<CancellationToken>()))
            .Returns<string, SubAgentCleanupFilter, CancellationToken>((_, filter, _) =>
            {
                captured = filter;
                return Task.FromResult(37);
            });
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);

        var result = await ExecuteCleanupAsync(pool.Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cleanup-bulk"}""");

        Assert.IsTrue(result.Success, result.Error);

        // 返回值是删掉的 run 行数，不是「清掉几个子代理」：只有投影没有 run 行的条目也会被清，
        // 但不计数。旧措辞「N 个子代理已清理」会低报，而读的人无从察觉。
        StringAssert.Contains(result.Output, "已删除运行归档 37 条",
            "回执必须与 CleanupAsync 的返回值语义一致");
        Assert.IsFalse(result.Output.Contains("个子代理已清理"),
            "旧措辞把「run 行数」说成「子代理数」，会低报");

        StringAssert.Contains(result.Output, "单次最多 100 条",
            "没有预演与确认，上限是调用方唯一能预先知道的约束");
        StringAssert.Contains(result.Output, "不限于池化成员",
            "候选集是整个父会话，不是池里那几个成员");
        StringAssert.Contains(result.Output, "删除后不可恢复",
            "归档含磁盘目录，删了没法恢复");
        StringAssert.Contains(result.Output, "pool_action=\"cleanup\"",
            "必须指出只清单个成员的入口，否则读者没有更细粒度的选项");
        StringAssert.Contains(result.Output, "仍需 pool_name",
            "那个入口自己也需要 pool_name；旧提示写成「pool_action=cleanup + sub_agent_id」会被这条守卫拒掉");

        Assert.IsNotNull(captured);
        Assert.AreEqual(100, captured!.MaxCount,
            "回执说的 100 必须就是传给清理器的上限，两头各写一遍会漂移");
        Assert.IsNull(captured.SubSessionId,
            "bulk 不得悄悄退化成单点清理");
    }

    [TestMethod]
    public async Task CleanupBulk_PassesFiltersThroughAndReflectsThemInReceipt()
    {
        SubAgentCleanupFilter? captured = null;
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.CleanupAsync(
                It.IsAny<string>(), It.IsAny<SubAgentCleanupFilter>(), It.IsAny<CancellationToken>()))
            .Returns<string, SubAgentCleanupFilter, CancellationToken>((_, filter, _) =>
            {
                captured = filter;
                return Task.FromResult(5);
            });
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);

        var result = await ExecuteCleanupAsync(pool.Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cleanup-bulk","cleanup_status":"failed","cleanup_older_than_days":7}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("failed", captured!.Status);
        Assert.AreEqual(7, captured.OlderThanDays);
        StringAssert.Contains(result.Output, "状态=failed",
            "实际生效的筛选条件必须回显，否则调用方无法核对");
        StringAssert.Contains(result.Output, "早于 7 天");
    }

    [TestMethod]
    public async Task CleanupBulk_DefaultStatusReadsAsNoStatusFilter()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.CleanupAsync(
                It.IsAny<string>(), It.IsAny<SubAgentCleanupFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);

        var result = await ExecuteCleanupAsync(pool.Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cleanup-bulk"}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "不限状态");
        Assert.IsFalse(result.Output.Contains("状态=all"),
            "all 是内部默认值，渲染成「状态=all」会让人以为只清状态名为 all 的条目");
    }

    // ═══════════════════════════════════════════════════════
    // cleanup-bulk 预演：删之前能看到候选集
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// <c>dry_run=true</c> 必须只调预演、不调清理，并列出候选与下一步。
    /// </summary>
    /// <remarks>
    /// <c>CleanupAsync</c> 刻意不 Setup：Strict 下被调用即抛，等于断言「一次都没调」。
    /// 这比断言「回执里写了预演」强——后者在「先删再列」的实现下同样成立。
    /// </remarks>
    [TestMethod]
    public async Task CleanupBulk_DryRun_ListsCandidatesAndNeverCallsCleanup()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.PreviewCleanupAsync(
                It.IsAny<string>(), It.IsAny<SubAgentCleanupFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                Candidate("sub-old", "probe-old", hoursAgo: 6),
                Candidate("sub-new", "probe-new", hoursAgo: 1),
            });
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);

        var result = await ExecuteCleanupAsync(pool.Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cleanup-bulk","dry_run":true}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "预演", "回执必须标明这是预演，不是已清理");
        StringAssert.Contains(result.Output, "命中 2 条", "候选总数要如实给");
        StringAssert.Contains(result.Output, "未删除任何内容", "没有这句，读者会当成已删");
        StringAssert.Contains(result.Output, "sub-old", "候选必须逐条列出，否则看不出会删什么");
        StringAssert.Contains(result.Output, "sub-new");
        StringAssert.Contains(result.Output, "去掉 dry_run", "必须给出从预演转到真删的下一步");
        Assert.IsFalse(result.Output.Contains("已删除运行归档"),
            "预演回执不得出现删除完成句式——两种模式共用措辞就分不清看的是哪一种");
        Assert.IsTrue(
            result.Output.IndexOf("sub-old", StringComparison.Ordinal) <
            result.Output.IndexOf("sub-new", StringComparison.Ordinal),
            "预演必须按服务层给的顺序（最旧优先）列出，不得自己重排");
    }

    /// <summary>
    /// <c>dry_run=true</c> 但候选为空时，不得给出「确认后去掉 dry_run」——没东西可删。
    /// </summary>
    [TestMethod]
    public async Task CleanupBulk_DryRun_EmptyCandidateSetSaysNothingToDelete()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.PreviewCleanupAsync(
                It.IsAny<string>(), It.IsAny<SubAgentCleanupFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SubAgentStatus>());
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);

        var result = await ExecuteCleanupAsync(pool.Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cleanup-bulk","dry_run":true}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "没有匹配的子代理");
        Assert.IsFalse(result.Output.Contains("去掉 dry_run"),
            "候选为空时提示「去掉 dry_run 再调一次」会引导一次必然为空的操作");
    }

    /// <summary>
    /// 正向对照：不传 <c>dry_run</c>（或 false）时行为完全不变，仍然真删。
    /// </summary>
    /// <remarks>
    /// 缺这条时，一个「永远只预演」的实现也能让上面两条全绿。
    /// </remarks>
    [TestMethod]
    public async Task CleanupBulk_WithoutDryRun_StillDeletes()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.CleanupAsync(
                It.IsAny<string>(), It.IsAny<SubAgentCleanupFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);

        var result = await ExecuteCleanupAsync(pool.Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cleanup-bulk","dry_run":false}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "已删除运行归档 3 条");
        Assert.IsFalse(result.Output.Contains("预演"));
    }

    private static SubAgentStatus Candidate(string subSessionId, string displayName, int hoursAgo) => new()
    {
        RunId = "run-" + subSessionId,
        ParentSessionId = "sess-parent",
        SubSessionId = subSessionId,
        Status = "completed",
        TaskSummary = "candidate " + subSessionId,
        DisplayName = displayName,
        SpawnedAt = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
    };

    // ═══════════════════════════════════════════════════════
    // 运行级动作（cancel / logs / steer）的归属前置
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Cancel_ForeignRun_IsRejectedBeforeCancel()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusOwnedBy("sess-other"));
        // CancelAsync 刻意不 Setup：Strict 下被调用即抛，等于断言「一次都没调」。

        var result = await ExecuteCleanupAsync(
            new Mock<ISubAgentPool>(MockBehavior.Strict).Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cancel","sub_agent_id":"sess-other-sub-abcd1234"}""");

        Assert.IsFalse(result.Success, "跨会话取消必须被拒；否则池操作只能动自己的条目、运行操作却能动别人的");
        StringAssert.Contains(result.Error ?? string.Empty, "不属于当前会话");
    }

    [TestMethod]
    public async Task Cancel_OwnRun_Proceeds()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusOwnedBy("sess-parent"));
        manager.Setup(m => m.CancelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await ExecuteCleanupAsync(
            new Mock<ISubAgentPool>(MockBehavior.Strict).Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"cancel","sub_agent_id":"sess-parent-sub-abcd1234"}""");

        Assert.IsTrue(result.Success, result.Error);
    }

    [TestMethod]
    public async Task Logs_ForeignRun_IsRejectedBeforeReadingArchive()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusOwnedBy("sess-other"));

        // 本例刻意不注册 PuddingDataPaths：若归属校验被移除，这条会落到「服务未注册」，
        // 所以断言归属文案同时证明了「校验在读归档之前」。
        var result = await ExecuteCleanupAsync(
            new Mock<ISubAgentPool>(MockBehavior.Strict).Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"logs","sub_agent_id":"sess-other-sub-abcd1234"}""");

        Assert.IsFalse(result.Success, "跨会话读取运行归档必须被拒。");
        StringAssert.Contains(result.Error ?? string.Empty, "不属于当前会话");
    }

    [TestMethod]
    public async Task Steer_ForeignRun_IsRejectedBeforeSteer()
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusOwnedBy("sess-other"));
        // SteerAsync 不 Setup：写进别人的执行比读错更难察觉，必须一次都不调。

        var result = await ExecuteCleanupAsync(
            new Mock<ISubAgentPool>(MockBehavior.Strict).Object, manager.Object, runArchiveStore: null,
            """{"pool_action":"steer","sub_agent_id":"sess-other-sub-abcd1234","steer_message":"hi"}""");

        Assert.IsFalse(result.Success, "跨会话注入必须被拒。");
        StringAssert.Contains(result.Error ?? string.Empty, "不属于当前会话");
    }

    [TestMethod]
    public async Task Cleanup_ForeignEntryName_IsTreatedAsMissing()
    {
        // 会话内可见列表为空：别的会话的同名条目不在其中。
        // 旧实现用全局 GetAsync 会把它解析出来，再看 CleanupAsync 清 0 条，
        // 最后报「归档仍在」——把「不是你的条目」写成「清理失败」。
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);
        pool.Setup(p => p.ListOwnedBy(It.IsAny<PoolOwnerScope>()))
            .Returns((PoolOwnerScope _) => (IReadOnlyList<PooledSubAgent>)Array.Empty<PooledSubAgent>());
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);

        var result = await ExecuteCleanupAsync(pool.Object, manager.Object, runArchiveStore: null,
            """{"pool_name":"p1","pool_action":"cleanup"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error ?? string.Empty, "未命中");
        Assert.IsFalse((result.Error ?? string.Empty).Contains("归档"),
            "不得把「不是你的条目」报成归档层失败：那会让人去查磁盘而不是查归属");
    }

    private static SubAgentStatus StatusOwnedBy(string parentSessionId) => new()
    {
        RunId = "run-owner-probe",
        ParentSessionId = parentSessionId,
        SubSessionId = "sub-owner-probe",
        Status = "running",
        TaskSummary = "probe",
        SpawnedAt = DateTimeOffset.UnixEpoch,
    };

    private static ISubAgentPool NewCleanupPool(
        List<string> calls,
        PooledSubAgent? entry,
        bool destroyResult = true)
    {
        var pool = new Mock<ISubAgentPool>(MockBehavior.Strict);
        // 目标解析走会话内列表而不是全局 GetAsync：全局入口会把**别的会话**的同名条目
        // 先解析出来，于是「清理失败」被写成「归档仍在」而不是「这不是你的条目」。
        pool.Setup(p => p.ListOwnedBy(It.IsAny<PoolOwnerScope>()))
            .Returns((PoolOwnerScope _) => entry is null
                ? (IReadOnlyList<PooledSubAgent>)Array.Empty<PooledSubAgent>()
                : new[] { entry });
        pool.Setup(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<PoolOwnerScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Func<bool>(() =>
            {
                calls.Add("destroy");
                return destroyResult;
            }));
        return pool.Object;
    }

    private static ISubAgentManager NewCleanupManager(
        List<string> calls,
        int cleaned,
        string runId)
    {
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((subSessionId, _) =>
            {
                calls.Add("status");
                return Task.FromResult<SubAgentStatus?>(new SubAgentStatus
                {
                    RunId = runId,
                    ParentSessionId = "sess-parent",
                    SubSessionId = subSessionId,
                    Status = "completed",
                    TaskSummary = "probe",
                    SpawnedAt = DateTimeOffset.UnixEpoch,
                });
            });
        manager.Setup(m => m.CleanupAsync(
                It.IsAny<string>(), It.IsAny<SubAgentCleanupFilter>(), It.IsAny<CancellationToken>()))
            .Returns<string, SubAgentCleanupFilter, CancellationToken>((_, _, _) =>
            {
                calls.Add("cleanup");
                return Task.FromResult(cleaned);
            });
        return manager.Object;
    }

    /// <summary>
    /// 运行归档探针。<c>present</c> 决定 <c>GetRunArchiveAsync</c> 是否有返回，
    /// 并记录被查询的 runId —— 「按哪个 id 查」本身也是被测行为。
    /// </summary>
    private static ArchiveProbe NewArchiveStore(bool present)
    {
        var probe = new ArchiveProbe(present);
        var store = new Mock<ISubAgentRunStore>(MockBehavior.Strict);
        store.Setup(s => s.GetRunArchiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((runId, _) => Task.FromResult(probe.Hit(runId)));
        probe.Mock = store;
        return probe;
    }

    private sealed class ArchiveProbe
    {
        private readonly bool _present;

        public ArchiveProbe(bool present) => _present = present;

        public Mock<ISubAgentRunStore>? Mock { get; set; }

        public List<string> Probed { get; } = new();

        public ISubAgentRunStore Object => Mock!.Object;

        public SubAgentRunArchive? Hit(string runId)
        {
            Probed.Add(runId);
            if (!_present)
                return null;

            return new SubAgentRunArchive
            {
                Manifest = new SubAgentRunManifest
                {
                    RunId = runId,
                    ParentSessionId = "sess-parent",
                    SubSessionId = "sess-parent-sub-p1",
                    WorkspaceId = "workspace_1",
                    AgentInstanceId = "agent_parent",
                    TemplateId = "workspace-task-agent",
                    Task = "probe",
                    Status = "completed",
                    StartedAt = DateTimeOffset.UnixEpoch,
                },
            };
        }
    }

    private static async Task<ToolExecutionResult> ExecuteCleanupAsync(
        ISubAgentPool pool,
        ISubAgentManager manager,
        ISubAgentRunStore? runArchiveStore,
        string argumentsJson)
    {
        var services = new ServiceCollection()
            .AddSingleton<ILlmResolver, FakeLlmResolver>()
            .AddSingleton<ISubAgentInvocationService, NoopSubAgentInvocationService>()
            .AddSingleton(pool)
            .AddSingleton(manager);
        if (runArchiveStore is not null)
            services.AddSingleton(runArchiveStore);

        using var provider = services.BuildServiceProvider();

        var tool = new SubAgentTool(provider, NullLogger<SubAgentTool>.Instance);

        return await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-cleanup-ordering",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent_parent",
                WorkspaceId = "workspace_1",
                SessionId = "sess-parent",
            },
        });
    }

    [TestMethod]
    public async Task Execute_WithoutExplicitTemplate_InheritsDeclaredPoolTemplate()
    {
        // 池复用只回写 ReuseSubSessionId；若模板也退回工具默认值，界面上那个成员「是谁」就只剩名字——
        // 建的时候是 code-agent、派下去变成 workspace-task-agent，而结果里看不出差异。
        var pool = new FakeSubAgentPool { DeclaredTemplateId = "code-agent" };

        var result = await ExecuteToolAsync(pool, """{"task":"run it","pool_name":"p1"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "code-agent",
            pool.LastExecuteTemplateId,
            "未显式指定 agent_template 时必须继承条目声明的模板，不得退回工具默认值。");
    }

    [TestMethod]
    public async Task Execute_WithExplicitTemplate_OverridesDeclaredPoolTemplate()
    {
        // 显式值是调用方的明确意图，不能被条目声明盖掉：
        // 「这个成员平时是 code-agent，这一单临时用 audit」是合法用法。
        var pool = new FakeSubAgentPool { DeclaredTemplateId = "code-agent" };

        var result = await ExecuteToolAsync(
            pool,
            """{"task":"run it","pool_name":"p1","agent_template":"workspace-audit-agent"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "workspace-audit-agent",
            pool.LastExecuteTemplateId,
            "显式 agent_template 必须优先于条目声明的模板。");
    }

    private static async Task<ToolExecutionResult> ExecuteToolAsync(FakeSubAgentPool pool, string argumentsJson)
    {
        using var provider = new ServiceCollection()
            .AddSingleton<ILlmResolver, FakeLlmResolver>()
            .AddSingleton<ISubAgentInvocationService, NoopSubAgentInvocationService>()
            .AddSingleton<ISubAgentPool>(pool)
            .BuildServiceProvider();

        var tool = new SubAgentTool(provider, NullLogger<SubAgentTool>.Instance);

        return await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-pool-failure",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent_parent",
                WorkspaceId = "workspace_1",
                SessionId = "sess-parent",
            },
        });
    }

    /// <summary>
    /// 最小假池：只负责「按需抛异常 / 返回既定结果」。
    /// 刻意不模拟池的其它语义——本组用例验证的是工具层如何渲染失败，不是池如何失败。
    /// </summary>
    private sealed class FakeSubAgentPool : ISubAgentPool
    {
        public Exception? ListError { get; init; }
        public Exception? ExecuteError { get; init; }
        public Exception? CreateError { get; init; }
        public bool DestroyResult { get; init; } = true;
        public bool SleepResult { get; init; } = true;
        public PoolReadiness ReadinessValue { get; init; } = PoolReadiness.Ready;
        public IReadOnlyList<PooledSubAgent> Entries { get; init; } = Array.Empty<PooledSubAgent>();

        /// <summary>条目声明的模板 id；null 表示「没有声明」。</summary>
        public string? DeclaredTemplateId { get; init; }

        /// <summary>工具交给池的那一单实际用的模板 id，供「继承 vs 显式覆盖」断言。</summary>
        public string? LastExecuteTemplateId { get; private set; }

        public int Count => Entries.Count;
        public int MaxCapacity => 4;
        public PoolReadiness Readiness => ReadinessValue;

        public Task<PooledSubAgent> CreateAsync(
            string name,
            SubAgentSpawnRequest request,
            CancellationToken ct = default)
        {
            if (CreateError is not null)
                throw CreateError;

            return Task.FromResult(Snapshot(name, request));
        }

        public Task<PooledSubAgent?> GetAsync(string name, CancellationToken ct = default)
            => Task.FromResult<PooledSubAgent?>(Snapshot(name, null));

        public Task<SubAgentExecuteResult> ExecuteAsync(
            string name,
            SubAgentSpawnRequest request,
            CancellationToken ct = default)
        {
            if (ExecuteError is not null)
                throw ExecuteError;

            LastExecuteTemplateId = request.TemplateId;

            return Task.FromResult(new SubAgentExecuteResult
            {
                SubSessionId = $"{request.ParentSessionId}-sub-{name}",
                RunId = $"run-{name}",
                Success = true,
                Status = "completed",
                Reply = "SUMMARY: noop",
            });
        }

        public Task<bool> SleepAsync(string name, CancellationToken ct = default) => Task.FromResult(SleepResult);

        public Task<string?> GetDeclaredTemplateIdAsync(string name, CancellationToken ct = default)
            => Task.FromResult(DeclaredTemplateId);

        /// <summary>归属受限重载：本假池不模拟归属，只记录「工具是否带了 scope」供断言用。</summary>
        public PoolOwnerScope? LastScope { get; private set; }

        public Task<bool> SleepAsync(string name, PoolOwnerScope scope, CancellationToken ct = default)
        {
            LastScope = scope;
            return Task.FromResult(SleepResult);
        }

        public Task<bool> DestroyAsync(string name, CancellationToken ct = default) => Task.FromResult(DestroyResult);

        public Task<bool> DestroyAsync(string name, PoolOwnerScope scope, CancellationToken ct = default)
        {
            LastScope = scope;
            return Task.FromResult(DestroyResult);
        }

        public IReadOnlyList<PooledSubAgent> List()
        {
            if (ListError is not null)
                throw ListError;

            return Entries;
        }

        public IReadOnlyList<PooledSubAgent> ListOwnedBy(PoolOwnerScope scope)
        {
            LastScope = scope;
            if (ListError is not null)
                throw ListError;

            if (scope.IsUnscoped)
                return Entries;

            return Entries
                .Where(e =>
                    (string.IsNullOrEmpty(scope.SessionId) || string.Equals(scope.SessionId, e.OwnerSessionId, StringComparison.Ordinal)) &&
                    (string.IsNullOrEmpty(scope.WorkspaceId) || string.Equals(scope.WorkspaceId, e.OwnerWorkspaceId, StringComparison.Ordinal)))
                .ToList();
        }

        public Task<string?> EvictLeastRecentlyUsedAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public async Task<IReadOnlyList<PooledSubAgent>> GetSnapshotAsync(CancellationToken ct = default)
            => await Task.FromResult(List());

        private static PooledSubAgent Snapshot(string name, SubAgentSpawnRequest? request) => new()
        {
            PoolId = $"poolid-{name}",
            Name = name,
            SubSessionId = $"{request?.ParentSessionId ?? "sess-parent"}-sub-{name}",
            TemplateId = request?.TemplateId ?? "workspace-task-agent",
            Role = request?.RoleInPlan,
            CreatedAt = DateTimeOffset.UnixEpoch,
            LastUsedAt = DateTimeOffset.UnixEpoch,
            Status = PooledSubAgentStatus.Idle,
            TaskCount = 0,
        };
    }

    private sealed class FakeLlmResolver : ILlmResolver
    {
        public Task<ResolvedLlmRoute> ResolveRouteAsync(
            string? modelRoute = null,
            IReadOnlyCollection<string>? requiredCapabilityTags = null,
            CancellationToken ct = default)
        {
            var parts = string.IsNullOrWhiteSpace(modelRoute)
                ? ["test", "test-model"]
                : modelRoute.Split('/', 2, StringSplitOptions.TrimEntries);
            var providerId = parts.Length == 2 ? parts[0] : "test";
            var modelId = parts.Length == 2 ? parts[1] : parts[0];

            return Task.FromResult(new ResolvedLlmRoute
            {
                ProviderId = providerId,
                ModelId = modelId,
                Config = new LlmConfig
                {
                    Endpoint = "https://example.invalid/v1",
                    ModelId = modelId,
                },
            });
        }
    }

    /// <summary>本组用例全部在进入真正派发之前就失败，派发服务只需要能解析出来。</summary>
    private sealed class NoopSubAgentInvocationService : ISubAgentInvocationService
    {
        public Task<SubAgentInvocationResult> InvokeAsync(
            SubAgentInvocationRequest request,
            CancellationToken ct = default)
            => Task.FromResult(new SubAgentInvocationResult
            {
                SubSessionId = "sess-parent-sub-noop",
                Status = "completed",
            });

        public Task<SubAgentBatchInvocationResult> InvokeBatchAsync(
            SubAgentBatchInvocationRequest request,
            CancellationToken ct = default)
            => Task.FromResult(new SubAgentBatchInvocationResult
            {
                BatchId = "batch-noop",
                Status = "completed",
                Results = [],
            });
    }
}
