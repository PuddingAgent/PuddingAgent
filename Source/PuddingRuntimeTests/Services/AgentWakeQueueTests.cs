using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentWakeQueueTests
{
    private static AgentWakeQueue CreateQueue() => new(NullLogger<AgentWakeQueue>.Instance);

    // ── 基础入队/出队 ──

    [TestMethod]
    public async Task EnqueueAndDequeue_SingleAgent()
    {
        var queue = CreateQueue();
        var agentId = "agent-1";
        var ct = CancellationToken.None;

        await queue.EnqueueAsync(agentId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), ct);
        Assert.AreEqual(1, await queue.CountAsync(ct));

        // 立即出队 — EarliestWakeAt 是 now+30s，所以不应出队
        var tooEarly = await queue.TryDequeueAsync(ct);
        Assert.IsNull(tooEarly);

        // 等待 31 秒后再试
        await Task.Delay(100); // 远小于30s，仍不应出队
        tooEarly = await queue.TryDequeueAsync(ct);
        Assert.IsNull(tooEarly);
    }

    [TestMethod]
    public async Task EnqueueAsync_ReplacesExistingAgent()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.EnqueueAsync("agent-1", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), ct);
        await queue.EnqueueAsync("agent-1", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), ct);

        // 同一 agent 只保留一条
        Assert.AreEqual(1, await queue.CountAsync(ct));
    }

    // ── EnsureDefaultAsync (P0: 多 Agent 默认心跳) ──

    [TestMethod]
    public async Task EnsureDefaultAsync_AddsAgentWhenQueueIsEmpty()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.EnsureDefaultAsync("agent-default", ct);

        Assert.AreEqual(1, await queue.CountAsync(ct));
        Assert.IsTrue(await queue.IsInQueueAsync("agent-default", ct));
    }

    [TestMethod]
    public async Task EnsureDefaultAsync_IdempotentForSameAgent()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.EnsureDefaultAsync("agent-1", ct);
        await queue.EnsureDefaultAsync("agent-1", ct);

        Assert.AreEqual(1, await queue.CountAsync(ct));
    }

    [TestMethod]
    public async Task EnsureDefaultAsync_MultipleAgents()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.EnsureDefaultAsync("agent-1", ct);
        await queue.EnsureDefaultAsync("agent-2", ct);
        await queue.EnsureDefaultAsync("agent-3", ct);

        Assert.AreEqual(3, await queue.CountAsync(ct));
    }

    [TestMethod]
    public async Task EnsureDefaultAsync_DoesNotOverrideCustomSleep()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        // Agent calls sleep with custom interval
        await queue.EnqueueAsync("agent-1", TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300), ct);

        // EnsureDefaultAsync should not override
        await queue.EnsureDefaultAsync("agent-1", ct);

        Assert.AreEqual(1, await queue.CountAsync(ct));
        var req = await queue.GetWakeRequestAsync("agent-1", ct);
        Assert.IsNotNull(req);
        Assert.AreEqual(TimeSpan.FromSeconds(120), req.MinIdle);
    }

    // ── EnqueueRetryAsync (P3: 指数退避重试) ──

    [TestMethod]
    public async Task EnqueueRetryAsync_EnqueuesWithBackoffDelay()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        Assert.AreEqual(0, await queue.CountAsync(ct));

        // First retry: 30s delay
        var result = await queue.EnqueueRetryAsync("agent-1", 0, ct);
        Assert.IsTrue(result);
        Assert.AreEqual(1, await queue.CountAsync(ct));

        var req = await queue.GetWakeRequestAsync("agent-1", ct);
        Assert.IsNotNull(req);
        Assert.AreEqual(TimeSpan.FromSeconds(30), req.MinIdle);
    }

    [TestMethod]
    public async Task EnqueueRetryAsync_SecondRetryUses60s()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        var result = await queue.EnqueueRetryAsync("agent-1", 1, ct);
        Assert.IsTrue(result);

        var req = await queue.GetWakeRequestAsync("agent-1", ct);
        Assert.IsNotNull(req);
        Assert.AreEqual(TimeSpan.FromSeconds(60), req.MinIdle);
    }

    [TestMethod]
    public async Task EnqueueRetryAsync_ThirdRetryUses120s()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        var result = await queue.EnqueueRetryAsync("agent-1", 2, ct);
        Assert.IsTrue(result);

        var req = await queue.GetWakeRequestAsync("agent-1", ct);
        Assert.IsNotNull(req);
        Assert.AreEqual(TimeSpan.FromSeconds(120), req.MinIdle);
    }

    [TestMethod]
    public async Task EnqueueRetryAsync_ExceedsMaxRetries_ReturnsFalse()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        var result = await queue.EnqueueRetryAsync("agent-1", 3, ct); // 3 = MaxRetryCount
        Assert.IsFalse(result);
        Assert.AreEqual(0, await queue.CountAsync(ct));
    }

    [TestMethod]
    public async Task EnqueueRetryAsync_BeyondMaxRetries_ReturnsFalse()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        var result = await queue.EnqueueRetryAsync("agent-1", 5, ct);
        Assert.IsFalse(result);
    }

    // ── ClearAsync ──

    [TestMethod]
    public async Task ClearAsync_RemovesAgentFromQueue()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.EnqueueAsync("agent-1", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), ct);
        Assert.AreEqual(1, await queue.CountAsync(ct));

        await queue.ClearAsync("agent-1", ct);
        Assert.AreEqual(0, await queue.CountAsync(ct));
    }

    [TestMethod]
    public async Task ClearAsync_NonExistentAgent_NoError()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.ClearAsync("ghost-agent", ct);
        Assert.AreEqual(0, await queue.CountAsync(ct));
    }

    // ── NotifyUserActivityAsync ──

    [TestMethod]
    public async Task NotifyUserActivityAsync_RemovesAgent()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.EnqueueAsync("agent-1", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), ct);
        await queue.NotifyUserActivityAsync("agent-1", ct);

        Assert.AreEqual(0, await queue.CountAsync(ct));
    }

    // ── RemoveAsync ──

    [TestMethod]
    public async Task RemoveAsync_RemovesAgent()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;

        await queue.EnqueueAsync("agent-1", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), ct);
        await queue.EnqueueAsync("agent-2", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), ct);
        Assert.AreEqual(2, await queue.CountAsync(ct));

        await queue.RemoveAsync("agent-1", ct);
        Assert.AreEqual(1, await queue.CountAsync(ct));
        Assert.IsFalse(await queue.IsInQueueAsync("agent-1", ct));
        Assert.IsTrue(await queue.IsInQueueAsync("agent-2", ct));
    }

    // ── IsInQueueAsync / GetWakeRequestAsync ──

    [TestMethod]
    public async Task IsInQueueAsync_ReturnsFalseForUnknownAgent()
    {
        var queue = CreateQueue();
        Assert.IsFalse(await queue.IsInQueueAsync("unknown", CancellationToken.None));
    }

    [TestMethod]
    public async Task GetWakeRequestAsync_ReturnsNullForUnknownAgent()
    {
        var queue = CreateQueue();
        Assert.IsNull(await queue.GetWakeRequestAsync("unknown", CancellationToken.None));
    }

    // ── 并发安全 ──

    [TestMethod]
    public async Task ConcurrentEnqueue_MultipleAgents_ThreadSafe()
    {
        var queue = CreateQueue();
        var ct = CancellationToken.None;
        var agents = Enumerable.Range(0, 10).Select(i => $"agent-{i}").ToList();

        var tasks = agents.Select(a =>
            queue.EnqueueAsync(a, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), ct));
        await Task.WhenAll(tasks);

        Assert.AreEqual(10, await queue.CountAsync(ct));
    }
}
