using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Background;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousWorkerServiceTests
{
    [TestMethod]
    public async Task DurableWorker_WithMemoryNotes_ShouldExecuteWikiPageUpdateAndCompleteJob()
    {
        await using var memory = await CreateMemoryScopeAsync();
        var queue = new RecordingSubconsciousJobQueue
        {
            Job = new ConsolidationJob
            {
                SessionId = "session-1",
                WorkspaceId = "workspace-1",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                MemoryNotes = ["用户偏好简单 V1。"],
            },
        };
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            wikiPageUpdateService: new MemoryWikiPageUpdateService(new StaticMemoryLlmClient(PageUpdateJson)),
            wikiPageWriteEntry: new WikiPageWriteEntry(memory.Library));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.ResultRecorded.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, orchestrator.CallCount);
        Assert.AreEqual(1, queue.CompleteCount);
        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(SubconsciousJobResultKinds.MemoryWikiPageUpdate, queue.RecordedResult!.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Accepted, queue.RecordedResult.Status);
        Assert.AreEqual("1", queue.RecordedResult.Metadata["written_page_count"]);

        var libraries = await memory.Library.ListLibrariesAsync("workspace-1");
        var books = await memory.Library.ListBooksAsync(libraries[0].LibraryId);
        var chapters = await memory.Library.ListChaptersAsync(books[0].BookId);
        Assert.AreEqual(1, books.Count);
        Assert.AreEqual("用户偏好", books[0].Title);
        Assert.AreEqual(1, chapters.Count);
        Assert.AreEqual("/设计", chapters[0].Title);
        Assert.AreEqual("# 设计\n\n- 用户偏好简单 V1。", chapters[0].Content);
    }

    [TestMethod]
    public async Task DurableWorker_ShouldRecordF5DryRunResultEnvelopeAndCompleteJob()
    {
        var queue = new RecordingSubconsciousJobQueue();
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var planService = new SubconsciousPlanGenerationService(
            new StaticMemoryLlmClient(ValidPlanJson),
            new MemoryMaintenancePlanValidator());
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            planGenerationService: planService,
            memoryWriteCoordinator: coordinator);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.ResultRecorded.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, orchestrator.CallCount);
        Assert.AreEqual(1, queue.CompleteCount);
        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(SubconsciousJobResultStatuses.Accepted, queue.RecordedResult!.Status);
        Assert.AreEqual(1, queue.RecordedResult.MemoryWriteResults.Count);
        Assert.AreEqual("plan-1:op-1", queue.RecordedResult.MemoryWriteResults[0].CommandId);
        Assert.AreEqual(MemoryWriteResultStatuses.DryRun, queue.RecordedResult.MemoryWriteResults[0].Status);
        Assert.AreEqual(MemoryWriteIntents.AppendNew, queue.RecordedResult.MemoryWriteResults[0].Intent);
    }

    [TestMethod]
    public async Task PausedWorker_ShouldNotLeaseDurableJobs()
    {
        var queue = new RecordingSubconsciousJobQueue();
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var runtimeControl = new PausedSubconsciousRuntimeControl();
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            runtimeControl: runtimeControl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, queue.LeaseCount);
        Assert.AreEqual(0, orchestrator.CallCount);
    }

    [TestMethod]
    public async Task PeriodicLoops_ShouldEnqueueFourDurableScopedJobs()
    {
        var queue = new RecordingSubconsciousJobQueue { DisableLeasing = true };
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions
            {
                Scheduling = new SubconsciousSchedulingOptions
                {
                    PeriodicJobsEnabled = true,
                    DefaultWorkspaceId = "workspace-evolution",
                    DefaultAgentInstanceId = "agent-evolution",
                    AutoDreamInitialDelaySeconds = 0,
                    PatternExtractionInitialDelaySeconds = 0,
                    SkillImprovementInitialDelaySeconds = 0,
                    SkillCurationInitialDelaySeconds = 0,
                    AutoDreamIntervalSeconds = 3600,
                    PatternExtractionIntervalSeconds = 3600,
                    SkillImprovementIntervalSeconds = 3600,
                    SkillCurationIntervalSeconds = 3600,
                },
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.AllPeriodicJobsEnqueued.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        var requests = queue.EnqueuedRequests;
        CollectionAssert.AreEquivalent(
            new[]
            {
                SubconsciousJobTypes.AutoDream,
                SubconsciousJobTypes.ExtractPatterns,
                SubconsciousJobTypes.ImproveSkills,
                SubconsciousJobTypes.SkillCurate,
            },
            requests.Select(request => request.JobType).ToArray());
        Assert.IsTrue(requests.All(request => request.Job.WorkspaceId == "workspace-evolution"));
        Assert.IsTrue(requests.All(request => request.Job.AgentId == "agent-evolution"));
        Assert.IsTrue(requests.All(request => request.IdempotencyKey.StartsWith("periodic:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PeriodicLoops_ShouldNotReopenExistingTimeBucketJobs()
    {
        var queue = new RecordingSubconsciousJobQueue
        {
            DisableLeasing = true,
            ExistingLookupItem = new SubconsciousJobQueueItem
            {
                JobId = "existing-periodic-job",
                JobType = SubconsciousJobTypes.AutoDream,
                IdempotencyKey = "existing-periodic-key",
                Status = "completed",
                Job = new ConsolidationJob
                {
                    SessionId = "periodic:existing",
                    WorkspaceId = "default",
                    AgentId = "default.general-assistant-001",
                    AgentTemplateId = "default.general-assistant-001",
                },
            },
        };
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions
            {
                Scheduling = new SubconsciousSchedulingOptions
                {
                    PeriodicJobsEnabled = true,
                    AutoDreamInitialDelaySeconds = 0,
                    PatternExtractionInitialDelaySeconds = 0,
                    SkillImprovementInitialDelaySeconds = 0,
                    AutoDreamIntervalSeconds = 3600,
                    PatternExtractionIntervalSeconds = 3600,
                    SkillImprovementIntervalSeconds = 3600,
                },
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(queue.LookupCount >= 3);
        Assert.AreEqual(0, queue.EnqueuedRequests.Count);
    }

    [TestMethod]
    public async Task PeriodicLoops_SkillCuration_ShouldNotReopenSameTimeBucketAcrossRestarts()
    {
        var queue = new KeyTrackingJobQueue();
        SubconsciousSchedulingOptions NewScheduling() => new()
        {
            PeriodicJobsEnabled = true,
            DefaultWorkspaceId = "workspace-evolution",
            DefaultAgentInstanceId = "agent-evolution",
            AutoDreamInitialDelaySeconds = 0,
            PatternExtractionInitialDelaySeconds = 0,
            SkillImprovementInitialDelaySeconds = 0,
            SkillCurationInitialDelaySeconds = 0,
            AutoDreamIntervalSeconds = 100_000,
            PatternExtractionIntervalSeconds = 100_000,
            SkillImprovementIntervalSeconds = 100_000,
            SkillCurationIntervalSeconds = 100_000,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker1 = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions { Scheduling = NewScheduling() }));
        await worker1.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
        await worker1.StopAsync(CancellationToken.None);

        var worker2 = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions { Scheduling = NewScheduling() }));
        await worker2.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
        await worker2.StopAsync(CancellationToken.None);

        var skillCurateEnqueues = queue.Requests.Count(request =>
            request.JobType == SubconsciousJobTypes.SkillCurate);
        Assert.AreEqual(1, skillCurateEnqueues);
    }

    [TestMethod]
    public async Task DurableWorker_SkillCurate_ShouldReportWithoutWritingSkills()
    {
        await using var memory = await CreateMemoryScopeAsync();
        var probe = new ProbeSkillStore();
        var llmClient = new StaticMemoryLlmClient("unused: curation must not call the llm");
        var orchestrator = new SubconsciousOrchestrator(
            memory.Library,
            new ThrowingMemoryEngine(),
            llmClient,
            new ThrowingMemoryLibrarian(),
            NullLogger<SubconsciousOrchestrator>.Instance,
            new ThrowingMemoryDbContextFactory(),
            new ThrowingSkillTrajectorySource(),
            probe,
            new SkillEvolutionDeduplicationService(
                probe,
                llmClient,
                NullLogger<SkillEvolutionDeduplicationService>.Instance));

        var queue = new RecordingSubconsciousJobQueue
        {
            JobType = SubconsciousJobTypes.SkillCurate,
            Job = new ConsolidationJob
            {
                SessionId = "debug:evolution:skill.curate:request-1",
                WorkspaceId = "workspace-evolution",
                AgentId = "agent-evolution",
                AgentTemplateId = "agent-evolution",
            },
        };
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.JobCompleted.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(SubconsciousJobResultKinds.SkillCuration, queue.RecordedResult!.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Completed, queue.RecordedResult.Status);
        Assert.AreEqual(0, queue.RecordedResult.OperationCount);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_before"]);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_after"]);
        Assert.IsTrue(!string.IsNullOrWhiteSpace(queue.RecordedResult.Metadata["not_reduced_reason"]));
        Assert.AreEqual("3", queue.RecordedResult.Metadata["candidate_count"]);
        Assert.AreEqual("1", queue.RecordedResult.Metadata["retire_suggestion_count"]);
        Assert.AreEqual("v1", queue.RecordedResult.Metadata["report_version"]);
        Assert.AreEqual(0, probe.WriteCallCount);
    }

    private sealed class ProbeSkillStore : IAgentSkillEvolutionStore
    {
        private int _writeCallCount;
        public int WriteCallCount => _writeCallCount;

        private static AgentSkillEvolutionDocument Skill(string id, string name, string version) => new()
        {
            SkillId = id,
            Name = name,
            Version = version,
            Enabled = true,
            Markdown = $"---\nname: {name}\nversion: {version}\n---\nbody",
        };

        public Task<IReadOnlyList<AgentSkillEvolutionDocument>> ListAutoGeneratedAsync(
            string agentInstanceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkillEvolutionDocument>>(
            [
                Skill("skill-a", "Same Name", "1.0.0"),
                Skill("skill-b", "Same Name", "1.0.1"),
                Skill("skill-c", "Other Name", "1.0.0"),
            ]);

        public Task<AgentSkillEvolutionDocument?> GetAsync(
            string agentInstanceId, string skillId, CancellationToken ct = default)
            => Task.FromResult<AgentSkillEvolutionDocument?>(null);

        public Task<AgentSkillEvolutionDocument> CreateAsync(
            string agentInstanceId, AgentSkillEvolutionWriteRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G6 curation must not write skills (CreateAsync).");
        }

        public Task<AgentSkillEvolutionDocument> UpdateAsync(
            string agentInstanceId, string skillId, AgentSkillEvolutionWriteRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G6 curation must not write skills (UpdateAsync).");
        }

        public Task<AgentSkillEvolutionDocument> SetEnabledAsync(
            string agentInstanceId, string skillId, bool enabled, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G6 curation must not write skills (SetEnabledAsync).");
        }
    }

    private sealed class ThrowingMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId, string? workspaceId, string? agentId,
            string? parentSessionId = null) => throw new NotSupportedException();
        public Task<string?> RecallWithIntentAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000, CancellationToken ct = default)
            => throw new NotSupportedException();
        public void WriteBack(
            string llmReply, string sessionId, string? workspaceId, string source,
            string? agentId = null, string? parentSessionId = null) => throw new NotSupportedException();
        public void ClearSession(string sessionId) => throw new NotSupportedException();
    }

    private sealed class ThrowingMemoryLibrarian : IMemoryLibrarian
    {
        public Task<ExperienceWriteResult> IngestExperienceAsync(
            MemoryIngestionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<MemoryTreeOperation>> PlanTreeMaintenanceAsync(
            string workspaceId, string libraryId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ApplyTreeOperationAsync(MemoryTreeOperation operation, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingMemoryDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => throw new NotSupportedException();
        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingSkillTrajectorySource : ISkillEvolutionTrajectorySource
    {
        public Task<IReadOnlyList<SkillEvolutionTrajectory>> GetRecentSuccessfulAsync(
            string workspaceId, string agentInstanceId, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class KeyTrackingJobQueue : ISubconsciousJobQueue
    {
        private readonly Dictionary<string, SubconsciousJobQueueItem> _items = new(StringComparer.Ordinal);
        public List<SubconsciousJobEnqueueRequest> Requests { get; } = [];

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var item = new SubconsciousJobQueueItem
            {
                JobId = $"job-{Requests.Count}",
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
                Job = request.Job,
            };
            _items[request.IdempotencyKey] = item;
            return Task.FromResult(item);
        }

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query, CancellationToken ct = default)
            => Task.FromResult(
                query.IdempotencyKey is not null
                && _items.TryGetValue(query.IdempotencyKey, out var item)
                    ? item
                    : null);

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner, TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null, CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobQueueItem?>(null);
        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new SubconsciousJobQueueStats());
        public Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
            DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task RecordSchedulingSkipAsync(
            SubconsciousSchedulingSkipRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task RecordResultAsync(
            string jobId, string leaseOwner, SubconsciousJobResultEnvelope result, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId, CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobResultEnvelope?>(null);
        public Task CompleteAsync(string jobId, string leaseOwner, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string> RetryAsync(
            string jobId, string leaseOwner, string error, TimeSpan? retryDelay = null, CancellationToken ct = default)
            => Task.FromResult("retrying");
        public Task DeadLetterAsync(
            string jobId, string leaseOwner, string error, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [TestMethod]
    [DataRow(SubconsciousJobTypes.AutoDream, SubconsciousJobResultKinds.MemoryAutoDream, 2)]
    [DataRow(SubconsciousJobTypes.ExtractPatterns, SubconsciousJobResultKinds.SkillPatternExtraction, 3)]
    [DataRow(SubconsciousJobTypes.ImproveSkills, SubconsciousJobResultKinds.SkillImprovement, 2)]
    [DataRow(SubconsciousJobTypes.SkillCurate, SubconsciousJobResultKinds.SkillCuration, 0)]
    public async Task DurableWorker_PeriodicEvolutionJob_ShouldPersistReportBeforeCompleting(
        string jobType,
        string expectedResultKind,
        int expectedOperationCount)
    {
        var queue = new RecordingSubconsciousJobQueue
        {
            JobType = jobType,
            Job = new ConsolidationJob
            {
                SessionId = $"debug:evolution:{jobType}:request-1",
                WorkspaceId = "workspace-evolution",
                AgentId = "agent-evolution",
                AgentTemplateId = "agent-evolution",
            },
        };
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.JobCompleted.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(queue.ResultWasRecordedBeforeComplete);
        Assert.AreEqual(1, orchestrator.CallCount);
        Assert.AreEqual(1, queue.CompleteCount);
        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(expectedResultKind, queue.RecordedResult!.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Completed, queue.RecordedResult.Status);
        Assert.AreEqual(SubconsciousJobResultDecisions.ExecutionCompleted, queue.RecordedResult.Decision);
        Assert.AreEqual(SubconsciousJobResultNextActions.CompleteJob, queue.RecordedResult.NextAction);
        Assert.IsTrue(queue.RecordedResult.Valid);
        Assert.AreEqual(expectedOperationCount, queue.RecordedResult.OperationCount);
        Assert.AreEqual("workspace-evolution", queue.RecordedResult.Metadata["workspace_id"]);
        Assert.AreEqual("agent-evolution", queue.RecordedResult.Metadata["agent_instance_id"]);
        Assert.AreEqual("job-1", queue.RecordedResult.Metadata["subconscious_job_id"]);
        Assert.AreEqual(jobType, queue.RecordedResult.Metadata["job_type"]);

        switch (jobType)
        {
            case SubconsciousJobTypes.AutoDream:
                Assert.AreEqual("2", queue.RecordedResult.Metadata["executed_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["merged_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["archived_count"]);
                break;
            case SubconsciousJobTypes.ExtractPatterns:
                Assert.AreEqual("3", queue.RecordedResult.Metadata["candidates_found_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["promoted_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["merged_count"]);
                Assert.AreEqual("0", queue.RecordedResult.Metadata["deferred_count"]);
                Assert.AreEqual("skill-create-pr", queue.RecordedResult.Metadata["created_skill_ids"]);
                Assert.AreEqual("skill-health-check", queue.RecordedResult.Metadata["updated_skill_ids"]);
                break;
            case SubconsciousJobTypes.ImproveSkills:
                Assert.AreEqual("2", queue.RecordedResult.Metadata["evaluated_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["patched_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["consolidated_count"]);
                Assert.AreEqual("skill-create-pr", queue.RecordedResult.Metadata["improved_skill_ids"]);
                Assert.AreEqual("skill-create-pr-old", queue.RecordedResult.Metadata["disabled_duplicate_skill_ids"]);
                break;
            case SubconsciousJobTypes.SkillCurate:
                Assert.AreEqual("6", queue.RecordedResult.Metadata["n_before"]);
                Assert.AreEqual("6", queue.RecordedResult.Metadata["n_after"]);
                Assert.IsTrue(!string.IsNullOrWhiteSpace(queue.RecordedResult.Metadata["not_reduced_reason"]));
                Assert.AreEqual("2", queue.RecordedResult.Metadata["candidate_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["retire_suggestion_count"]);
                Assert.AreEqual("v1", queue.RecordedResult.Metadata["report_version"]);
                break;
        }
    }

    private const string ValidPlanJson = """
        {
          "planId": "plan-1",
          "workspaceId": "workspace-1",
          "source": {
            "workspaceId": "workspace-1",
            "sessionId": "session-1",
            "subconsciousJobId": "job-1",
            "agentId": "agent-1",
            "agentTemplateId": "template-1"
          },
          "operations": [
            {
              "operationId": "op-1",
              "action": "append_new",
              "proposedContent": "User prefers concise engineering summaries.",
              "confidence": 0.84,
              "rationale": "Stable preference from session evidence."
            }
          ],
          "confidence": 0.84,
          "rationale": "Dry-run plan only."
        }
        """;

    private const string PageUpdateJson = """
        {
          "schema": "pudding.memory_wiki_page_update.v1",
          "updates": [
            {
              "book": "用户偏好",
              "page": "/设计",
              "content": "# 设计\n\n- 用户偏好简单 V1。"
            }
          ]
        }
        """;

    private sealed class RecordingSubconsciousJobQueue : ISubconsciousJobQueue
    {
        private int _leaseCount;
        private int _lookupCount;

        public TaskCompletionSource ResultRecorded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllPeriodicJobsEnqueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource JobCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SubconsciousJobResultEnvelope? RecordedResult { get; private set; }
        public bool ResultWasRecordedBeforeComplete { get; private set; }
        public int CompleteCount { get; private set; }
        public int LeaseCount => _leaseCount;
        public int LookupCount => _lookupCount;
        public ConsolidationJob? Job { get; init; }
        public string JobType { get; init; } = SubconsciousJobTypes.MemoryConsolidateSession;
        public bool DisableLeasing { get; init; }
        public SubconsciousJobQueueItem? ExistingLookupItem { get; init; }
        private readonly List<SubconsciousJobEnqueueRequest> _enqueuedRequests = [];
        public IReadOnlyList<SubconsciousJobEnqueueRequest> EnqueuedRequests
        {
            get
            {
                lock (_enqueuedRequests)
                    return _enqueuedRequests.ToArray();
            }
        }

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
        {
            lock (_enqueuedRequests)
            {
                _enqueuedRequests.Add(request);
                if (_enqueuedRequests.Count >= 4)
                    AllPeriodicJobsEnqueued.TrySetResult();
            }

            return Task.FromResult(new SubconsciousJobQueueItem
            {
                JobId = Guid.NewGuid().ToString("N"),
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
                Job = request.Job,
            });
        }

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
        {
            if (DisableLeasing)
                return Task.FromResult<SubconsciousJobQueueItem?>(null);

            if (Interlocked.Increment(ref _leaseCount) > 1)
                return Task.FromResult<SubconsciousJobQueueItem?>(null);

            return Task.FromResult<SubconsciousJobQueueItem?>(new SubconsciousJobQueueItem
            {
                JobId = "job-1",
                JobType = JobType,
                IdempotencyKey = "memory:workspace-1:session-1:cmp-1",
                Status = "processing",
                Job = Job ?? new ConsolidationJob
                {
                    SessionId = "session-1",
                    WorkspaceId = "workspace-1",
                    AgentId = "agent-1",
                    AgentTemplateId = "template-1",
                    LastUserMessage = "Please keep summaries concise.",
                    LastAssistantReply = "I will keep the engineering summary concise.",
                },
            });
        }

        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new SubconsciousJobQueueStats());

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _lookupCount);
            return Task.FromResult(ExistingLookupItem);
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
        {
            RecordedResult = result;
            ResultRecorded.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId,
            CancellationToken ct = default)
            => Task.FromResult(RecordedResult);

        public Task CompleteAsync(string jobId, string leaseOwner, CancellationToken ct = default)
        {
            ResultWasRecordedBeforeComplete = RecordedResult is not null;
            CompleteCount++;
            JobCompleted.TrySetResult();
            return Task.CompletedTask;
        }

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

    private sealed class RecordingSubconsciousOrchestrator : ISubconsciousOrchestrator
    {
        public int CallCount { get; private set; }

        public Task ConsolidateAsync(
            ConsolidationJob job,
            string mode,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }

        public Task<SessionSummary> SummarizeSessionAsync(
            string sessionId,
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> RecallAugmentedAsync(
            string userMessage,
            string workspaceId,
            string agentId,
            string? sessionId = null,
            int maxTokens = 2000,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryDashboard> GetMemoryDashboardAsync(
            string workspaceId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemorySearchResult> SearchMemoriesAsync(
            MemorySearchRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AutoDreamReport> AutoDreamAsync(
            string workspaceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new AutoDreamReport
            {
                DurationMs = 12,
                Merged = 1,
                Archived = 1,
                Deleted = 0,
                Suggested = 3,
                Executed = 2,
                Summary = "merged 1, archived 1, deleted 0",
                Timestamp = new DateTime(2026, 7, 30, 4, 0, 0, DateTimeKind.Utc),
            });
        }

        public Task<PatternExtractionReport> ExtractPatternsAsync(
            string workspaceId,
            string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new PatternExtractionReport
            {
                DurationMs = 23,
                CandidatesFound = 3,
                Promoted = 1,
                Merged = 1,
                Deferred = 0,
                DemotedToMemory = 1,
                Skipped = 1,
                CreatedSkillIds = ["skill-create-pr"],
                UpdatedSkillIds = ["skill-health-check"],
                Summary = "found 3, promoted 1, demoted 1, skipped 1",
                Timestamp = new DateTime(2026, 7, 30, 4, 1, 0, DateTimeKind.Utc),
            });
        }

        public Task<SkillImprovementReport> ImproveSkillsAsync(
            string workspaceId,
            string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new SkillImprovementReport
            {
                DurationMs = 34,
                Evaluated = 2,
                Patched = 1,
                Consolidated = 1,
                Skipped = 1,
                ImprovedSkillIds = ["skill-create-pr"],
                DisabledDuplicateSkillIds = ["skill-create-pr-old"],
                Summary = "Improved 1 skill",
                Timestamp = new DateTime(2026, 7, 30, 4, 2, 0, DateTimeKind.Utc),
            });
        }

        public Task<SkillCurationReport> SkillCurateAsync(
            string workspaceId,
            string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new SkillCurationReport
            {
                DurationMs = 45,
                NBefore = 6,
                NAfter = 6,
                NotReducedReason = "G6 is report-only: identified 1 duplicate-name retire suggestion(s), but curation has no disable authority until G7 gates land.",
                CandidateCount = 2,
                RetireSuggestionCount = 1,
                Summary = "n_before=6, n_after=6, refine candidates=2, retire suggestions=1; report-only, no skills modified",
                Timestamp = new DateTime(2026, 7, 30, 4, 3, 0, DateTimeKind.Utc),
            });
        }
    }

    private sealed class StaticMemoryLlmClient(string response) : IMemoryLlmClient
    {
        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => Task.FromResult(response);

        public Task<string> ChatWithConfigAsync(
            string systemPrompt,
            string userMessage,
            MemoryLlmConfig? memoryLlmConfig,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => Task.FromResult(response);
    }

    private sealed class PausedSubconsciousRuntimeControl : ISubconsciousRuntimeControl
    {
        public bool IsPaused => true;

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

    private static async Task<MemoryScope> CreateMemoryScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;
        var factory = new TestDbContextFactory(options);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new MemoryScope(connection, new MemoryLibrary(factory));
    }

    private sealed class MemoryScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public MemoryScope(SqliteConnection connection, IMemoryLibrary library)
        {
            _connection = connection;
            Library = library;
        }

        public IMemoryLibrary Library { get; }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }
}
