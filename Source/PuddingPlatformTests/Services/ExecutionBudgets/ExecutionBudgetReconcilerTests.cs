using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services.ExecutionBudgets;

/// <summary>
/// PA-01 S5 restart reconciliation on real SQLite: pending attempts are released, dispatched attempts become
/// unknown (retained, never refunded), existing unknown attempts are retained, and a second pass is a no-op.
/// </summary>
[TestClass]
public sealed class ExecutionBudgetReconcilerTests
{
    [TestMethod]
    public async Task PendingAttemptsAreReleased_WhileDispatchedBecomeUnknownAndUnknownAreRetained()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 10_000, MaxConcurrentAttempts: 5),
            Identity(), "usd");
        var pending = await host.Store.ReserveAsync(Command("op-pending", "attempt-pending"));
        var dispatched = await host.Store.ReserveAsync(Command("op-dispatched", "attempt-dispatched"));
        await host.Store.MarkDispatchedAsync(new(dispatched.ReservationId, "attempt-dispatched", 1, 0));
        var unknown = await host.Store.ReserveAsync(Command("op-unknown", "attempt-unknown"));
        await host.Store.MarkDispatchedAsync(new(unknown.ReservationId, "attempt-unknown", 1, 0));
        await host.Store.MarkUnknownAsync(new(unknown.ReservationId, "attempt-unknown", 1, "original-unknown"));

        var report = await host.ReconcileAsync();
        Assert.AreEqual(1, report.Accounts);
        Assert.AreEqual(1, report.ReleasedPending);
        Assert.AreEqual(1, report.MarkedUnknown);
        Assert.AreEqual(1, report.RetainedUnknown);

        var snapshot = await host.Store.ReadAsync(new("goal-1"));
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens, "The pending reservation was refunded.");
        Assert.AreEqual(800L, snapshot.UnknownOutputTokens, "Dispatched + previous unknown amounts stay unavailable.");
        var remaining = await host.Store.ListUnsettledAsync(new("goal-1"));
        Assert.AreEqual(2, remaining.Length, "Unknown attempts stay visible as recovery candidates.");
        Assert.IsTrue(remaining.All(c => c.Phase == BudgetAttemptPhase.Unknown));
    }

    [TestMethod]
    public async Task SecondPassIsANoOp()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000), Identity(), "usd");
        var pending = await host.Store.ReserveAsync(Command("op-1", "attempt-1"));
        await host.ReconcileAsync();
        var second = await host.ReconcileAsync();
        Assert.AreEqual(0, second.ReleasedPending);
        Assert.AreEqual(0, second.MarkedUnknown);
        Assert.AreEqual(0, second.RetainedUnknown);
        Assert.AreEqual(0L, (await host.Store.ReadAsync(new("goal-1"))).ReservedOutputTokens);
        Assert.AreEqual(0, (await host.Store.ListUnsettledAsync(new("goal-1"))).Length);
        _ = pending;
    }

    [TestMethod]
    public async Task SettledAttemptsAreNotTouched()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 2m), Identity(), "usd");
        var reservation = await host.Store.ReserveAsync(Command("op-1", "attempt-1"));
        await host.Store.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 1, 0));
        await host.Store.SettleAsync(new(reservation.ReservationId, "attempt-1", 1, new(5, 5, 0), 0.01m, "usd",
            "receipt-1", DateTimeOffset.UtcNow));

        var report = await host.ReconcileAsync();
        Assert.AreEqual(0, report.ReleasedPending + report.MarkedUnknown + report.RetainedUnknown);
        var snapshot = await host.Store.ReadAsync(new("goal-1"));
        Assert.AreEqual(5L, snapshot.OutputTokens);
    }

    private static BudgetIdentity Identity() => new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1");

    private static ReserveBudget Command(string operationId, string attemptId) => new(operationId, attemptId,
        new("goal-1"), [new("goal-1")], Identity(), "model-a", "rev-1", "price-1", 100, 400, 0.5m, "usd",
        DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ExecutionBudgetStore store,
        ExecutionBudgetReconciler reconciler) : IAsyncDisposable
    {
        public ExecutionBudgetStore Store { get; } = store;
        public Task<BudgetReconcileReport> ReconcileAsync() => reconciler.ReconcileAsync();

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            return new(connection, new ExecutionBudgetStore(factory),
                new ExecutionBudgetReconciler(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }


}
