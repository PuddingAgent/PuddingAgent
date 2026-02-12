using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
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

        public TaskCompletionSource ResultRecorded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SubconsciousJobResultEnvelope? RecordedResult { get; private set; }
        public int CompleteCount { get; private set; }
        public int LeaseCount => _leaseCount;
        public ConsolidationJob? Job { get; init; }

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _leaseCount) > 1)
                return Task.FromResult<SubconsciousJobQueueItem?>(null);

            return Task.FromResult<SubconsciousJobQueueItem?>(new SubconsciousJobQueueItem
            {
                JobId = "job-1",
                JobType = SubconsciousJobTypes.MemoryConsolidateSession,
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
            CompleteCount++;
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
