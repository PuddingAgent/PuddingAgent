using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services.ExecutionBudgets;

/// <summary>
/// PA-01 S5: durable budget ledger on real SQLite. Verifies ancestor-atomic reservation, idempotency/conflict,
/// cancellation linearization, owner fencing, once-only settlement, unknown retention (never refunded), release,
/// subtree cancellation and recovery candidates — and that state survives a new DbContext.
/// </summary>
[TestClass]
public sealed class ExecutionBudgetStoreTests
{
    [TestMethod]
    public async Task CreateAccount_AndRead_ReturnTheHostBoundState()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 5m, MaxCalls: 10),
            Identity(), "usd");
        var snapshot = await host.Store.ReadAsync(new("goal-1"));
        Assert.AreEqual(1000L, snapshot.Limits.MaxOutputTokens);
        Assert.AreEqual(10, snapshot.Limits.MaxCalls);
        Assert.AreEqual("usd", snapshot.CostUnit);
        Assert.AreEqual(0L, snapshot.Revision);
        Assert.IsFalse(snapshot.Cancelled);
    }

    [TestMethod]
    public async Task Reserve_IsAncestorAtomic_AndIdempotent()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 5m), Identity(), "usd");
        await host.Store.CreateAccountAsync(new("work-1"), new("goal-1"), new(MaxOutputTokens: 600, MaxCost: 3m), Identity(), "usd");
        var command = Command("work-1", "op-1", "attempt-1", output: 100, cost: 0.5m);

        var reservation = await host.Store.ReserveAsync(command);
        Assert.AreEqual("attempt-1", reservation.AttemptId);
        Assert.AreEqual(0L, reservation.CancellationRevision);

        var replay = await host.Store.ReserveAsync(command);
        Assert.AreEqual(reservation.ReservationId, replay.ReservationId, "An exact replay is accepted once.");

        var root = await host.Store.ReadAsync(new("goal-1"));
        var leaf = await host.Store.ReadAsync(new("work-1"));
        Assert.AreEqual(100L, root.ReservedOutputTokens);
        Assert.AreEqual(100L, leaf.ReservedOutputTokens);
        Assert.AreEqual(1, root.Calls);
        Assert.AreEqual(1, leaf.ActiveAttempts);

        var conflict = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.ReserveAsync(command with { MaxCost = 0.7m }));
        Assert.AreEqual(BudgetErrorCodes.Conflict, conflict.Code);
    }

    [TestMethod]
    public async Task Reserve_RejectsExhaustionConcurrencyPathAndCurrency()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 100, MaxCost: 1m,
            MaxConcurrentAttempts: 1), Identity(), "usd");
        await host.Store.CreateAccountAsync(new("work-1"), new("goal-1"), new(MaxOutputTokens: 100), Identity(), "eur");

        var path = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.ReserveAsync(Command("goal-1", "op-p", "attempt-p", 10, 0.1m) with { AccountPath = Path("goal-1", "work-1") }));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, path.Code);

        var currency = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.ReserveAsync(Command("work-1", "op-c", "attempt-c", 10, 0.1m) with { AccountPath = [new("goal-1"), new("work-1")] }));
        Assert.AreEqual(BudgetErrorCodes.CurrencyMismatch, currency.Code);

        await host.Store.ReserveAsync(Command("goal-1", "op-1", "attempt-1", 60, 0.6m));
        var concurrency = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.ReserveAsync(Command("goal-1", "op-2", "attempt-2", 10, 0.1m)));
        Assert.AreEqual(BudgetErrorCodes.Concurrency, concurrency.Code);
    }

    [TestMethod]
    public async Task Dispatch_RequiresFreshCancellationRevision_AndFencesOwner()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000), Identity(), "usd");
        var reservation = await host.Store.ReserveAsync(Command("goal-1", "op-1", "attempt-1", 100, 0.1m));

        var staleRevision = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 1, ExpectedCancellationRevision: 99)));
        Assert.AreEqual(BudgetErrorCodes.Conflict, staleRevision.Code);

        var permit = await host.Store.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 1, 0));
        Assert.AreEqual(1L, permit.OwnerEpoch);
        Assert.AreEqual(0L, permit.CancellationRevision);

        await host.Store.CancelAsync(new(new("goal-1"), "user.cancelled", 1));
        var cancelled = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.MarkDispatchedAsync(
                new(reservation.ReservationId, "attempt-1", 1, ExpectedCancellationRevision: 0)));
        Assert.AreEqual(BudgetErrorCodes.DispatchInvalid, cancelled.Code, "A dispatched attempt is not dispatched twice.");
    }

    [TestMethod]
    public async Task Settle_IsOnceOnly_IdempotentAndRejectsStaleOrDifferentReceipts()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 5m), Identity(), "usd");
        await host.Store.CreateAccountAsync(new("work-1"), new("goal-1"), new(MaxOutputTokens: 500), Identity(), "usd");
        var reservation = await host.Store.ReserveAsync(Command("work-1", "op-1", "attempt-1", 100, 0.5m));
        await host.Store.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 2, 0));
        var settle = new SettleBudget(reservation.ReservationId, "attempt-1", 2, new(20, 30, 5), 0.3m, "usd", "receipt-1",
            DateTimeOffset.UtcNow);

        var staleOwner = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.SettleAsync(settle with { OwnerEpoch = 1 }));
        Assert.AreEqual(BudgetErrorCodes.OwnerStale, staleOwner.Code);

        var receipt = await host.Store.SettleAsync(settle);
        Assert.IsFalse(receipt.Overrun);
        Assert.IsTrue(receipt.Revision > 0);

        var replay = await host.Store.SettleAsync(settle);
        Assert.AreEqual(receipt.Revision, replay.Revision);
        var root = await host.Store.ReadAsync(new("goal-1"));
        Assert.AreEqual(30L, root.OutputTokens, "A replay must not double-count usage.");
        Assert.AreEqual(0m, root.ReservedCost);

        var different = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.SettleAsync(settle with { ActualCost = 0.4m }));
        Assert.AreEqual(BudgetErrorCodes.SettlementConflict, different.Code);
    }

    [TestMethod]
    public async Task MarkUnknown_RetainsUnknownAmounts_AndSettleFromUnknownClearsThem()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 5m), Identity(), "usd");
        var reservation = await host.Store.ReserveAsync(Command("goal-1", "op-1", "attempt-1", 400, 1.5m));
        await host.Store.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 1, 0));

        var unknown = await host.Store.MarkUnknownAsync(new(reservation.ReservationId, "attempt-1", 1, "timeout"));
        Assert.AreEqual(400L, unknown.UnknownOutputTokens);
        Assert.AreEqual(1.5m, unknown.UnknownCost);
        Assert.AreEqual(0L, unknown.ReservedOutputTokens, "Unknown must leave the reservation but stay unavailable.");
        Assert.AreEqual(BudgetErrorCodes.UsageUnknown, unknown.ReasonCode);

        var settle = new SettleBudget(reservation.ReservationId, "attempt-1", 1, new(10, 20, 0), 0.2m, "usd", "receipt-x",
            DateTimeOffset.UtcNow);
        var receipt = await host.Store.SettleAsync(settle);
        Assert.IsTrue(receipt.FromUnknown);
        var after = await host.Store.ReadAsync(new("goal-1"));
        Assert.AreEqual(0L, after.UnknownOutputTokens);
        Assert.AreEqual(20L, after.OutputTokens);
        Assert.AreEqual(0m, after.ReservedCost);
    }

    [TestMethod]
    public async Task Release_RefundsOnlyUndispatchedAttempts()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 5m), Identity(), "usd");
        var reservation = await host.Store.ReserveAsync(Command("goal-1", "op-1", "attempt-1", 100, 0.5m));
        var snapshot = await host.Store.ReleaseAsync(new(reservation.ReservationId, "attempt-1"));
        Assert.AreEqual(0L, snapshot.ReservedOutputTokens);
        Assert.AreEqual(0, snapshot.ActiveAttempts);

        var replay = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.ReleaseAsync(new(reservation.ReservationId, "attempt-1")));
        Assert.AreEqual(BudgetErrorCodes.DispatchInvalid, replay.Code);
    }

    [TestMethod]
    public async Task Cancel_CascadesSubtree_CountsRunningAndIsIdempotent()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 5000), Identity(), "usd");
        await host.Store.CreateAccountAsync(new("work-1"), new("goal-1"), new(MaxOutputTokens: 3000), Identity(), "usd");
        await host.Store.CreateAccountAsync(new("work-2"), new("goal-1"), new(MaxOutputTokens: 3000), Identity(), "usd");
        var reservation = await host.Store.ReserveAsync(Command("work-1", "op-1", "attempt-1", 100, 0.1m));
        await host.Store.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 1, 0));

        var first = await host.Store.CancelAsync(new(new("goal-1"), "user.cancelled", 1));
        Assert.AreEqual(1, first.RunningAttempts);
        Assert.IsFalse(first.AlreadyCancelled);
        Assert.IsTrue((await host.Store.ReadAsync(new("work-2"))).Cancelled);
        Assert.IsTrue((await host.Store.ReadAsync(new("work-1"))).Cancelled);

        var second = await host.Store.CancelAsync(new(new("goal-1"), "user.cancelled", 1));
        Assert.IsTrue(second.AlreadyCancelled);
        Assert.AreEqual(first.CancellationRevision, second.CancellationRevision);

        var rejected = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.ReserveAsync(Command("work-2", "op-2", "attempt-2", 10, 0.1m)));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, rejected.Code);
    }

    [TestMethod]
    public async Task RecoveryCandidates_IncludeUnsettledStates_AndDisappearAfterSettlement()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 5000, MaxConcurrentAttempts: 5), Identity(), "usd");
        var pending = await host.Store.ReserveAsync(Command("goal-1", "op-1", "attempt-1", 100, 0.1m));
        var dispatched = await host.Store.ReserveAsync(Command("goal-1", "op-2", "attempt-2", 100, 0.1m));
        await host.Store.MarkDispatchedAsync(new(dispatched.ReservationId, "attempt-2", 1, 0));
        var unknown = await host.Store.ReserveAsync(Command("goal-1", "op-3", "attempt-3", 100, 0.1m));
        await host.Store.MarkDispatchedAsync(new(unknown.ReservationId, "attempt-3", 1, 0));
        await host.Store.MarkUnknownAsync(new(unknown.ReservationId, "attempt-3", 1, "network"));

        var candidates = await host.Store.ListUnsettledAsync(new("goal-1"));
        Assert.AreEqual(3, candidates.Length);
        CollectionAssert.AreEquivalent(new[] { BudgetAttemptPhase.Pending, BudgetAttemptPhase.Dispatched, BudgetAttemptPhase.Unknown },
            candidates.Select(c => c.Phase).ToArray());

        await host.Store.SettleAsync(new(dispatched.ReservationId, "attempt-2", 1, new(1, 1, 0), 0.01m, "usd", "r",
            DateTimeOffset.UtcNow));
        await host.Store.ReleaseAsync(new(pending.ReservationId, "attempt-1"));
        var remaining = await host.Store.ListUnsettledAsync(new("goal-1"));
        Assert.AreEqual(1, remaining.Length);
        Assert.AreEqual(BudgetAttemptPhase.Unknown, remaining[0].Phase);
    }

    [TestMethod]
    public async Task State_SurvivesANewDbContextOnTheSameDatabase()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(Options(connection));
        using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
        }
        var first = new ExecutionBudgetStore(factory);
        await first.CreateAccountAsync(new("goal-1"), null, new(MaxOutputTokens: 1000, MaxCost: 2m), Identity(), "usd");
        var reservation = await first.ReserveAsync(Command("goal-1", "op-1", "attempt-1", 100, 0.5m));
        await first.MarkDispatchedAsync(new(reservation.ReservationId, "attempt-1", 1, 0));

        var second = new ExecutionBudgetStore(factory);
        var snapshot = await second.ReadAsync(new("goal-1"));
        Assert.AreEqual(100L, snapshot.ReservedOutputTokens);
        var candidates = await second.ListUnsettledAsync(new("goal-1"));
        Assert.AreEqual(1, candidates.Length);
        Assert.AreEqual(reservation.AttemptId, candidates[0].AttemptId);
    }

    private static BudgetIdentity Identity() => new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1");

    private static ImmutableArray<BudgetAccountId> Path(params string[] ids) => [.. ids.Select(id => new BudgetAccountId(id))];

    private static ReserveBudget Command(string leaf, string operationId, string attemptId, long output, decimal cost,
        ImmutableArray<BudgetAccountId>? path = null) => new(operationId, attemptId, new(leaf),
        path ?? (leaf == "goal-1" ? Path("goal-1") : Path("goal-1", leaf)), Identity(), "model-a", "rev-1", "price-1",
        100, output, cost, "usd", DateTimeOffset.UtcNow.AddMinutes(5));

    private static DbContextOptions<PlatformDbContext> Options(SqliteConnection connection)
        => new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;

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
            var factory = new TestDbContextFactory(Options(connection));
            using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            return new(connection, new ExecutionBudgetStore(factory));
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
