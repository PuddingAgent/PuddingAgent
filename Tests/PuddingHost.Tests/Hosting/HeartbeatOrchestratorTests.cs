using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Services;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingRuntime;
using PuddingRuntime.Services;

namespace PuddingHost.Tests.Hosting;

public sealed class HeartbeatOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "heartbeat-host-" + Guid.NewGuid().ToString("N"));
    private readonly Catalog _catalog = new();
    private readonly Idle _idle = new();
    private readonly AgentWakeQueue _queue = new(NullLogger<AgentWakeQueue>.Instance);
    private readonly Clock _clock = new();

    private (HeartbeatOrchestrator Service, ServiceProvider Provider) Create()
    {
        var provider = new ServiceCollection().AddSingleton<IWorkspaceAgentCatalog>(_catalog).BuildServiceProvider();
        return (new HeartbeatOrchestrator(_idle, _queue, provider.GetRequiredService<IServiceScopeFactory>(),
            PuddingDataPaths.FromRoot(_root), NullLogger<HeartbeatOrchestrator>.Instance, new ConfigurationBuilder().Build(), _clock), provider);
    }

    [Fact]
    public async Task IdleTicks_DoNotRescanCatalogEveryFiveSeconds()
    {
        _catalog.Agents.Add(Agent("a"));
        var (service, provider) = Create();
        using (provider)
        {
            await service.StartAsync(default);
            _catalog.Reads = 0;
            for (var i = 0; i < 11; i++)
            {
                _clock.Now = _clock.Now.AddSeconds(5);
                await _idle.Tick();
            }
            Assert.Equal(0, _catalog.Reads);
            Assert.Equal(1, await _queue.CountAsync());
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task Start_RegistersAllEligibleAgents_AndHonorsHeartbeatOptOut()
    {
        _catalog.Agents.AddRange([Agent("a"), Agent("b"), Agent("off"),
            Agent("disabled") with { IsEnabled = false }, Agent("frozen") with { IsFrozen = true },
            Agent("unbound") with { MainSessionId = null }]);
        var root = PuddingDataPaths.FromRoot(_root).AgentInstanceRoot("off");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "heartbeat.json"),
            "{\"enabled\":false,\"min_idle_seconds\":3600,\"max_idle_seconds\":7200}");
        var (service, provider) = Create();
        using (provider)
        {
            await service.StartAsync(default);
            Assert.Equal(2, await _queue.CountAsync());
            Assert.True(await _queue.IsInQueueAsync("a"));
            Assert.True(await _queue.IsInQueueAsync("b"));
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task EmptyCatalog_KeepsIdleDetectorArmedForAgentsCreatedLater()
    {
        var (service, provider) = Create();
        using (provider)
        {
            await service.StartAsync(default);
            await _idle.Tick();
            Assert.True(_idle.RearmCount > 0);
            _catalog.Agents.Add(Agent("new"));
            _clock.Now = _clock.Now.AddMinutes(1);
            await _idle.Tick();
            Assert.True(await _queue.IsInQueueAsync("new"));
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task Reconciliation_RemovesDisabledAndDeletedAgentsWithoutResettingOtherDeadlines()
    {
        _catalog.Agents.AddRange([Agent("a"), Agent("b"), Agent("c")]);
        var (service, provider) = Create();
        using (provider)
        {
            await service.StartAsync(default);
            var original = await _queue.GetWakeRequestAsync("a");
            _catalog.Agents[1] = Agent("b") with { IsEnabled = false };
            _catalog.Agents.RemoveAt(2);
            _clock.Now = _clock.Now.AddMinutes(1);
            await _idle.Tick();
            Assert.Equal(1, await _queue.CountAsync());
            Assert.Equal(original!.EarliestWakeAt, (await _queue.GetWakeRequestAsync("a"))!.EarliestWakeAt);
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task InvalidPreference_DoesNotEnableAgentOrPreventOthersFromRegistering()
    {
        _catalog.Agents.AddRange([Agent("broken"), Agent("healthy")]);
        var root = PuddingDataPaths.FromRoot(_root).AgentInstanceRoot("broken");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "heartbeat.json"), "{broken");
        var (service, provider) = Create();
        using (provider)
        {
            await service.StartAsync(default);
            Assert.False(await _queue.IsInQueueAsync("broken"));
            Assert.True(await _queue.IsInQueueAsync("healthy"));
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task HeartbeatOptOut_RemovesExistingRegistrationOnReconciliation()
    {
        _catalog.Agents.Add(Agent("a"));
        var (service, provider) = Create();
        using (provider)
        {
            await service.StartAsync(default);
            var root = PuddingDataPaths.FromRoot(_root).AgentInstanceRoot("a");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "heartbeat.json"), "{\"enabled\":false}");
            _clock.Now = _clock.Now.AddMinutes(1);
            await _idle.Tick();
            Assert.Equal(0, await _queue.CountAsync());
            await service.StopAsync(default);
        }
    }

    private static WorkspaceAgentDto Agent(string id) => new(id, id, null, null, null, null, null,
        "session-" + id, null, null, null, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Catalog : IWorkspaceAgentCatalog
    {
        public List<WorkspaceAgentDto> Agents { get; } = [];
        public int Reads;
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>(Agents.ToArray());
        }
    }

    private sealed class Idle : IIdleDetector
    {
        public DateTimeOffset LastActiveAt => DateTimeOffset.UtcNow.AddHours(-2);
        public TimeSpan IdleDuration => TimeSpan.FromHours(2);
        public int RearmCount;
        public event Func<TimeSpan, CancellationToken, Task>? OnIdleThresholdReached;
        public Task Tick() => OnIdleThresholdReached?.Invoke(IdleDuration, default) ?? Task.CompletedTask;
        public void ReArm() => RearmCount++;
        public void RecordActivity() { }
        public void RecordUserMessage() { }
        public void RecordToolCompleted() { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
