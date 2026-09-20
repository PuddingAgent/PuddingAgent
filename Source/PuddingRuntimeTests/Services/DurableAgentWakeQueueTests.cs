using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingRuntime.Services;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class DurableAgentWakeQueueTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "heartbeat-test-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();

    private ServiceProvider CreateHost() => new ServiceCollection()
        .AddSingleton(PuddingDataPaths.FromRoot(_root))
        .AddSingleton<TimeProvider>(_clock)
        .AddSingleton<ILogger<AgentWakeQueue>>(NullLogger<AgentWakeQueue>.Instance)
        .AddSingleton<AgentWakeQueue>()
        .BuildServiceProvider();

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task RemoveUnknownAgent_DoesNotRequireExistingStateDirectory()
    {
        using var host = CreateHost();
        await host.GetRequiredService<AgentWakeQueue>().RemoveAsync("never-scheduled");
    }

    [TestMethod]
    public async Task Restart_PreservesDefaultDeadline()
    {
        using var first = CreateHost();
        var queue = first.GetRequiredService<AgentWakeQueue>();
        await queue.EnsureDefaultAsync("agent-a");
        var before = await queue.GetWakeRequestAsync("agent-a");

        _clock.Now = _clock.Now.AddMinutes(25);

        using var second = CreateHost();
        var restored = second.GetRequiredService<AgentWakeQueue>();
        await restored.EnsureDefaultAsync("agent-a");
        var after = await restored.GetWakeRequestAsync("agent-a");

        Assert.AreEqual(before!.EarliestWakeAt, after!.EarliestWakeAt);
        Assert.AreEqual(before.LatestWakeAt, after.LatestWakeAt);
    }

    [TestMethod]
    public async Task Restart_OverdueCustomSchedule_IsReadyOnlyOnce()
    {
        using var first = CreateHost();
        await first.GetRequiredService<AgentWakeQueue>().EnqueueAsync(
            "agent-a", TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(-1));

        using var second = CreateHost();
        var restored = second.GetRequiredService<AgentWakeQueue>();
        await restored.EnsureDefaultAsync("agent-a");
        Assert.IsNotNull(await restored.TryDequeueAsync());
        Assert.IsNull(await restored.TryDequeueAsync());
    }

    [TestMethod]
    public async Task Retry_ReplacesExistingRegistrationInsteadOfDuplicatingIt()
    {
        using var host = CreateHost();
        var queue = host.GetRequiredService<AgentWakeQueue>();
        await queue.EnsureDefaultAsync("agent-a");
        await queue.EnqueueRetryAsync("agent-a", 0);
        Assert.AreEqual(1, await queue.CountAsync());
    }

    [TestMethod]
    public async Task DeliveredCycle_DoesNotReplayOldDeadlineOnRestart()
    {
        using var first = CreateHost();
        var queue = first.GetRequiredService<AgentWakeQueue>();
        await queue.EnqueueAsync("agent-a", TimeSpan.Zero, TimeSpan.Zero);
        Assert.IsNotNull(await queue.TryDequeueAsync());
        await queue.ScheduleNextAsync("agent-a", TimeSpan.FromHours(1), TimeSpan.FromHours(2));
        using var second = CreateHost();
        var restored = second.GetRequiredService<AgentWakeQueue>();
        await restored.EnsureDefaultAsync("agent-a");
        Assert.IsNull(await restored.TryDequeueAsync());
        Assert.AreEqual(_clock.Now.UtcDateTime.AddHours(1), (await restored.GetWakeRequestAsync("agent-a"))!.EarliestWakeAt);
    }

    [TestMethod]
    public async Task NextCycle_DoesNotOverwriteConcurrentSleep()
    {
        using var host = CreateHost();
        var queue = host.GetRequiredService<AgentWakeQueue>();
        await queue.EnqueueAsync("agent-a", TimeSpan.FromHours(3), TimeSpan.FromHours(4));
        await queue.ScheduleNextAsync("agent-a", TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        Assert.AreEqual(TimeSpan.FromHours(3), (await queue.GetWakeRequestAsync("agent-a"))!.MinIdle);
    }

    [TestMethod]
    public async Task UserActivity_DiscardsOldDeadlineAcrossRestart()
    {
        using var first = CreateHost();
        var queue = first.GetRequiredService<AgentWakeQueue>();
        await queue.EnqueueAsync("agent-a", TimeSpan.Zero, TimeSpan.Zero);
        await queue.NotifyUserActivityAsync("agent-a");
        using var second = CreateHost();
        var restored = second.GetRequiredService<AgentWakeQueue>();
        await restored.EnsureDefaultAsync("agent-a");
        Assert.IsNull(await restored.TryDequeueAsync());
    }

    [TestMethod]
    public async Task DequeuedButInterrupted_RemainsRecoverableAfterRestart()
    {
        using var first = CreateHost();
        var queue = first.GetRequiredService<AgentWakeQueue>();
        await queue.EnqueueAsync("agent-a", TimeSpan.Zero, TimeSpan.Zero);
        Assert.IsNotNull(await queue.TryDequeueAsync());
        using var second = CreateHost();
        var restored = second.GetRequiredService<AgentWakeQueue>();
        await restored.EnsureDefaultAsync("agent-a");
        Assert.IsNotNull(await restored.TryDequeueAsync());
        Assert.IsNull(await restored.TryDequeueAsync());
    }

    [TestMethod]
    public async Task Sleep_DoesNotReenableExplicitHeartbeatOptOut()
    {
        var paths = PuddingDataPaths.FromRoot(_root);
        var directory = paths.AgentInstanceRoot("agent-a");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "heartbeat.json");
        const string disabled = "{\"enabled\":false,\"min_idle_seconds\":3600}";
        await File.WriteAllTextAsync(path, disabled);
        using var host = CreateHost();
        var queue = host.GetRequiredService<AgentWakeQueue>();
        var tool = new AgentSleepTool(queue, paths, NullLogger<AgentSleepTool>.Instance);
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "sleep-opt-out",
            ArgumentsJson = "{\"min_idle_seconds\":60}",
            Context = new ToolExecutionContext { WorkspaceId = "default", SessionId = "test", AgentInstanceId = "agent-a" },
        });
        Assert.IsFalse(result.Success);
        Assert.AreEqual(disabled, await File.ReadAllTextAsync(path));
        Assert.AreEqual(0, await queue.CountAsync());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
