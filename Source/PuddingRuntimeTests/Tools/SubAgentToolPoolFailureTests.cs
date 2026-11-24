using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
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
                    Name = "p1",
                    SubSessionId = "sess-parent-sub-p1",
                    TemplateId = "workspace-task-agent",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    LastUsedAt = DateTimeOffset.UnixEpoch,
                    Status = PooledSubAgentStatus.Idle,
                    TaskCount = 0,
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

        public Task<bool> DestroyAsync(string name, CancellationToken ct = default) => Task.FromResult(DestroyResult);

        public IReadOnlyList<PooledSubAgent> List()
        {
            if (ListError is not null)
                throw ListError;

            return Entries;
        }

        public Task<string?> EvictLeastRecentlyUsedAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public async Task<IReadOnlyList<PooledSubAgent>> GetSnapshotAsync(CancellationToken ct = default)
            => await Task.FromResult(List());

        private static PooledSubAgent Snapshot(string name, SubAgentSpawnRequest? request) => new()
        {
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
