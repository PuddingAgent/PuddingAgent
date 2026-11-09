using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 01D adapter on real SQLite: known/unknown amounts are preserved (unknown is never zeroed), cancellation
/// flags round-trip, and a missing account is null rather than a fabricated empty budget.
/// </summary>
[TestClass]
public sealed class ExecutionBudgetApiAdapterTests
{
    [TestMethod]
    public async Task MapsKnownAndUnknownAmounts_AndMissingAccountIsNull()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 5m),
            new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd");
        var reservation = await host.Store.ReserveAsync(new("op-1", "attempt-1", new("goal-1"), [new("goal-1")],
            new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "model-a", "rev-1", "price-1",
            100, 400, 1.5m, "usd", DateTimeOffset.UtcNow.AddMinutes(5)));
        await host.Store.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 1, 0));
        await host.Store.MarkUnknownAsync(new(reservation.ReservationId, "attempt-1", 1, "timeout"));

        var adapter = new ExecutionBudgetApiAdapter(host.Store);
        var snapshot = await adapter.GetAsync("goal-1");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(1000L, snapshot.Limits.MaxOutputTokens);
        Assert.AreEqual(400L, snapshot.Unknown.OutputTokens);
        Assert.AreEqual(1.5m, snapshot.Unknown.Cost);
        Assert.AreEqual(0m, snapshot.Actual.Cost);
        Assert.IsFalse(snapshot.Cancelled);

        Assert.IsNull(await adapter.GetAsync("missing"));
        Assert.IsNull(await adapter.GetAsync(" "));
    }

    [TestMethod]
    public async Task CancellationFlagsRoundTrip()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000),
            new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd");
        await host.Store.CancelAsync(new(new("goal-1"), "user.cancelled", 1));
        var snapshot = await new ExecutionBudgetApiAdapter(host.Store).GetAsync("goal-1");
        Assert.IsTrue(snapshot!.Cancelled);
        Assert.IsTrue(snapshot.CancellationRevision > 0);
        Assert.AreEqual("user.cancelled", snapshot.ReasonCode);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ExecutionBudgetStore store) : IAsyncDisposable
    {
        public ExecutionBudgetStore Store { get; } = store;

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
            return new(connection, new ExecutionBudgetStore(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
