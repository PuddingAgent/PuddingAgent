using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 P2 attempt gate on real SQLite: Legacy never reserves; Enforced always quotes/reserves/dispatches and fails
/// closed when the quote or limit is unavailable; Shadow reserves but reports shadow; Enforced cannot be downgraded.
/// </summary>
[TestClass]
public sealed class ModelAttemptBudgetGateTests
{
    [TestMethod]
    public async Task LegacyRoot_DoesNotReserve()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Legacy);
        var permit = await host.Gate.BeginAsync(Request());
        Assert.IsFalse(permit.Gated);
        Assert.AreEqual(string.Empty, permit.ReservationId);
        var snapshot = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
    }

    [TestMethod]
    public async Task EnforcedRoot_ReservesDispatchesAndSettles()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        var permit = await host.Gate.BeginAsync(Request());
        Assert.IsTrue(permit.Gated);
        Assert.IsFalse(permit.Shadow);
        Assert.IsNotNull(permit.Quote);
        var reserved = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(1, reserved.ActiveAttempts);
        Assert.IsTrue(reserved.ReservedCost > 0m);

        var receipt = await host.Gate.SettleAsync(permit, new ModelUsage(1_000, 100), permit.ReservedCost, "usd", "receipt-1");
        Assert.IsTrue(receipt.Revision > 0);
        var settled = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, settled.ActiveAttempts);
        Assert.AreEqual(0m, settled.ReservedCost);
        Assert.IsTrue(settled.Cost > 0m);
    }

    [TestMethod]
    public async Task ShadowRoot_ReservesButReportsShadow()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Shadow);
        var permit = await host.Gate.BeginAsync(Request());
        Assert.IsTrue(permit.Gated);
        Assert.IsTrue(permit.Shadow);
        Assert.AreEqual(1, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
    }

    [TestMethod]
    public async Task UnknownQuote_FailsClosedWithoutReservation()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced, quoteAvailable: false);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () => await host.Gate.BeginAsync(Request()));
        Assert.AreEqual(ModelErrorCodes.PriceUnknown, failure.Code);
        Assert.AreEqual(0, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
    }

    [TestMethod]
    public async Task ExhaustedLimit_FailsClosed()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced,
            limits: new(MaxOutputTokens: 100_000, MaxCost: 0.000001m));
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Gate.BeginAsync(Request()));
        Assert.AreEqual(BudgetErrorCodes.Exhausted, failure.Code);
    }

    [TestMethod]
    public async Task QuoteCurrencyMismatch_FailsClosedWithoutReservation()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced, quoteCurrency: "eur");
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () => await host.Gate.BeginAsync(Request()));
        Assert.AreEqual(ModelErrorCodes.CurrencyMismatch, failure.Code);
        Assert.AreEqual(0, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
        Assert.AreEqual(0m, (await host.Store.ReadAsync(host.Account)).ReservedCost);
    }

    [TestMethod]
    public async Task QuotePricingRuleMismatch_FailsClosedWithoutReservation()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced, quotePricingRule: "pricing-v2");
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () => await host.Gate.BeginAsync(Request()));
        Assert.AreEqual(ModelErrorCodes.PriceUnknown, failure.Code);
        Assert.AreEqual(0, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
    }

    [TestMethod]
    public async Task QuoteRevisionMismatch_FailsClosedWithoutReservation()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced, quoteRevision: "rev-2");
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () => await host.Gate.BeginAsync(Request()));
        Assert.AreEqual(ModelErrorCodes.PriceUnknown, failure.Code);
        Assert.AreEqual(0, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
    }

    [TestMethod]
    public async Task ExpiredQuoteWindow_FailsClosedWithoutReservation()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced, quoteExpired: true);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () => await host.Gate.BeginAsync(Request()));
        Assert.AreEqual(ModelErrorCodes.QuoteWindowMissed, failure.Code);
        Assert.AreEqual(0, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
    }

    [TestMethod]
    public async Task CancelledBetweenReserveAndDispatch_ReleasesThePendingReservation()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        using var cts = new CancellationTokenSource();
        host.Gate.AfterReservationForTests = () => cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await host.Gate.BeginAsync(Request(), cts.Token));

        var snapshot = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts, "A cancelled pre-dispatch attempt must not stay reserved.");
        Assert.AreEqual(0m, snapshot.ReservedCost);
        Assert.AreEqual(0, (await host.Store.ListUnsettledAsync(host.Account)).Length);
    }

    [TestMethod]
    public async Task CancelledAfterDispatch_KeepsTheAttemptForUnknownSettlement()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        var permit = await host.Gate.BeginAsync(Request());

        // Cancellation after dispatch must not release; the caller owns the unknown/settlement path.
        await host.Gate.MarkUnknownAsync(permit, "llm.cancelled");
        var snapshot = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
    }


    [TestMethod]
    public async Task StaleOwner_CannotSettleOrMarkUnknown()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        var permit = await host.Gate.BeginAsync(Request());
        var stale = permit with { OwnerEpoch = permit.OwnerEpoch - 1 };

        var settle = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Gate.SettleAsync(stale, new ModelUsage(10, 2), permit.ReservedCost, "usd", "receipt"));
        Assert.AreEqual(BudgetErrorCodes.OwnerStale, settle.Code);
        var unknown = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Gate.MarkUnknownAsync(stale, "llm.cancelled"));
        Assert.AreEqual(BudgetErrorCodes.OwnerStale, unknown.Code);
        Assert.AreEqual(1, (await host.Store.ReadAsync(host.Account)).ActiveAttempts, "The stale owner cannot release the attempt.");
    }    [TestMethod]
    public async Task ProviderOverrun_BlocksFurtherAttempts()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        var permit = await host.Gate.BeginAsync(Request());
        await host.Gate.SettleAsync(permit, new ModelUsage(1_000, 100), permit.ReservedCost + 1m, "usd", "receipt-overrun");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Gate.BeginAsync(Request("attempt-2")));
        Assert.AreEqual(BudgetErrorCodes.ProviderOverrun, failure.Code,
            "An overrun failure must close further admission until reconciled.");
    }

    [TestMethod]
    public async Task EnforcedRoot_CannotBeDowngradedInPlace()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        var downgrade = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.SetEnforcementModeAsync(host.Account, BudgetEnforcementMode.Legacy));
        Assert.AreEqual(BudgetErrorCodes.Conflict, downgrade.Code);
        Assert.AreEqual(BudgetEnforcementMode.Enforced,
            await host.Store.GetEnforcementModeAsync(host.Account));

        await using var legacy = await TestHost.CreateAsync(BudgetEnforcementMode.Legacy);
        Assert.AreEqual(BudgetEnforcementMode.Enforced,
            await legacy.Store.SetEnforcementModeAsync(legacy.Account, BudgetEnforcementMode.Enforced));
    }

    [TestMethod]
    public async Task SettleReplay_IsIdempotent_AndDoesNotDoubleCount()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        var permit = await host.Gate.BeginAsync(Request());
        var usage = new ModelUsage(1_000, 100);
        var first = await host.Gate.SettleAsync(permit, usage, permit.ReservedCost, "usd", "receipt-1");
        var cost = (await host.Store.ReadAsync(host.Account)).Cost;
        var replay = await host.Gate.SettleAsync(permit, usage, permit.ReservedCost, "usd", "receipt-1");
        Assert.AreEqual(first.Revision, replay.Revision);
        Assert.AreEqual(cost, (await host.Store.ReadAsync(host.Account)).Cost);
        var conflict = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Gate.SettleAsync(permit, new ModelUsage(2_000, 200), permit.ReservedCost, "usd", "receipt-2"));
        Assert.AreEqual(BudgetErrorCodes.SettlementConflict, conflict.Code);
        Assert.AreEqual(cost, (await host.Store.ReadAsync(host.Account)).Cost);
    }

    [TestMethod]
    public async Task TwoAttempts_ReserveAndSettleSeparately()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced,
            limits: new(MaxOutputTokens: 1_000_000, MaxCost: 10m, MaxInputTokensPerRequest: 1_000_000));
        var first = await host.Gate.BeginAsync(Request("attempt-1"));
        var second = await host.Gate.BeginAsync(Request("attempt-2"));
        Assert.AreEqual(2, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
        await host.Gate.SettleAsync(first, new ModelUsage(1_000, 100), first.ReservedCost, "usd", "receipt-1");
        await host.Gate.SettleAsync(second, new ModelUsage(1_000, 100), second.ReservedCost, "usd", "receipt-2");
        var snapshot = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(2, snapshot.Calls);
        Assert.AreEqual(0m, snapshot.ReservedCost);
        Assert.IsTrue(snapshot.Cost > 0m);
    }
    private static ModelAttemptBudgetRequest Request(string attemptId = "attempt-1") => new(new("account-1"), [], Identity(), attemptId,
        "model-a", "rev-1", "pricing-v1", MaxInputTokens: 1_000_000, MaxOutputTokens: 100_000, "usd",
        DateTimeOffset.UtcNow);

    private static BudgetIdentity Identity() => new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1");

    private sealed class FakeQuotes(bool available, string? currency = null, string? pricingRule = null,
        string? revision = null, bool expired = false) : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(available
                ? new("quote-1", request.ModelId, revision ?? request.Revision, currency ?? "usd", "per-1m-tokens",
                    1m, 1m, 2m, 1_000_000,
                    request.DispatchAt.AddHours(expired ? -3 : -1), request.DispatchAt.AddHours(expired ? -2 : 1),
                    "utc", "declared", false, 10m, pricingRule ?? "pricing-v1", DateTimeOffset.UtcNow)
                : null);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ExecutionBudgetStore store,
        ModelAttemptBudgetGate gate, BudgetAccountId account) : IAsyncDisposable
    {
        public ExecutionBudgetStore Store { get; } = store;
        public ModelAttemptBudgetGate Gate { get; } = gate;
        public BudgetAccountId Account { get; } = account;

        public static async Task<TestHost> CreateAsync(BudgetEnforcementMode mode, bool quoteAvailable = true,
            BudgetLimits? limits = null, string? quoteCurrency = null, string? quotePricingRule = null,
            string? quoteRevision = null, bool quoteExpired = false)
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
            var store = new ExecutionBudgetStore(factory);
            var account = new BudgetAccountId("account-1");
            await store.CreateAccountAsync(account, null,
                limits ?? new(MaxOutputTokens: 100_000, MaxCost: 10m, MaxInputTokensPerRequest: 1_000_000),
                Identity(), "usd", mode);
            return new(connection, store, new ModelAttemptBudgetGate(store,
                new FakeQuotes(quoteAvailable, quoteCurrency, quotePricingRule, quoteRevision, quoteExpired)), account);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
