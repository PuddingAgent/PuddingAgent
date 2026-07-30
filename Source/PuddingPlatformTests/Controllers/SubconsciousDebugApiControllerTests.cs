using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Controllers.Api;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class SubconsciousDebugApiControllerTests
{
    [TestMethod]
    public async Task TriggerEvolution_All_ShouldEnqueueThreeScopedDurableJobsAndReuseRequestId()
    {
        var queue = new RecordingJobQueue();
        var controller = CreateController(queue);
        var request = new SubconsciousDebugEvolutionTriggerRequest
        {
            Action = "all",
            RequestId = "test-request-1",
        };

        var first = await controller.TriggerEvolution(request, CancellationToken.None);
        var firstAccepted = Assert.IsInstanceOfType<AcceptedResult>(first.Result);
        var firstResponse = Assert.IsInstanceOfType<SubconsciousDebugEvolutionTriggerResponse>(firstAccepted.Value);

        Assert.AreEqual("default", firstResponse.WorkspaceId);
        Assert.AreEqual("agent-evolution", firstResponse.AgentInstanceId);
        CollectionAssert.AreEqual(
            new[]
            {
                SubconsciousJobTypes.AutoDream,
                SubconsciousJobTypes.ExtractPatterns,
                SubconsciousJobTypes.ImproveSkills,
            },
            firstResponse.Jobs.Select(job => job.JobType).ToArray());
        Assert.IsTrue(firstResponse.Jobs.All(job => !job.Reused));
        Assert.IsTrue(queue.Requests.All(enqueued => enqueued.Job.WorkspaceId == "default"));
        Assert.IsTrue(queue.Requests.All(enqueued => enqueued.Job.AgentId == "agent-evolution"));
        Assert.IsTrue(queue.Requests.All(enqueued => enqueued.SourceHookName == "debug.subconscious.evolution"));

        var second = await controller.TriggerEvolution(request, CancellationToken.None);
        var secondAccepted = Assert.IsInstanceOfType<AcceptedResult>(second.Result);
        var secondResponse = Assert.IsInstanceOfType<SubconsciousDebugEvolutionTriggerResponse>(secondAccepted.Value);

        Assert.AreEqual(3, queue.Requests.Count);
        Assert.IsTrue(secondResponse.Jobs.All(job => job.Reused));
        CollectionAssert.AreEqual(
            firstResponse.Jobs.Select(job => job.JobId).ToArray(),
            secondResponse.Jobs.Select(job => job.JobId).ToArray());
    }

    [TestMethod]
    public async Task TriggerEvolution_UnknownAction_ShouldReturnBadRequestWithoutEnqueue()
    {
        var queue = new RecordingJobQueue();
        var controller = CreateController(queue);

        var result = await controller.TriggerEvolution(
            new SubconsciousDebugEvolutionTriggerRequest { Action = "unknown" },
            CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, queue.Requests.Count);
    }

    private static SubconsciousDebugApiController CreateController(RecordingJobQueue queue)
        => new(
            new StubRuntimeControl(),
            queue,
            new StubHookPublisher(),
            Options.Create(new SubconsciousOptions
            {
                DebugApiEnabled = true,
                Scheduling = new SubconsciousSchedulingOptions
                {
                    DefaultWorkspaceId = "default",
                    DefaultAgentInstanceId = "agent-evolution",
                },
            }));

    private sealed class RecordingJobQueue : ISubconsciousJobQueue
    {
        private readonly Dictionary<string, SubconsciousJobQueueItem> _items = new(StringComparer.Ordinal);

        public List<SubconsciousJobEnqueueRequest> Requests { get; } = [];

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var item = new SubconsciousJobQueueItem
            {
                JobId = $"job-{Requests.Count}",
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
                SourceHookName = request.SourceHookName,
                SourceEventId = request.SourceEventId,
                SourceCompactionId = request.SourceCompactionId,
                Job = request.Job,
            };
            _items[request.IdempotencyKey] = item;
            return Task.FromResult(item);
        }

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query,
            CancellationToken ct = default)
            => Task.FromResult(
                query.IdempotencyKey is not null
                && _items.TryGetValue(query.IdempotencyKey, out var item)
                    ? item
                    : null);

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
            DateTimeOffset since,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RecordSchedulingSkipAsync(
            SubconsciousSchedulingSkipRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RecordResultAsync(
            string jobId,
            string leaseOwner,
            SubconsciousJobResultEnvelope result,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CompleteAsync(
            string jobId,
            string leaseOwner,
            CancellationToken ct = default)
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

    private sealed class StubRuntimeControl : ISubconsciousRuntimeControl
    {
        public bool IsPaused => false;

        public Task<SubconsciousRuntimeControlSnapshot> StartAsync(
            SubconsciousRuntimeControlRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousRuntimeControlSnapshot> StopAsync(
            SubconsciousRuntimeControlRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousRuntimeControlSnapshot> GetSnapshotAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubHookPublisher : IHookPublisher
    {
        public Task<string> PublishAsync<TPayload>(
            HookEventName name,
            TPayload payload,
            HookPublishOptions? options = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
