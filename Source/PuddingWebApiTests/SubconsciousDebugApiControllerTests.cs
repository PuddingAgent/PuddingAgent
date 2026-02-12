using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Controllers.Api;

namespace PuddingWebApiTests;

[TestClass]
public sealed class SubconsciousDebugApiControllerTests
{
    [TestMethod]
    public async Task GetDebug_ShouldReturnRuntimeControlSnapshot()
    {
        var control = new RecordingRuntimeControl();
        var controller = CreateController(control);

        var response = await controller.GetDebug(CancellationToken.None);

        var ok = response.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var snapshot = ok.Value as SubconsciousRuntimeControlSnapshot;
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(SubconsciousRuntimeStates.Running, snapshot.State);
        Assert.AreEqual(1, control.DebugCalls);
    }

    [TestMethod]
    public async Task Stop_ShouldForwardReasonAndRequestedBy()
    {
        var control = new RecordingRuntimeControl();
        var controller = CreateController(control);

        await controller.Stop(
            new SubconsciousRuntimeControlRequest
            {
                Reason = "manual debug",
                RequestedBy = "tester",
            },
            CancellationToken.None);

        Assert.AreEqual("manual debug", control.LastStopRequest?.Reason);
        Assert.AreEqual("tester", control.LastStopRequest?.RequestedBy);
    }

    [TestMethod]
    public async Task Start_ShouldForwardReasonAndRequestedBy()
    {
        var control = new RecordingRuntimeControl();
        var controller = CreateController(control);

        await controller.Start(
            new SubconsciousRuntimeControlRequest
            {
                Reason = "resume debug",
                RequestedBy = "tester",
            },
            CancellationToken.None);

        Assert.AreEqual("resume debug", control.LastStartRequest?.Reason);
        Assert.AreEqual("tester", control.LastStartRequest?.RequestedBy);
    }

    [TestMethod]
    public async Task Trigger_ShouldEnqueueDebugConsolidationJob()
    {
        var queue = new RecordingSubconsciousJobQueue();
        var controller = CreateController(new RecordingRuntimeControl(), queue: queue);

        var response = await controller.Trigger(
            new SubconsciousDebugTriggerRequest
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                LastUserMessage = "User prefers ADR first.",
                LastAssistantReply = "I will write design first.",
                SourceEventId = "event-1",
                SourceCompactionId = "compaction-1",
            },
            CancellationToken.None);

        var ok = response.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var payload = ok.Value as SubconsciousDebugTriggerResponse;
        Assert.IsNotNull(payload);
        Assert.AreEqual("debug-job-1", payload.JobId);
        Assert.AreEqual("pending", payload.Status);
        Assert.AreEqual(SubconsciousJobTypes.MemoryConsolidateSession, queue.LastRequest?.JobType);
        Assert.AreEqual("debug.subconscious.trigger", queue.LastRequest?.SourceHookName);
        Assert.AreEqual("event-1", queue.LastRequest?.SourceEventId);
        Assert.AreEqual("compaction-1", queue.LastRequest?.SourceCompactionId);
        Assert.AreEqual("session-1", queue.LastRequest?.Job.SessionId);
        Assert.AreEqual("agent-1", queue.LastRequest?.Job.AgentId);
        Assert.AreEqual("User prefers ADR first.", queue.LastRequest?.Job.LastUserMessage);
    }

    [TestMethod]
    public async Task Trigger_ShouldReturnNotFoundWhenDebugApiDisabled()
    {
        var controller = CreateController(
            new RecordingRuntimeControl(),
            debugApiEnabled: false);

        var response = await controller.Trigger(
            new SubconsciousDebugTriggerRequest
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
            },
            CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(response.Result);
    }

    [TestMethod]
    public async Task TriggerSessionCompressedHook_ShouldPublishHookEvent()
    {
        var publisher = new RecordingHookPublisher();
        var controller = CreateController(new RecordingRuntimeControl(), hookPublisher: publisher);

        var response = await controller.TriggerSessionCompressedHook(
            new SubconsciousDebugSessionCompressedHookRequest
            {
                WorkspaceId = "default",
                OriginalSessionId = "session-1",
                NewSessionId = "session-2",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                CompactionId = "cmp-1",
                Reason = "debug smoke",
                SummaryPreview = "summary preview",
            },
            CancellationToken.None);

        var ok = response.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var payload = ok.Value as SubconsciousDebugHookTriggerResponse;
        Assert.IsNotNull(payload);
        Assert.AreEqual("event-1", payload.EventId);
        Assert.AreEqual(HookEventNames.SessionCompressed.Value, payload.SourceHookName);
        Assert.AreEqual("cmp-1", payload.SourceCompactionId);
        Assert.AreEqual(HookEventNames.SessionCompressed, publisher.LastName);
        var hookPayload = publisher.LastPayload as SessionCompressedHookPayload;
        Assert.IsNotNull(hookPayload);
        Assert.AreEqual("session-1", hookPayload.OriginalSessionId);
        Assert.AreEqual("summary preview", hookPayload.SummaryPreview);
        Assert.AreEqual("debug", publisher.LastOptions?.SourceType);
    }

    [TestMethod]
    public async Task LookupJob_ShouldReturnLatestQueueItem()
    {
        var queue = new RecordingSubconsciousJobQueue
        {
            LookupResult = new SubconsciousJobQueueItem
            {
                JobId = "job-1",
                JobType = SubconsciousJobTypes.MemoryConsolidateSession,
                IdempotencyKey = "memory:default:session-1:cmp-1",
                Status = "pending",
                SourceHookName = HookEventNames.SessionCompressed.Value,
                SourceCompactionId = "cmp-1",
                Job = new ConsolidationJob
                {
                    WorkspaceId = "default",
                    SessionId = "session-1",
                    AgentId = "agent-1",
                    AgentTemplateId = "template-1",
                },
            },
        };
        var controller = CreateController(new RecordingRuntimeControl(), queue: queue);

        var response = await controller.LookupJob(
            jobId: null,
            idempotencyKey: null,
            sourceHookName: HookEventNames.SessionCompressed.Value,
            sourceCompactionId: "cmp-1",
            workspaceId: "default",
            sessionId: "session-1",
            CancellationToken.None);

        var ok = response.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var item = ok.Value as SubconsciousJobQueueItem;
        Assert.IsNotNull(item);
        Assert.AreEqual("job-1", item.JobId);
        Assert.AreEqual("cmp-1", queue.LastLookupQuery?.SourceCompactionId);
        Assert.AreEqual("session-1", queue.LastLookupQuery?.SessionId);
    }

    [TestMethod]
    public async Task GetJobResult_ShouldReturnRecordedResult()
    {
        var queue = new RecordingSubconsciousJobQueue
        {
            Result = new SubconsciousJobResultEnvelope
            {
                Kind = SubconsciousJobResultKinds.MemoryMaintenancePlanDryRun,
                Status = SubconsciousJobResultStatuses.Accepted,
                Valid = true,
            },
        };
        var controller = CreateController(new RecordingRuntimeControl(), queue: queue);

        var response = await controller.GetJobResult("debug-job-1", CancellationToken.None);

        var ok = response.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var result = ok.Value as SubconsciousJobResultEnvelope;
        Assert.IsNotNull(result);
        Assert.AreEqual(SubconsciousJobResultStatuses.Accepted, result.Status);
    }

    [TestMethod]
    public async Task GetJobResult_ShouldReturnNotFoundWhenResultMissing()
    {
        var controller = CreateController(new RecordingRuntimeControl());

        var response = await controller.GetJobResult("debug-job-1", CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(response.Result);
    }

    [TestMethod]
    public async Task GetDebug_ShouldReturnNotFoundWhenDebugApiDisabled()
    {
        var controller = CreateController(
            new RecordingRuntimeControl(),
            debugApiEnabled: false);

        var response = await controller.GetDebug(CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(response.Result);
    }

    private static SubconsciousDebugApiController CreateController(
        ISubconsciousRuntimeControl runtimeControl,
        bool debugApiEnabled = true,
        ISubconsciousJobQueue? queue = null,
        IHookPublisher? hookPublisher = null) =>
        new(
            runtimeControl,
            queue ?? new RecordingSubconsciousJobQueue(),
            hookPublisher ?? new RecordingHookPublisher(),
            Options.Create(new SubconsciousOptions
            {
                DebugApiEnabled = debugApiEnabled,
            }));

    private sealed class RecordingRuntimeControl : ISubconsciousRuntimeControl
    {
        public bool IsPaused { get; private set; }
        public int DebugCalls { get; private set; }
        public SubconsciousRuntimeControlRequest? LastStartRequest { get; private set; }
        public SubconsciousRuntimeControlRequest? LastStopRequest { get; private set; }

        public Task<SubconsciousRuntimeControlSnapshot> StartAsync(
            SubconsciousRuntimeControlRequest request,
            CancellationToken ct = default)
        {
            IsPaused = false;
            LastStartRequest = request;
            return Task.FromResult(CreateSnapshot(SubconsciousRuntimeStates.Running));
        }

        public Task<SubconsciousRuntimeControlSnapshot> StopAsync(
            SubconsciousRuntimeControlRequest request,
            CancellationToken ct = default)
        {
            IsPaused = true;
            LastStopRequest = request;
            return Task.FromResult(CreateSnapshot(SubconsciousRuntimeStates.Paused));
        }

        public Task<SubconsciousRuntimeControlSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            DebugCalls++;
            return Task.FromResult(CreateSnapshot(SubconsciousRuntimeStates.Running));
        }

        private static SubconsciousRuntimeControlSnapshot CreateSnapshot(string state) =>
            new()
            {
                State = state,
                IsPaused = state == SubconsciousRuntimeStates.Paused,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                QueueStats = new SubconsciousJobQueueStats(),
                Scheduling = new Dictionary<string, string>(StringComparer.Ordinal),
                Diagnostics = new Dictionary<string, string>(StringComparer.Ordinal),
            };
    }

    private sealed class RecordingSubconsciousJobQueue : ISubconsciousJobQueue
    {
        public SubconsciousJobEnqueueRequest? LastRequest { get; private set; }
        public SubconsciousJobResultEnvelope? Result { get; init; }
        public SubconsciousJobQueueItem? LookupResult { get; init; }
        public SubconsciousJobLookupQuery? LastLookupQuery { get; private set; }

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new SubconsciousJobQueueItem
            {
                JobId = "debug-job-1",
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
                RetryCount = 0,
                SourceHookName = request.SourceHookName,
                SourceEventId = request.SourceEventId,
                SourceCompactionId = request.SourceCompactionId,
                Job = request.Job,
            });
        }

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new SubconsciousJobQueueStats());

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query,
            CancellationToken ct = default)
        {
            LastLookupQuery = query;
            return Task.FromResult(LookupResult);
        }

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
            => throw new NotSupportedException();

        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId,
            CancellationToken ct = default)
            => Task.FromResult(Result);

        public Task CompleteAsync(string jobId, string leaseOwner, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> RetryAsync(
            string jobId,
            string leaseOwner,
            string error,
            TimeSpan? retryDelay = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeadLetterAsync(
            string jobId,
            string leaseOwner,
            string error,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingHookPublisher : IHookPublisher
    {
        public HookEventName LastName { get; private set; }
        public object? LastPayload { get; private set; }
        public HookPublishOptions? LastOptions { get; private set; }

        public Task<string> PublishAsync<TPayload>(
            HookEventName name,
            TPayload payload,
            HookPublishOptions? options = null,
            CancellationToken ct = default)
        {
            LastName = name;
            LastPayload = payload;
            LastOptions = options;
            return Task.FromResult("event-1");
        }
    }
}
