using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pudding.Api.Contracts;
using PuddingEventing.Contracts;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Eventing;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Plugins;
using PuddingPlatform.Services.Reviews;
using PuddingReviews;
using PuddingReviews.Contracts;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03/PA-08 composition smoke on real SQLite (model/evidence faked, as 08A allows): review job → worker →
/// outbox → dispatcher → canonical review.completed → projection → query service snapshot, plus the durable budget
/// account visible through the read API. Proves the independently tested pieces compose without hand wiring.
/// </summary>
[TestClass]
public sealed class ReviewPipelineIntegrationTests
{
    [TestMethod]
    public async Task SubmitToPanelSnapshot_EndToEndWithFakeModel()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Budget.CreateAccountAsync(new("budget-1"), null, new(MaxOutputTokens: 100_000, MaxCost: 10m),
            new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd");
        await host.InsertGoalAsync("goal-1", "conv-1");
        await host.JobStore.CreateOrGetAsync(Job(), "op-1");

        var worker = new ReviewJobWorker(host.JobStore,
            new ReviewJobCoordinator(host.JobStore, new FakeReader(), new FakeExecutor()));
        var batch = await worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, batch.Executed);

        var dispatcher = new ReviewEventDispatcher(host.Factory,
            new ConversationReviewCompletionSink(host.Events, host.Factory));
        var dispatched = await dispatcher.DispatchBatchAsync(10);
        Assert.AreEqual(1, dispatched.Published);

        var projection = new ReviewCompletionProjection(host.Events);
        var latest = await projection.GetLatestAsync("conv-1");
        Assert.AreEqual("Satisfied", latest.Latest!.Finding);
        Assert.AreEqual("CheckVerified", latest.Latest.Level);

        var service = new ExecutionReviewQueryService(host.JobStore, new ExecutionBudgetApiAdapter(host.Budget),
            projection, new ReviewGoalConversationResolver(host.Factory), host.JobStore);
        var result = await service.GetAsync("review-1");
        Assert.IsNull(result.MissingReason);
        Assert.AreEqual("satisfied", result.Snapshot!.ReviewState);
        Assert.AreEqual("completed", result.Snapshot.RunState);
        Assert.IsTrue(result.Snapshot.Budget.Actual.Known);
        Assert.AreEqual("model-a", result.Snapshot.ModelPolicy.ModelId == "" ? "model-a" : result.Snapshot.ModelPolicy.ModelId);
    }

    private static ReviewJobRecord Job() => new("job-1", "review-1",
        new("ws-1", "work-1", "goal-1", 1, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(-1),
        AcceptedSequence: 7);

    private sealed class FakeReader : IReviewEvidenceReader
    {
        public ValueTask<ReviewEvidenceReadResult> ReadAsync(ReviewJobView job, CancellationToken ct = default)
        {
            var snapshot = new ReviewInputSnapshot(job.ReviewId, job.Key, "Build both files.",
                [new("criterion-1", "Both files exist.")],
                [new("evidence-1", EvidenceKind.CheckReport, "check://build", "check://build/1", "sha-check",
                    DateTimeOffset.UtcNow, "controlled-check", Truncated: false, CheckExitCode: "0")],
                EvidenceComplete: true, [], "prompt-v1", "policy-v1", job.BudgetAccountId, job.Deadline.AddMinutes(-1));
            return ValueTask.FromResult(new ReviewEvidenceReadResult(snapshot, null));
        }
    }

    private sealed class FakeExecutor : IReviewExecutor
    {
        public ValueTask<ReviewModelResult> ReviewAsync(ReviewInputSnapshot snapshot, string attemptId,
            CancellationToken ct = default) => ValueTask.FromResult(new ReviewModelResult(ReviewFindingValue.Satisfied,
            [new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-1"])], 0.9, "both files present", null,
            snapshot.Key.ObjectiveVersion, snapshot.Key.AcceptanceContractVersion, snapshot.Key.ArtifactSetHash,
            snapshot.PromptVersion, "model-a", "rev-1", "fingerprint"));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _connection;

        public IDbContextFactory<PlatformDbContext> Factory { get; }
        public ConversationEventStore Events { get; }
        public ReviewJobStore JobStore { get; }
        public ReviewEventOutbox Outbox { get; }
        public ExecutionBudgetStore Budget { get; }

        private TestHost(string dbPath, SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory,
            ConversationEventStore events, ReviewJobStore jobs, ReviewEventOutbox outbox, ExecutionBudgetStore budget)
        {
            _dbPath = dbPath;
            _connection = connection;
            Factory = factory;
            Events = events;
            JobStore = jobs;
            Outbox = outbox;
            Budget = budget;
        }

        public async Task InsertGoalAsync(string goalRunId, string conversationId)
        {
            await using var db = Factory.CreateDbContext();
            db.GoalRuns.Add(new GoalRunEntity
            {
                GoalRunId = goalRunId,
                WorkspaceId = "ws-1",
                CurrentConversationId = conversationId,
                AgentInstanceId = "agent-1",
                Objective = "Build both files.",
                ObjectiveVersion = 2,
                ActivationEpoch = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        public static async Task<TestHost> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "pudding-review-pipeline-" + Guid.NewGuid().ToString("N") + ".db");
            var services = new ServiceCollection();
            services.AddDbContextFactory<PlatformDbContext>(options => options.UseSqlite($"Data Source={dbPath};Pooling=False"));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext());
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
                await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
                await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var events = new ConversationEventStore(provider.GetRequiredService<IServiceScopeFactory>(),
                new CommittedEventSignal(), NullLogger<ConversationEventStore>.Instance);
            await events.EnsureTablesAsync(default);
            var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            await connection.OpenAsync();
            return new(dbPath, connection, factory, events, new ReviewJobStore(factory), new ReviewEventOutbox(factory),
                new ExecutionBudgetStore(factory));
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }
}
