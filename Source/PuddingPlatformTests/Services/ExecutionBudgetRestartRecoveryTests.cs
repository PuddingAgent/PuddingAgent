using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingModels;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using ModelUsage = PuddingExecutionResources.Contracts.ModelUsage;

namespace PuddingPlatformTests.Services;

/// <summary>
/// T6 restart path on a file-backed SQLite database: a new store/gate over the same file still sees dispatched and
/// unknown attempts, can settle/unknown them exactly once, and never resets the ledger.
/// </summary>
[TestClass]
public sealed class ExecutionBudgetRestartRecoveryTests
{
    [TestMethod]
    public async Task ReopenedDatabase_RecoversDispatchedAttempt_AndSettlesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "budget-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var account = new BudgetAccountId("account-restart");
            await using (var first = await Host.CreateAsync(path))
            {
                await first.Store.CreateAccountAsync(account, null, new BudgetLimits(MaxOutputTokens: 1_000_000, MaxCost: 10m),
                    new BudgetIdentity(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd",
                    BudgetEnforcementMode.Enforced);
                var permit = await first.Gate.BeginAsync(Request(account, "attempt-1"));
                Assert.IsTrue(permit.Gated);
            }

            await using (var restarted = await Host.CreateAsync(path))
            {
                var unsettled = await restarted.Store.ListUnsettledAsync(account);
                Assert.AreEqual(1, unsettled.Length, "The dispatched attempt survives a restart.");
                var permit = new ModelAttemptBudgetPermit("reservation-1", "attempt-1", true, false, 1, 0, null, 0m);
                // The reservation id is assigned by the store; recover it from the candidate and settle by attempt.
                var candidate = unsettled[0];
                permit = permit with { ReservationId = candidate.ReservationId, OwnerEpoch = candidate.OwnerEpoch };
                await restarted.Store.SettleAsync(new SettleBudget(candidate.ReservationId, "attempt-1",
                    candidate.OwnerEpoch, new ModelUsage(10, 2), 0.5m, "usd", "receipt-restarted",
                    DateTimeOffset.UtcNow));
                var snapshot = await restarted.Store.ReadAsync(account);
                Assert.AreEqual(0, snapshot.ActiveAttempts);
                Assert.AreEqual(0m, snapshot.ReservedCost);
                Assert.AreEqual(0.5m, snapshot.Cost);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task ReopenedDatabase_UnknownAttempt_CanStillSettleOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "budget-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var account = new BudgetAccountId("account-restart-unknown");
            await using (var first = await Host.CreateAsync(path))
            {
                await first.Store.CreateAccountAsync(account, null, new BudgetLimits(MaxOutputTokens: 1_000_000, MaxCost: 10m),
                    new BudgetIdentity(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd",
                    BudgetEnforcementMode.Enforced);
                var permit = await first.Gate.BeginAsync(Request(account, "attempt-2"));
                await first.Gate.MarkUnknownAsync(permit, "llm.cancelled");
            }

            await using (var restarted = await Host.CreateAsync(path))
            {
                var unsettled = await restarted.Store.ListUnsettledAsync(account);
                Assert.AreEqual(1, unsettled.Length);
                var candidate = unsettled[0];
                await restarted.Store.SettleAsync(new SettleBudget(candidate.ReservationId, "attempt-2",
                    candidate.OwnerEpoch, new ModelUsage(10, 2), 0.25m, "usd", "receipt-unknown",
                    DateTimeOffset.UtcNow));
                var snapshot = await restarted.Store.ReadAsync(account);
                Assert.AreEqual(0, snapshot.ActiveAttempts);
                Assert.AreEqual(0, snapshot.UnknownOutputTokens);
                Assert.AreEqual(0m, snapshot.UnknownCost);
                Assert.AreEqual(0.25m, snapshot.Cost);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static ModelAttemptBudgetRequest Request(BudgetAccountId account, string attemptId) => new(
        account, [], new BudgetIdentity(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"),
        attemptId, "model-a", "rev-1", "pricing-v1", 1_000_000, 100_000, "usd", DateTimeOffset.UtcNow);

    private sealed class Quotes : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(new("quote-1", request.ModelId, request.Revision, "usd",
                "per-1m-tokens", 1m, 1m, 2m, 1_000_000, request.DispatchAt.AddHours(-1), request.DispatchAt.AddHours(1),
                "utc", "declared", false, 10m, "pricing-v1", DateTimeOffset.UtcNow));
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class Host(SqliteConnection connection, ExecutionBudgetStore store,
        ModelAttemptBudgetGate gate) : IAsyncDisposable
    {
        public ExecutionBudgetStore Store { get; } = store;
        public ModelAttemptBudgetGate Gate { get; } = gate;

        public static async Task<Host> CreateAsync(string path)
        {
            var connection = new SqliteConnection("DataSource=" + path);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();
            var store = new ExecutionBudgetStore(factory);
            return new(connection, store, new ModelAttemptBudgetGate(store, new Quotes()));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
