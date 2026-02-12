using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingRuntime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class RuntimeServiceExtensionsTests
{
    [TestMethod]
    public void AddPuddingRuntime_DoesNotRegisterLegacySubconsciousHookByDefault()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPuddingRuntime()
            .BuildServiceProvider();

        var hooks = provider.GetServices<IAgentLoopHook>().ToList();

        Assert.IsFalse(hooks.Any(h => h is SubconsciousConsolidationHook));
    }

    [TestMethod]
    public void AddPuddingRuntime_CanEnableLegacySubconsciousHookForCompatibility()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subconscious:EnableLegacyConsolidationHook"] = "true",
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPuddingRuntime(configuration)
            .BuildServiceProvider();

        var hooks = provider.GetServices<IAgentLoopHook>().ToList();

        Assert.IsTrue(hooks.Any(h => h is SubconsciousConsolidationHook));
    }

    [TestMethod]
    public void AddPuddingRuntime_BindsSubconsciousSchedulingOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subconscious:Scheduling:Enabled"] = "true",
                ["Subconscious:Scheduling:DryRun"] = "true",
                ["Subconscious:Scheduling:IdleCooldownSeconds"] = "15",
                ["Subconscious:Scheduling:MaxWorkspaceConcurrentJobs"] = "2",
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPuddingRuntime(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SubconsciousOptions>>().Value;

        Assert.IsTrue(options.Scheduling.Enabled);
        Assert.IsTrue(options.Scheduling.DryRun);
        Assert.AreEqual(15, options.Scheduling.IdleCooldownSeconds);
        Assert.AreEqual(2, options.Scheduling.MaxWorkspaceConcurrentJobs);
    }

    [TestMethod]
    public void AddPuddingRuntime_RegistersSubconsciousJobScheduler()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ISubconsciousJobQueue, FakeSubconsciousJobQueue>()
            .AddPuddingRuntime()
            .BuildServiceProvider();

        var scheduler = provider.GetService<SubconsciousJobScheduler>();

        Assert.IsNotNull(scheduler);
    }

    [TestMethod]
    public void AddPuddingRuntime_RegistersSubconsciousPlanGenerationService()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ISubconsciousJobQueue, FakeSubconsciousJobQueue>()
            .AddPuddingRuntime();

        Assert.IsTrue(services.Any(descriptor =>
            descriptor.ServiceType == typeof(SubconsciousPlanGenerationService)));
    }

    private sealed class FakeSubconsciousJobQueue : ISubconsciousJobQueue
    {
        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
            => Task.FromResult(new SubconsciousJobQueueItem
            {
                JobId = "job-1",
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
                Job = request.Job,
            });

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobQueueItem?>(null);

        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new SubconsciousJobQueueStats());

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query,
            CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobQueueItem?>(null);

        public Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
            DateTimeOffset since,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(
                new Dictionary<string, int>(StringComparer.Ordinal));

        public Task RecordSchedulingSkipAsync(
            SubconsciousSchedulingSkipRequest request,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RecordResultAsync(
            string jobId,
            string leaseOwner,
            SubconsciousJobResultEnvelope result,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId,
            CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobResultEnvelope?>(null);

        public Task CompleteAsync(string jobId, string leaseOwner, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> RetryAsync(
            string jobId,
            string leaseOwner,
            string error,
            TimeSpan? retryDelay = null,
            CancellationToken ct = default)
            => Task.FromResult("retrying");

        public Task DeadLetterAsync(
            string jobId,
            string leaseOwner,
            string error,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
