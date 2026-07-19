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
        var pool = CreatePool();
        var result = await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        Assert.AreEqual("dev-agent", result.Name);
        Assert.AreEqual(PooledSubAgentStatus.Idle, result.Status);
        Assert.AreEqual(0, result.TaskCount);
        Assert.IsNull(result.LastSuccess);
        Assert.IsNotNull(result.SubSessionId);
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
        Assert.AreEqual(1, pool.Count); // Count 不减，条目标记 Dead 但仍在字典

        var snapshot = await pool.GetAsync("dev-agent");
        Assert.IsNull(snapshot); // GetAsync 不过滤 Dead，但 TryGetValue 返回 true...
        // 实际上 Count 不会减（ConcurrentDictionary 计数），但 List 中包含 Dead 条目
    }

    [TestMethod]
    public async Task DestroyAsync_ReturnsFalseForUnknown()
    {
        var pool = CreatePool();
        var result = await pool.DestroyAsync("nonexistent");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DestroyAsync_MarksAsDead()
    {
        var pool = CreatePool();
        await pool.CreateAsync("dev-agent", CreateSpawnRequest());
        await pool.DestroyAsync("dev-agent");

        var allAgents = pool.List();
        var deadAgent = allAgents.FirstOrDefault(a => a.Name == "dev-agent");
        Assert.IsNotNull(deadAgent);
        Assert.AreEqual(PooledSubAgentStatus.Dead, deadAgent!.Status);
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
}

// ═══════════════════════════════════════════════════════════
// Test doubles
// ═══════════════════════════════════════════════════════════

public sealed class TestSubAgentManager : ISubAgentManager
{
    public int DelayMs { get; set; }
    public bool ShouldFail { get; set; }
    private int _spawnCounter;

    public Task<SubAgentSpawnResult> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _spawnCounter);
        return Task.FromResult(new SubAgentSpawnResult
        {
            SubSessionId = $"pool-sub-{id}",
            Success = true,
        });
    }

    public async Task<SubAgentExecuteResult> ExecuteSyncAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
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
}
