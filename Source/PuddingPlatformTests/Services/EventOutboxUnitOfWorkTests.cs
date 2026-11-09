using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Eventing;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-07A S5 Platform UoW: business state and outbox facts commit in one transaction. An exception or a duplicate
/// event id rolls back both; the fact gets a committed sequence only at commit.
/// </summary>
[TestClass]
public sealed class EventOutboxUnitOfWorkTests
{
    [TestMethod]
    public async Task BusinessStateAndOutboxFacts_CommitTogether()
    {
        await using var host = await TestHost.CreateAsync();
        var result = await host.Uow.ExecuteAsync(async (db, outbox, ct) =>
        {
            db.GoalRuns.Add(Goal("goal-1"));
            await outbox.AppendAsync(Fact("event-1", "ws-1"), ct);
            await outbox.AppendAsync(Fact("event-2", "ws-1"), ct);
            await db.SaveChangesAsync(ct);
            return "done";
        });
        Assert.AreEqual("done", result);
        Assert.AreEqual(1, await host.GoalCountAsync());
        var rows = await host.OutboxAsync();
        CollectionAssert.AreEqual(new[] { "event-1", "event-2" }, rows.Select(r => r.EventId).ToArray());
        CollectionAssert.AreEqual(new[] { 1L, 2L }, rows.Select(r => r.Sequence).ToArray());
    }

    [TestMethod]
    public async Task Exception_RollsBackBusinessStateAndFacts()
    {
        await using var host = await TestHost.CreateAsync();
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await host.Uow.ExecuteAsync<int>(async (db, outbox, ct) =>
            {
                db.GoalRuns.Add(Goal("goal-1"));
                await outbox.AppendAsync(Fact("event-1", "ws-1"), ct);
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("business failure");
            }));
        Assert.AreEqual("business failure", failure.Message);
        Assert.AreEqual(0, await host.GoalCountAsync());
        Assert.AreEqual(0, (await host.OutboxAsync()).Length);
    }

    [TestMethod]
    public async Task DuplicateEventId_RejectsTheWholeTransaction()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Uow.ExecuteAsync(async (db, outbox, ct) =>
        {
            await outbox.AppendAsync(Fact("event-dup", "ws-1"), ct);
            return 0;
        });
        var duplicate = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Uow.ExecuteAsync(async (db, outbox, ct) =>
            {
                db.GoalRuns.Add(Goal("goal-2"));
                await outbox.AppendAsync(Fact("event-dup", "ws-1"), ct);
                await db.SaveChangesAsync(ct);
                return 0;
            }));
        Assert.AreEqual(EventingErrorCodes.DuplicateEvent, duplicate.Code);
        Assert.AreEqual(0, await host.GoalCountAsync(), "A rejected transaction commits no business state.");
        Assert.AreEqual(1, (await host.OutboxAsync()).Length);
    }

    private static GoalRunEntity Goal(string goalRunId) => new()
    {
        GoalRunId = goalRunId,
        WorkspaceId = "ws-1",
        CurrentConversationId = "conv-1",
        AgentInstanceId = "agent-1",
        Objective = "Build both files.",
        ObjectiveVersion = 2,
        ActivationEpoch = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static OutboxFact Fact(string eventId, string workspaceId) => new(eventId, workspaceId, "review.completed",
        1, workspaceId, "ref://" + eventId, "sha-" + eventId, "review", DateTimeOffset.UtcNow);

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory,
        EventOutboxUnitOfWork uow) : IAsyncDisposable
    {
        public EventOutboxUnitOfWork Uow { get; } = uow;

        public async Task<int> GoalCountAsync()
        {
            await using var db = factory.CreateDbContext();
            return await db.GoalRuns.CountAsync();
        }

        public async Task<OutboxEventEntity[]> OutboxAsync()
        {
            await using var db = factory.CreateDbContext();
            return await db.EventOutbox.AsNoTracking().OrderBy(e => e.Sequence).ToArrayAsync();
        }

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            return new(connection, factory, new EventOutboxUnitOfWork(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
