using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class SubconsciousJobQueueTests
{
    [TestMethod]
    public async Task EnqueueAsync_ShouldReuseActiveJob_WhenIdempotencyKeyMatches()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        var request = new SubconsciousJobEnqueueRequest
        {
            JobType = SubconsciousJobTypes.MemoryConsolidateSession,
            IdempotencyKey = "memory:workspace-1:session-1:cmp-1",
            SourceHookName = "session.compressed",
            SourceEventId = "evt-1",
            SourceCompactionId = "cmp-1",
            Job = new ConsolidationJob
            {
                SessionId = "session-1",
                WorkspaceId = "workspace-1",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                LastAssistantReply = "summary",
            },
        };

        var first = await queue.EnqueueAsync(request);
        var second = await queue.EnqueueAsync(request with { SourceEventId = "evt-2" });

        Assert.AreEqual(first.JobId, second.JobId);
        await using var db = scope.Factory.CreateDbContext();
        Assert.AreEqual(1, await db.SubconsciousJobs.CountAsync());
    }

    [TestMethod]
    public async Task LeaseNextAsync_ShouldMarkPendingJobProcessing()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));

        var leased = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNotNull(leased);
        Assert.AreEqual("session-1", leased.Job.SessionId);
        Assert.AreEqual("processing", leased.Status);
        Assert.AreEqual("worker-1", leased.LeaseOwner);

        await using var db = scope.Factory.CreateDbContext();
        var entity = await db.SubconsciousJobs.SingleAsync();
        Assert.AreEqual("processing", entity.Status);
        Assert.AreEqual("worker-1", entity.LeaseOwner);
        Assert.IsNotNull(entity.LeaseUntil);
    }

    [TestMethod]
    public async Task RetryAsync_ShouldBackoffAndDeadLetterAfterMaxRetries()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        var enqueued = await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
        var leased = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));
        Assert.IsNotNull(leased);

        var retryStatus = await queue.RetryAsync(enqueued.JobId, "worker-1", "temporary failure", TimeSpan.FromSeconds(1));
        Assert.AreEqual("retrying", retryStatus);

        await using (var db = scope.Factory.CreateDbContext())
        {
            var entity = await db.SubconsciousJobs.SingleAsync();
            Assert.AreEqual(1, entity.RetryCount);
            Assert.AreEqual("retrying", entity.Status);
            Assert.IsNull(entity.LeaseOwner);
            Assert.IsNull(entity.LeaseUntil);
            Assert.IsTrue(entity.AvailableAt > entity.CreatedAt);
        }

        await ForceLeaseableAsync(scope.Factory, enqueued.JobId);
        _ = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));
        _ = await queue.RetryAsync(enqueued.JobId, "worker-1", "temporary failure", TimeSpan.Zero);
        await ForceLeaseableAsync(scope.Factory, enqueued.JobId);
        _ = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));
        var finalStatus = await queue.RetryAsync(enqueued.JobId, "worker-1", "final failure", TimeSpan.Zero);

        Assert.AreEqual("dead_letter", finalStatus);
    }

    [TestMethod]
    public async Task QueueTransitions_ShouldRecordRuntimeActivities()
    {
        await using var scope = await TestScope.CreateAsync();
        var sink = new RecordingRuntimeActivitySink();
        var queue = new SubconsciousJobQueue(
            scope.Factory,
            NullLogger<SubconsciousJobQueue>.Instance,
            sink);

        var enqueued = await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
        var leased = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));
        Assert.IsNotNull(leased);
        await queue.CompleteAsync(enqueued.JobId, "worker-1");

        var operations = sink.Activities.Select(a => a.Operation).ToArray();
        CollectionAssert.Contains(operations, "subconscious_job.enqueue");
        CollectionAssert.Contains(operations, "subconscious_job.lease");
        CollectionAssert.Contains(operations, "subconscious_job.complete");

        var enqueueActivity = sink.Activities.Single(a => a.Operation == "subconscious_job.enqueue");
        Assert.AreEqual(RuntimeActivityComponents.Memory, enqueueActivity.Component);
        Assert.AreEqual(RuntimeActivityStatuses.Succeeded, enqueueActivity.Status);
        Assert.IsNotNull(enqueueActivity.Metadata);
        Assert.AreEqual(enqueued.JobId, enqueueActivity.Metadata!["jobId"]);
        Assert.AreEqual(SubconsciousJobTypes.MemoryConsolidateSession, enqueueActivity.Metadata!["jobType"]);
        Assert.AreEqual("workspace-1", enqueueActivity.Metadata!["workspaceId"]);
        Assert.AreEqual("session-1", enqueueActivity.Metadata!["sessionId"]);
        Assert.AreEqual("session.compressed", enqueueActivity.Metadata!["sourceHookName"]);
        Assert.AreEqual("cmp-1", enqueueActivity.Metadata!["sourceCompactionId"]);
    }

    [TestMethod]
    public async Task QueueTransitions_ShouldRecordTelemetryMetrics()
    {
        await using var scope = await TestScope.CreateAsync();
        var telemetry = new RecordingTelemetryMetricSink();
        var queue = new SubconsciousJobQueue(
            scope.Factory,
            NullLogger<SubconsciousJobQueue>.Instance,
            telemetrySink: telemetry);

        var enqueued = await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
        var leased = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));
        Assert.IsNotNull(leased);
        await queue.CompleteAsync(enqueued.JobId, "worker-1");

        Assert.IsTrue(telemetry.Metrics.Any(m =>
            m.Category == TelemetryMetricCategories.Memory
            && m.Name == "subconscious_job.enqueue"
            && m.Status == TelemetryMetricStatuses.Succeeded));
        Assert.IsTrue(telemetry.Metrics.Any(m =>
            m.Category == TelemetryMetricCategories.Memory
            && m.Name == "subconscious_job.lease"
            && m.Status == TelemetryMetricStatuses.Started));
        Assert.IsTrue(telemetry.Metrics.Any(m =>
            m.Category == TelemetryMetricCategories.Memory
            && m.Name == "subconscious_job.complete"
            && m.Status == TelemetryMetricStatuses.Succeeded));

        var enqueueMetric = telemetry.Metrics.Single(m => m.Name == "subconscious_job.enqueue");
        Assert.AreEqual("workspace-1", enqueueMetric.Trace.WorkspaceId);
        Assert.AreEqual("session-1", enqueueMetric.Trace.SessionId);
        Assert.AreEqual(1, enqueueMetric.CountValue);
        Assert.AreEqual("job", enqueueMetric.Unit);
        Assert.IsNotNull(enqueueMetric.Dimensions);
        Assert.AreEqual(SubconsciousJobTypes.MemoryConsolidateSession, enqueueMetric.Dimensions!["job_type"]);
        Assert.AreEqual("session.compressed", enqueueMetric.Dimensions!["source_hook_name"]);
    }

    [TestMethod]
    public async Task LeaseNextAsync_ShouldSkipExcludedWorkspace()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
        await queue.EnqueueAsync(CreateRequest("session-2", "cmp-2") with
        {
            IdempotencyKey = "memory:workspace-2:session-2:cmp-2",
            Job = new ConsolidationJob
            {
                SessionId = "session-2",
                WorkspaceId = "workspace-2",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
            },
        });

        var leased = await queue.LeaseNextAsync(
            "worker-1",
            TimeSpan.FromMinutes(5),
            new SubconsciousJobLeaseQuery
            {
                ExcludedWorkspaceIds = new HashSet<string>(StringComparer.Ordinal) { "workspace-1" },
            });

        Assert.IsNotNull(leased);
        Assert.AreEqual("workspace-2", leased.Job.WorkspaceId);
    }

    [TestMethod]
    public async Task GetStatsAsync_ShouldCountProcessingByWorkspace()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
        await queue.EnqueueAsync(CreateRequest("session-2", "cmp-2"));
        _ = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        var stats = await queue.GetStatsAsync();

        Assert.AreEqual(1, stats.Pending);
        Assert.AreEqual(1, stats.Processing);
        Assert.AreEqual(1, stats.ProcessingByWorkspace["workspace-1"]);
    }

    [TestMethod]
    public async Task GetWorkspaceLeaseCountsAsync_ShouldCountStartedJobsSinceWindow()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        var oldJob = await queue.EnqueueAsync(CreateRequest("session-old", "cmp-old"));
        var recentJob1 = await queue.EnqueueAsync(CreateRequest("session-new-1", "cmp-new-1"));
        var recentJob2 = await queue.EnqueueAsync(CreateRequest("session-new-2", "cmp-new-2"));

        await using (var db = scope.Factory.CreateDbContext())
        {
            var oldEntity = await db.SubconsciousJobs.FindAsync(oldJob.JobId);
            var recentEntity1 = await db.SubconsciousJobs.FindAsync(recentJob1.JobId);
            var recentEntity2 = await db.SubconsciousJobs.FindAsync(recentJob2.JobId);
            var now = DateTimeOffset.UtcNow;
            oldEntity!.StartedAt = now.AddHours(-2).ToUnixTimeMilliseconds();
            recentEntity1!.StartedAt = now.AddMinutes(-10).ToUnixTimeMilliseconds();
            recentEntity2!.StartedAt = now.AddMinutes(-5).ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }

        var counts = await queue.GetWorkspaceLeaseCountsAsync(DateTimeOffset.UtcNow.AddHours(-1));

        Assert.AreEqual(2, counts["workspace-1"]);
    }

    [TestMethod]
    public async Task FindLatestAsync_ShouldReturnJobBySourceCompactionId()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));

        var found = await queue.FindLatestAsync(new SubconsciousJobLookupQuery
        {
            SourceHookName = "session.compressed",
            SourceCompactionId = "cmp-1",
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
        });

        Assert.IsNotNull(found);
        Assert.AreEqual("session-1", found.Job.SessionId);
        Assert.AreEqual("cmp-1", found.SourceCompactionId);
        Assert.AreEqual(SubconsciousJobTypes.MemoryConsolidateSession, found.JobType);
    }

    [TestMethod]
    public async Task RecordSchedulingSkipAsync_ShouldRecordTelemetryMetric()
    {
        await using var scope = await TestScope.CreateAsync();
        var telemetry = new RecordingTelemetryMetricSink();
        var queue = new SubconsciousJobQueue(
            scope.Factory,
            NullLogger<SubconsciousJobQueue>.Instance,
            telemetrySink: telemetry);

        await queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
        {
            Reason = SubconsciousSchedulingSkipReasons.Cooldown,
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentId = "agent-1",
            AgentTemplateId = "template-1",
            JobType = SubconsciousJobTypes.MemoryConsolidateSession,
        });

        var metric = telemetry.Metrics.Single(m => m.Name == "subconscious_job.schedule_skip");
        Assert.AreEqual(TelemetryMetricCategories.Memory, metric.Category);
        Assert.AreEqual(TelemetryMetricStatuses.Deferred, metric.Status);
        Assert.AreEqual("skip_cooldown", metric.Dimensions!["skip_reason"]);
        Assert.AreEqual("workspace-1", metric.Dimensions!["workspace_id"]);
    }

    [TestMethod]
    public async Task RecordResultAsync_ShouldPersistDryRunPlanEnvelope()
    {
        await using var scope = await TestScope.CreateAsync();
        var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
        var enqueued = await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
        _ = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        var envelope = new SubconsciousJobResultEnvelope
        {
            Schema = "pudding.subconscious_job_result.v1",
            Kind = SubconsciousJobResultKinds.MemoryMaintenancePlanDryRun,
            Status = SubconsciousJobResultStatuses.Accepted,
            PlanId = "plan-1",
            Valid = true,
            OperationCount = 1,
            ErrorCount = 0,
            Summary = "Dry-run memory maintenance plan accepted.",
            MemoryWriteResults =
            [
                new MemoryWriteResultEnvelope
                {
                    CommandId = "plan-1:op-1",
                    WorkspaceId = "workspace-1",
                    Status = MemoryWriteResultStatuses.DryRun,
                    Mode = MemoryWriteExecutionModes.DryRun,
                    Intent = MemoryWriteIntents.AppendNew,
                },
            ],
        };

        await queue.RecordResultAsync(enqueued.JobId, "worker-1", envelope);
        var result = await queue.GetResultAsync(enqueued.JobId);

        Assert.IsNotNull(result);
        Assert.AreEqual("pudding.subconscious_job_result.v1", result.Schema);
        Assert.AreEqual(SubconsciousJobResultKinds.MemoryMaintenancePlanDryRun, result.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Accepted, result.Status);
        Assert.AreEqual("plan-1", result.PlanId);
        Assert.IsTrue(result.Valid);
        Assert.AreEqual(1, result.MemoryWriteResults.Count);
        Assert.AreEqual("plan-1:op-1", result.MemoryWriteResults[0].CommandId);
        Assert.AreEqual(MemoryWriteResultStatuses.DryRun, result.MemoryWriteResults[0].Status);

        await using var db = scope.Factory.CreateDbContext();
        var entity = await db.SubconsciousJobs.SingleAsync(j => j.JobId == enqueued.JobId);
        Assert.IsNotNull(entity.ResultJson);
        StringAssert.Contains(entity.ResultJson!, "\"planId\":\"plan-1\"");
        StringAssert.Contains(entity.ResultJson!, "\"memoryWriteResults\"");
        Assert.AreEqual("processing", entity.Status);
    }

    private static SubconsciousJobEnqueueRequest CreateRequest(string sessionId, string compactionId) => new()
    {
        JobType = SubconsciousJobTypes.MemoryConsolidateSession,
        IdempotencyKey = $"memory:workspace-1:{sessionId}:{compactionId}",
        SourceHookName = "session.compressed",
        SourceEventId = $"evt-{compactionId}",
        SourceCompactionId = compactionId,
        Job = new ConsolidationJob
        {
            SessionId = sessionId,
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            AgentTemplateId = "template-1",
        },
    };

    private static async Task ForceLeaseableAsync(IDbContextFactory<MemoryDbContext> factory, string jobId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var entity = await db.SubconsciousJobs.SingleAsync(j => j.JobId == jobId);
        entity.AvailableAt = DateTimeOffset.UtcNow.AddMilliseconds(-1).ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestScope(SqliteConnection connection, IDbContextFactory<MemoryDbContext> factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public IDbContextFactory<MemoryDbContext> Factory { get; }

        public static async Task<TestScope> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestMemoryDbContextFactory(options);
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return new TestScope(connection, factory);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
    {
        public List<RuntimeActivity> Activities { get; } = [];

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
        {
            Activities.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
            RuntimeActivityQuery query,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Activities);
    }

    private sealed class RecordingTelemetryMetricSink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }
}
