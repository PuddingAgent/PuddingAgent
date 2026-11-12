using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03/04/08 read-side projection on a real SQLite conversation event store: the latest well-formed
/// <c>review.completed</c> fact by sequence wins, malformed events are counted and skipped, duplicate event ids are
/// deduplicated, and an empty conversation yields null without inventing a review result.
/// </summary>
[TestClass]
public sealed class ReviewCompletionProjectionTests
{
    [TestMethod]
    public async Task LatestWellFormedFactBySequenceWins()
    {
        await using var host = await TestHost.CreateAsync();
        await host.AppendAsync(Completed("event-1", "review-1", revision: 1));
        await host.AppendAsync(Completed("event-2", "review-1", revision: 3, finding: "Satisfied", level: "CheckVerified"));
        await host.AppendAsync(Completed("event-3", "review-2", revision: 2, finding: "NeedsWork", level: "Observed"));

        var result = await new ReviewCompletionProjection(host.Events).GetLatestAsync("conv-1");
        Assert.IsNotNull(result.Latest);
        Assert.AreEqual("review-2", result.Latest!.ReviewId);
        Assert.AreEqual("NeedsWork", result.Latest.Finding);
        Assert.AreEqual(2L, result.Latest.Revision);
        Assert.AreEqual(3, result.Scanned);
        Assert.AreEqual(0, result.Malformed);
    }

    [TestMethod]
    public async Task MalformedEventsAreSkippedAndNeverBecomeTheResult()
    {
        await using var host = await TestHost.CreateAsync();
        await host.AppendAsync(Completed("event-1", "review-1", revision: 1, finding: "Satisfied", level: "Observed"));
        await host.AppendAsync(Invalid("event-bad"));

        var result = await new ReviewCompletionProjection(host.Events).GetLatestAsync("conv-1");
        Assert.AreEqual("job-review-1", result.Latest!.JobId);
        Assert.AreEqual(1, result.Malformed);
        Assert.AreEqual("review-1", result.Latest.ReviewId);
    }

    [TestMethod]
    public async Task DuplicateEventIdsAreDeduplicated()
    {
        await using var host = await TestHost.CreateAsync();
        await host.AppendAsync(Completed("event-1", "review-1", revision: 1));
        await host.AppendAsync(Completed("event-1", "review-1", revision: 9)); // same id, later delivery
        var result = await new ReviewCompletionProjection(host.Events).GetLatestAsync("conv-1");
        Assert.AreEqual(1L, result.Latest!.Revision, "The same event id must not be projected twice with new content.");
    }

    [TestMethod]
    public async Task EmptyConversationReturnsNoResult_AndInvalidIdIsTyped()
    {
        await using var host = await TestHost.CreateAsync();
        var empty = await new ReviewCompletionProjection(host.Events).GetLatestAsync("conv-1");
        Assert.IsNull(empty.Latest);
        Assert.AreEqual(0, empty.Scanned);

        var invalid = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await new ReviewCompletionProjection(host.Events).GetLatestAsync(" "));
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, invalid.Code);
    }

    private static NewConversationEvent Completed(string eventId, string reviewId, long revision,
        string finding = "NeedsWork", string level = "Observed") => new(eventId, ConversationReviewCompletionSink.EventType,
        1, "ws-1", null, null, null, null, "goal-1", null, eventId,
        JsonSerializer.SerializeToElement(new
        {
            reviewId,
            jobId = "job-" + reviewId,
            finding,
            level,
            revision,
            artifactSetHash = "artifact-hash",
            acceptanceContractVersion = "acceptance-v1",
        }), SourceKind: ConversationEventSourceKind.Goal);

    private static NewConversationEvent Invalid(string eventId) => new(eventId, ConversationReviewCompletionSink.EventType,
        1, "ws-1", null, null, null, null, "goal-1", null, eventId,
        JsonSerializer.SerializeToElement(new { reviewId = "", finding = "Satisfied" }),
        SourceKind: ConversationEventSourceKind.Goal);

    private sealed class TestHost(SqliteConnection connection, string dbPath, ConversationEventStore events)
        : IAsyncDisposable
    {
        public ConversationEventStore Events { get; } = events;

        public Task AppendAsync(NewConversationEvent evt) =>
            Events.AppendAsync("conv-1", -1, [evt], EventWriteCondition.ForRun("review:test", 0), default);

        public static async Task<TestHost> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "pudding-review-projection-" + Guid.NewGuid().ToString("N") + ".db");
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
            return new(connection, dbPath, events);
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
