using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services.Hooks;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SessionCompressedMemoryMaintenanceHookTests
{
    [TestMethod]
    public async Task HandleAsync_ShouldEnqueueConsolidationJob_ForSessionCompressedPayload()
    {
        var queue = new RecordingSubconsciousJobQueue();
        var hook = new SessionCompressedMemoryMaintenanceHook(
            new RecordingInternalEventBus(),
            queue,
            NullLogger<SessionCompressedMemoryMaintenanceHook>.Instance);

        await hook.HandleAsync(new InternalEvent
        {
            EventId = "event-1",
            Type = HookEventNames.SessionCompressed.Value,
            WorkspaceId = "workspace-1",
            Payload = new SessionCompressedHookPayload
            {
                WorkspaceId = "workspace-1",
                OriginalSessionId = "session-1",
                NewSessionId = "session-2",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                CompactionId = "cmp-1",
                Mode = "Auto",
                Level = "Full",
                Reason = "auto threshold",
                SummaryPreview = "compressed summary preview",
                MemoryNotes =
                [
                    "用户要求 Memory v2 V1 默认不做，除非证明缺它跑不起来。",
                    "潜意识整理以 memory notes 为主输入。",
                ],
            },
        }, CancellationToken.None);

        Assert.IsNotNull(queue.LastRequest);
        Assert.AreEqual(SubconsciousJobTypes.MemoryConsolidateSession, queue.LastRequest.JobType);
        Assert.AreEqual("memory.consolidate_session:workspace-1:session-1:cmp-1", queue.LastRequest.IdempotencyKey);
        Assert.AreEqual(HookEventNames.SessionCompressed.Value, queue.LastRequest.SourceHookName);
        Assert.AreEqual("event-1", queue.LastRequest.SourceEventId);
        Assert.AreEqual("cmp-1", queue.LastRequest.SourceCompactionId);
        Assert.AreEqual("session-1", queue.LastRequest.Job.SessionId);
        Assert.AreEqual("workspace-1", queue.LastRequest.Job.WorkspaceId);
        Assert.AreEqual("agent-1", queue.LastRequest.Job.AgentId);
        Assert.AreEqual("template-1", queue.LastRequest.Job.AgentTemplateId);
        Assert.AreEqual("compressed summary preview", queue.LastRequest.Job.LastAssistantReply);
        CollectionAssert.AreEqual(
            new[]
            {
                "用户要求 Memory v2 V1 默认不做，除非证明缺它跑不起来。",
                "潜意识整理以 memory notes 为主输入。",
            },
            queue.LastRequest.Job.MemoryNotes.ToArray());
    }

    [TestMethod]
    public async Task HandleAsync_ShouldSkip_WhenAgentIdentityIsMissing()
    {
        var queue = new RecordingSubconsciousJobQueue();
        var hook = new SessionCompressedMemoryMaintenanceHook(
            new RecordingInternalEventBus(),
            queue,
            NullLogger<SessionCompressedMemoryMaintenanceHook>.Instance);

        await hook.HandleAsync(new InternalEvent
        {
            Type = HookEventNames.SessionCompressed.Value,
            WorkspaceId = "workspace-1",
            Payload = new SessionCompressedHookPayload
            {
                WorkspaceId = "workspace-1",
                OriginalSessionId = "session-1",
                CompactionId = "cmp-1",
                Mode = "Auto",
                Level = "Full",
                Reason = "auto threshold",
            },
        }, CancellationToken.None);

        Assert.IsNull(queue.LastRequest);
    }

    private sealed class RecordingSubconsciousJobQueue : ISubconsciousJobQueue
    {
        public SubconsciousJobEnqueueRequest? LastRequest { get; private set; }

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new SubconsciousJobQueueItem
            {
                JobId = "job-1",
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
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

        public Task DeadLetterAsync(string jobId, string leaseOwner, string error, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default)
            => Task.FromResult<IEventSubscriptionHandle>(new RecordingSubscriptionHandle(eventTypePattern));

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle)
            => Task.CompletedTask;
    }

    private sealed class RecordingSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = Guid.NewGuid().ToString("N");
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;

        public void Dispose() => IsActive = false;
    }
}
