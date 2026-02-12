using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime;
using PuddingRuntime.Services.Background;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousJobSchedulerTests
{
    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldSkip_WhenCooldownHasNotElapsed()
    {
        var idle = new FakeIdleDetector(TimeSpan.FromSeconds(5));
        var queue = new FakeQueue();
        var scheduler = CreateScheduler(
            queue,
            idle,
            new SubconsciousSchedulingOptions
            {
                Enabled = true,
                IdleCooldownSeconds = 60,
            });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNull(result);
        Assert.AreEqual(SubconsciousSchedulingSkipReasons.Cooldown, queue.Skips.Single().Reason);
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldReturnNullAndRecordWouldLease_WhenDryRun()
    {
        var queue = new FakeQueue
        {
            Stats = new SubconsciousJobQueueStats
            {
                Pending = 1,
            },
            Next = SampleJob("job-1", "workspace-1", "session-1"),
        };
        var scheduler = CreateScheduler(
            queue,
            new FakeIdleDetector(TimeSpan.FromMinutes(5)),
            new SubconsciousSchedulingOptions
            {
                Enabled = true,
                DryRun = true,
                IdleCooldownSeconds = 1,
            });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNull(result);
        Assert.AreEqual(SubconsciousSchedulingSkipReasons.DryRun, queue.Skips.Single().Reason);
        Assert.AreEqual(0, queue.LeaseCalls);
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldRecordNoEligibleJob_WhenDryRunAndQueueEmpty()
    {
        var queue = new FakeQueue();
        var scheduler = CreateScheduler(
            queue,
            new FakeIdleDetector(TimeSpan.FromMinutes(5)),
            new SubconsciousSchedulingOptions
            {
                Enabled = true,
                DryRun = true,
                IdleCooldownSeconds = 1,
            });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNull(result);
        Assert.AreEqual(SubconsciousSchedulingSkipReasons.NoEligibleJob, queue.Skips.Single().Reason);
        Assert.AreEqual(0, queue.LeaseCalls);
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldLease_WhenIdleAndWithinLimits()
    {
        var queue = new FakeQueue
        {
            Next = SampleJob("job-1", "workspace-1", "session-1"),
        };
        var scheduler = CreateScheduler(
            queue,
            new FakeIdleDetector(TimeSpan.FromMinutes(5)),
            new SubconsciousSchedulingOptions
            {
                Enabled = true,
                IdleCooldownSeconds = 1,
            });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNotNull(result);
        Assert.AreEqual("job-1", result.JobId);
        Assert.AreEqual(1, queue.LeaseCalls);
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldNotLease_WhenSchedulingDisabled()
    {
        var queue = new FakeQueue
        {
            Next = SampleJob("job-1", "workspace-1", "session-1"),
        };
        var scheduler = CreateScheduler(
            queue,
            new FakeIdleDetector(TimeSpan.FromMinutes(5)),
            new SubconsciousSchedulingOptions
            {
                Enabled = false,
            });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNull(result);
        Assert.AreEqual(0, queue.LeaseCalls);
        Assert.AreEqual(SubconsciousSchedulingSkipReasons.Disabled, queue.Skips.Single().Reason);
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldExcludeWorkspace_WhenWorkspaceLimitReached()
    {
        var queue = new FakeQueue
        {
            Stats = new SubconsciousJobQueueStats
            {
                Processing = 1,
                ProcessingByWorkspace = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["workspace-1"] = 1,
                },
            },
            Next = SampleJob("job-2", "workspace-2", "session-2"),
        };
        var scheduler = CreateScheduler(
            queue,
            new FakeIdleDetector(TimeSpan.FromMinutes(5)),
            new SubconsciousSchedulingOptions
            {
                Enabled = true,
                IdleCooldownSeconds = 1,
                MaxGlobalConcurrentJobs = 2,
                MaxWorkspaceConcurrentJobs = 1,
            });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNotNull(result);
        Assert.AreEqual("workspace-2", result.Job.WorkspaceId);
        CollectionAssert.Contains(queue.LastQuery!.ExcludedWorkspaceIds.ToArray(), "workspace-1");
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldExcludeWorkspace_WhenBudgetExhausted()
    {
        var queue = new FakeQueue
        {
            Stats = new SubconsciousJobQueueStats
            {
                Pending = 2,
            },
            WorkspaceLeaseCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["workspace-1"] = 2,
            },
            Next = SampleJob("job-2", "workspace-2", "session-2"),
        };
        var scheduler = CreateScheduler(
            queue,
            new FakeIdleDetector(TimeSpan.FromMinutes(5)),
            new SubconsciousSchedulingOptions
            {
                Enabled = true,
                IdleCooldownSeconds = 1,
                MaxJobsPerWorkspacePerHour = 2,
                BudgetWindowMinutes = 60,
            });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNotNull(result);
        Assert.AreEqual("workspace-2", result.Job.WorkspaceId);
        CollectionAssert.Contains(queue.LastQuery!.ExcludedWorkspaceIds.ToArray(), "workspace-1");
    }

    private static SubconsciousJobScheduler CreateScheduler(
        FakeQueue queue,
        IIdleDetector idleDetector,
        SubconsciousSchedulingOptions options) =>
        new(
            queue,
            Options.Create(new SubconsciousOptions { Scheduling = options }),
            NullLogger<SubconsciousJobScheduler>.Instance,
            idleDetector);

    private static SubconsciousJobQueueItem SampleJob(
        string jobId,
        string workspaceId,
        string sessionId) =>
        new()
        {
            JobId = jobId,
            JobType = SubconsciousJobTypes.MemoryConsolidateSession,
            IdempotencyKey = $"memory:{workspaceId}:{sessionId}:cmp-1",
            Status = "processing",
            Job = new ConsolidationJob
            {
                SessionId = sessionId,
                WorkspaceId = workspaceId,
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
            },
        };

    private sealed class FakeIdleDetector(TimeSpan idleDuration) : IIdleDetector
    {
        public DateTimeOffset LastActiveAt => DateTimeOffset.UtcNow - idleDuration;
        public TimeSpan IdleDuration => idleDuration;
        public event Func<TimeSpan, CancellationToken, Task>? OnIdleThresholdReached;
        public void RecordUserMessage() { }
        public void RecordToolCompleted() { }
        public void RecordActivity() { }
        public void ReArm() { }
    }

    private sealed class FakeQueue : ISubconsciousJobQueue
    {
        public int LeaseCalls { get; private set; }
        public SubconsciousJobQueueItem? Next { get; init; }
        public SubconsciousJobLeaseQuery? LastQuery { get; private set; }
        public SubconsciousJobQueueStats Stats { get; init; } = new();
        public IReadOnlyDictionary<string, int> WorkspaceLeaseCounts { get; init; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public List<SubconsciousSchedulingSkipRequest> Skips { get; } = [];

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
            => Task.FromResult(SampleJob("job-1", request.Job.WorkspaceId, request.Job.SessionId));

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
        {
            LeaseCalls++;
            LastQuery = query;
            return Task.FromResult(Next);
        }

        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(Stats);

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query,
            CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobQueueItem?>(null);

        public Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
            DateTimeOffset since,
            CancellationToken ct = default)
            => Task.FromResult(WorkspaceLeaseCounts);

        public Task RecordSchedulingSkipAsync(
            SubconsciousSchedulingSkipRequest request,
            CancellationToken ct = default)
        {
            Skips.Add(request);
            return Task.CompletedTask;
        }

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
