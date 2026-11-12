using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Reviews;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 × PA-01 recovery seam on real SQLite: an expired Running review with a dispatched but unsettled budget attempt
/// is not retried (that would pay twice); once the attempt is marked unknown the worker reclaims and completes it.
/// </summary>
[TestClass]
public sealed class ReviewBudgetReconciliationTests
{
    [TestMethod]
    public async Task ExpiredRunningWithDispatchedAttempt_IsDeferredUntilTheAttemptIsReconciled()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record(), "op-1");
        await host.Store.ClaimAsync("job-1", 1, DateTimeOffset.UtcNow.AddMilliseconds(40), 0);
        await Task.Delay(100);
        var reservation = await host.ReserveAttemptAsync();

        var deferred = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, deferred.Executed);
        Assert.AreEqual(1, deferred.Skipped, "An unsettled dispatched attempt blocks the retry.");
        Assert.AreEqual(ReviewJobState.Running, (await host.Store.GetAsync("job-1"))!.State);

        await host.Budget.MarkUnknownAsync(new MarkUnknownBudget(reservation.ReservationId, "job-1:1", 1,
            "review.retry_after_restart"));
        var recovered = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, recovered.Executed,
            $"claimed={recovered.Claimed} skipped={recovered.Skipped} failed={recovered.Failed}");
        Assert.AreEqual(ReviewJobState.Completed, (await host.Store.GetAsync("job-1"))!.State);
    }

    private static ReviewJobRecord Record() => ReviewJobWorkerTests.Record("job-1", "review-1");

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ReviewJobStore store, ExecutionBudgetStore budget,
        ReviewJobWorker worker) : IAsyncDisposable
    {
        public ReviewJobStore Store { get; } = store;
        public ExecutionBudgetStore Budget { get; } = budget;
        public ReviewJobWorker Worker { get; } = worker;

        public async Task<BudgetReservation> ReserveAttemptAsync()
        {
            var account = new BudgetAccountId("budget-1");
            await Budget.CreateAccountAsync(account, null, new(MaxOutputTokens: 100_000, MaxCost: 10m),
                new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd");
            var reservation = await Budget.ReserveAsync(new ReserveBudget("op-1:job-1:1", "job-1:1", account,
                [account], new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "model-a",
                "rev-1", "pricing-v1", 1_000, 1_000, 1m, "usd", DateTimeOffset.UtcNow.AddMinutes(10)));
            await Budget.MarkDispatchedAsync(new DispatchBudget(reservation.ReservationId, "job-1:1", 1, 0));
            return reservation;
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
                await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var store = new ReviewJobStore(factory);
            var budget = new ExecutionBudgetStore(factory);
            var coordinator = new ReviewJobCoordinator(store, new ReviewJobWorkerTests.FakeReader(),
                new ReviewJobWorkerTests.FakeExecutor());
            var worker = new ReviewJobWorker(store, coordinator, reconciler: new ReviewAttemptReconciler(budget));
            return new(connection, store, budget, worker);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
