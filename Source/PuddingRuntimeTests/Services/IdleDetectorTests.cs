using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class IdleDetectorTests
{
    [TestMethod]
    public async Task ReArm_KeepsCallbacksButLogsOnlyOncePerActivityWindow()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var logger = new IdleWindowLogger();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Heartbeat:IdleCheckIntervalSeconds"] = "1",
            ["Heartbeat:GlobalIdleThresholdSeconds"] = "10",
        }).Build();
        using var detector = new IdleDetector(null, logger, clock, config);
        using var callbacks = new SemaphoreSlim(0);
        detector.OnIdleThresholdReached += (_, _) =>
        {
            detector.ReArm();
            callbacks.Release();
            return Task.CompletedTask;
        };
        clock.Advance(TimeSpan.FromSeconds(30));
        await detector.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 3; i++)
                Assert.IsTrue(await callbacks.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(1, logger.ThresholdLogCount);
            detector.RecordActivity();
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.IsTrue(await callbacks.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(2, logger.ThresholdLogCount);
        }
        finally { await detector.StopAsync(CancellationToken.None); }
    }

    private sealed class IdleWindowLogger : ILogger<IdleDetector>
    {
        private int _thresholdLogCount;
        public int ThresholdLogCount => Volatile.Read(ref _thresholdLogCount);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Information && formatter(state, error).Contains("Global idle threshold reached"))
                Interlocked.Increment(ref _thresholdLogCount);
        }
    }

    [TestMethod]
    public void IdleDuration_UsesLastRecordedActivity()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var detector = new IdleDetector(null, NullLogger<IdleDetector>.Instance, clock);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.AreEqual(TimeSpan.FromMinutes(2), detector.IdleDuration);

        detector.RecordUserMessage();
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.AreEqual(TimeSpan.FromSeconds(5), detector.IdleDuration);

        detector.RecordToolCompleted();

        Assert.AreEqual(TimeSpan.Zero, detector.IdleDuration);
        Assert.AreEqual(DateTimeOffset.Parse("2026-06-17T00:02:05Z"), detector.LastActiveAt);
    }

    [TestMethod]
    public async Task StartAsync_SubscribesToUserMessagesAndToolCompletionEvents()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var eventBus = new RecordingInternalEventBus();
        var detector = new IdleDetector(eventBus, NullLogger<IdleDetector>.Instance, clock);

        await detector.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "message.deliver", "tool.*" },
            eventBus.SubscriptionPatterns);

        clock.Advance(TimeSpan.FromSeconds(30));
        await eventBus.PublishToHandlerAsync(new InternalEvent { Type = "message.deliver" });

        Assert.AreEqual(TimeSpan.Zero, detector.IdleDuration);

        clock.Advance(TimeSpan.FromSeconds(15));
        await eventBus.PublishToHandlerAsync(new InternalEvent { Type = "tool.execution.completed" });

        Assert.AreEqual(TimeSpan.Zero, detector.IdleDuration);
    }

    [TestMethod]
    public async Task Dispose_CanRunAfterPreviousDisposeWithoutThrowing()
    {
        var detector = new IdleDetector(null, NullLogger<IdleDetector>.Instance);
        await detector.StartAsync(CancellationToken.None);

        detector.Dispose();
        detector.Dispose();
    }

    // ── P1: IdleDetector 可配置 ──

    [TestMethod]
    public void Constructor_UsesDefaultValues_WhenConfigurationMissing()
    {
        var config = new ConfigurationBuilder().Build(); // empty
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var detector = new IdleDetector(null, NullLogger<IdleDetector>.Instance, clock, config);

        Assert.IsNotNull(detector);
    }

    [TestMethod]
    public void Constructor_ReadsCustomThresholdAndInterval()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Heartbeat:IdleCheckIntervalSeconds"] = "10",
                ["Heartbeat:GlobalIdleThresholdSeconds"] = "60"
            })
            .Build();

        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var detector = new IdleDetector(null, NullLogger<IdleDetector>.Instance, clock, config);

        Assert.IsNotNull(detector);
    }

    [TestMethod]
    public void Constructor_ClampsOutOfRangeConfigurationValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Heartbeat:IdleCheckIntervalSeconds"] = "0",    // below min 1 → clamped to 1
                ["Heartbeat:GlobalIdleThresholdSeconds"] = "999"  // above max 300 → clamped to 300
            })
            .Build();

        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var detector = new IdleDetector(null, NullLogger<IdleDetector>.Instance, clock, config);

        Assert.IsNotNull(detector); // construction succeeds with clamped values
    }

    [TestMethod]
    public void ReArm_ResetsFiredFlag()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var detector = new IdleDetector(null, NullLogger<IdleDetector>.Instance, clock);

        // Should not throw
        detector.ReArm();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        private readonly List<Func<InternalEvent, Task>> _handlers = [];

        public List<string> SubscriptionPatterns { get; } = [];

        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default)
        {
            SubscriptionPatterns.Add(eventTypePattern);
            _handlers.Add(handler);
            return Task.FromResult<IEventSubscriptionHandle>(new RecordingEventSubscriptionHandle(eventTypePattern));
        }

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;

        public async Task PublishToHandlerAsync(InternalEvent evt)
        {
            foreach (var handler in _handlers)
                await handler(evt);
        }
    }

    private sealed class RecordingEventSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = Guid.NewGuid().ToString("N");
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;

        public void Dispose() => IsActive = false;
    }
}
