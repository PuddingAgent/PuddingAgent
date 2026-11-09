using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 P2 closure over the real persistent ledger, reached through the same port Runtime uses: an Enforced root
/// reserves before dispatch, settles actual usage, and a sibling attempt cannot overdraw the shared root budget.
/// </summary>
[TestClass]
public sealed class BudgetBoundaryClosureTests
{
    [TestMethod]
    public async Task Port_ReservesBeforeDispatch_SettlesUsage_AndRejectsSiblingOverdraw()
    {
        await using var host = await Host.CreateAsync();
        var claim = Claim("attempt-1");
        var grant = await host.Port.BeginAsync(claim)
            ?? throw new AssertFailedException("An Enforced root must return a grant.");
        Assert.IsTrue(grant.Shadow == false);
        var reserved = await host.Budget.ReadAsync(host.Account);
        Assert.IsTrue(reserved.ReservedCost > 0m, "The reservation must be visible before dispatch.");
        Assert.AreEqual(1, reserved.ActiveAttempts);

        var sibling = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
            await host.Port.BeginAsync(Claim("attempt-2")));
        Assert.AreEqual(BudgetErrorCodes.Exhausted, sibling.Code);

        await host.Port.SettleAsync(grant, new ModelUsage(InputTokens: 500, OutputTokens: 100), "receipt-1");
        var settled = await host.Budget.ReadAsync(host.Account);
        Assert.AreEqual(0, settled.ActiveAttempts);
        Assert.AreEqual(0m, settled.ReservedCost);
        Assert.IsTrue(settled.Cost > 0m);
        Assert.AreEqual(0m, settled.UnknownCost);
    }

    [TestMethod]
    public async Task ConcurrencyLimit_RejectsSecondActiveAttempt()
    {
        await using var host = await Host.CreateAsync(maxConcurrentAttempts: 1, maxCost: 1m);
        _ = await host.Port.BeginAsync(Claim("attempt-1"));
        var failure = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
            await host.Port.BeginAsync(Claim("attempt-2")));
        Assert.AreEqual(BudgetErrorCodes.Concurrency, failure.Code);
    }

    [TestMethod]
    public async Task CancelledRoot_BlocksNewDispatch_AndUnknownIsNotRefunded()
    {
        await using var host = await Host.CreateAsync();
        var grant = await host.Port.BeginAsync(Claim("attempt-1"));
        await host.Budget.CancelAsync(new(host.Account, "user.cancelled", 1));
        var failure = await Assert.ThrowsExactlyAsync<ModelAttemptBudgetException>(async () =>
            await host.Port.BeginAsync(Claim("attempt-2")));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, failure.Code);
        await host.Port.MarkUnknownAsync(grant!, "provider_timeout");
        var snapshot = await host.Budget.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
        Assert.IsTrue(snapshot.UnknownCost > 0m, "A dispatched-but-unsettled attempt must remain Unknown, never refunded.");
    }
    private static ModelAttemptBudgetClaim Claim(string attemptId) => new("budget-close-1", [],
        new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), attemptId, "model-1", "rev-42",
        "pricing-v9", MaxInputTokens: 1_000, MaxOutputTokens: 1_000, CostUnit: "usd", DispatchAt: DateTimeOffset.UtcNow);

    private sealed class Host(SqliteConnection connection, ExecutionBudgetStore budget, BudgetAccountId account,
        PlatformModelAttemptBudgetPort port) : IAsyncDisposable
    {
        public ExecutionBudgetStore Budget { get; } = budget;
        public BudgetAccountId Account { get; } = account;
        public PlatformModelAttemptBudgetPort Port { get; } = port;

        public static async Task<Host> CreateAsync(int maxConcurrentAttempts = 2, decimal maxCost = 0.004m)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var budget = new ExecutionBudgetStore(factory);
            var account = new BudgetAccountId("budget-close-1");
            await budget.CreateAccountAsync(account, null,
                new(MaxOutputTokens: 100_000, MaxCost: maxCost, MaxConcurrentAttempts: maxConcurrentAttempts),
                new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd",
                BudgetEnforcementMode.Enforced);
            var gate = new ModelAttemptBudgetGate(budget, new Quotes());
            return new(connection, budget, account, new PlatformModelAttemptBudgetPort(gate));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();

        private sealed class Factory(DbContextOptions<PlatformDbContext> options)
            : IDbContextFactory<PlatformDbContext>
        {
            public PlatformDbContext CreateDbContext() => new(options);
        }
    }

    private sealed class Quotes : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(new("quote-1", request.ModelId, request.Revision, "usd",
                "per-1m-tokens", 1m, 0.5m, 2m, 1_000_000, request.DispatchAt.AddHours(-1),
                request.DispatchAt.AddHours(1), "utc", "declared", false, 10m, "pricing-v9", DateTimeOffset.UtcNow));
    }
}
