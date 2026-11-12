using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5 product sink on a real SQLite conversation event store: the completed review becomes a canonical
/// <c>review.completed</c> event on the Goal's current conversation with the frozen id/parity fields; a review
/// without a resolvable Goal/conversation is a typed failure so the outbox keeps it unpublished.
/// </summary>
[TestClass]
public sealed class ConversationReviewCompletionSinkTests
{
    [TestMethod]
    public async Task PublishesACanonicalReviewCompletedEventOnTheGoalConversation()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertGoalAsync("goal-1", "conv-1");
        var sink = new ConversationReviewCompletionSink(host.Events, host.Factory);

        await sink.PublishAsync(Completed());
        var page = await host.Events.ReadForwardAsync("conv-1", 0, null, 100, default);
        Assert.AreEqual(1, page.Events.Count);
        var evt = page.Events[0];
        Assert.AreEqual(ConversationReviewCompletionSink.EventType, evt.Type);
        Assert.AreEqual("event-1", evt.EventId);
        Assert.AreEqual("NeedsWork", evt.Payload.GetProperty("finding").GetString());
        Assert.AreEqual("Observed", evt.Payload.GetProperty("level").GetString());
        Assert.AreEqual("review-1", evt.Payload.GetProperty("reviewId").GetString());
        Assert.AreEqual("artifact-hash", evt.Payload.GetProperty("artifactSetHash").GetString());
    }

    [TestMethod]
    public async Task UnresolvableGoalOrConversation_IsATypedFailure()
    {
        await using var host = await TestHost.CreateAsync();
        var sink = new ConversationReviewCompletionSink(host.Events, host.Factory);
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await sink.PublishAsync(Completed()));
        Assert.AreEqual(ReviewErrorCodes.ExecutorFailed, failure.Code);

        await host.InsertGoalAsync("goal-1", "");
        var empty = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await sink.PublishAsync(Completed()));
        Assert.AreEqual(ReviewErrorCodes.ExecutorFailed, empty.Code);

        var noGoal = Completed() with
        {
            Key = Completed().Key with { GoalRunId = null, ParentWorkId = "work-1" },
        };
        var noGoalFailure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await sink.PublishAsync(noGoal));
        Assert.AreEqual(ReviewErrorCodes.ExecutorFailed, noGoalFailure.Code);
    }

    private static ReviewCompletedEvent Completed() => new("event-1", "review-1", "job-1",
        new("ws-1", "work-1", "goal-1", 3, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        ReviewFindingValue.NeedsWork, ReviewEvidenceLevel.Observed, 4, DateTimeOffset.UtcNow);

    private sealed class TestHost(SqliteConnection connection, string dbPath,
        IDbContextFactory<PlatformDbContext> factory, ConversationEventStore events) : IAsyncDisposable
    {
        public IDbContextFactory<PlatformDbContext> Factory { get; } = factory;
        public ConversationEventStore Events { get; } = events;

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
                ActivationEpoch = 3,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        public static async Task<TestHost> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "pudding-review-sink-" + Guid.NewGuid().ToString("N") + ".db");
            var services = new ServiceCollection();
            services.AddDbContextFactory<PlatformDbContext>(options => options.UseSqlite($"Data Source={dbPath};Pooling=False"));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext());
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
            using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();
            var events = new ConversationEventStore(provider.GetRequiredService<IServiceScopeFactory>(),
                new CommittedEventSignal(), NullLogger<ConversationEventStore>.Instance);
            await events.EnsureTablesAsync(default);
            var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            await connection.OpenAsync();
            return new(connection, dbPath, factory, events);
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
