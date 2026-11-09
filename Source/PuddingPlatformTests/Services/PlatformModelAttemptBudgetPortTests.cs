using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 P2 port adapter: a Legacy root returns no grant (runtime stays ungated); an Enforced root reserves and returns
/// price-frozen grant data; settlement records the actual usage cost under the frozen quote.
/// </summary>
[TestClass]
public sealed class PlatformModelAttemptBudgetPortTests
{
    [TestMethod]
    public async Task LegacyRoot_ReturnsNoGrant()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Legacy);
        Assert.IsNull(await host.Port.BeginAsync(Claim()));
        Assert.AreEqual(0, (await host.Store.ReadAsync(host.Account)).ActiveAttempts);
    }

    [TestMethod]
    public async Task EnforcedRoot_ReturnsPriceFrozenGrant_AndSettlesActualCost()
    {
        await using var host = await TestHost.CreateAsync(BudgetEnforcementMode.Enforced);
        var grant = await host.Port.BeginAsync(Claim());
        Assert.IsNotNull(grant);
        Assert.AreEqual(1m, grant.InputPerMillion);
        Assert.AreEqual(0.5m, grant.CacheHitPerMillion);
        Assert.AreEqual(2m, grant.OutputPerMillion);
        Assert.AreEqual(1_000_000, grant.BaseUnit);

        await host.Port.SettleAsync(grant, new ModelUsage(1_000_000, 100_000, 400_000), "provider:receipt");
        var snapshot = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
        // 600k normal input * 1 + 400k cache * 0.5 + 100k output * 2 = 0.6 + 0.2 + 0.2 = 1.0
        Assert.AreEqual(1.0m, snapshot.Cost);
    }

    private static ModelAttemptBudgetClaim Claim() => new("account-1", [], Identity(), "attempt-1", "model-a",
        "rev-1", "pricing-v1", 1_000_000, 100_000, "usd", DateTimeOffset.UtcNow);

    private static BudgetIdentity Identity() => new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1");

    private sealed class FakeQuotes : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(new("quote-1", request.ModelId, request.Revision, "usd",
                "per-1m-tokens", 1m, 0.5m, 2m, 1_000_000, request.DispatchAt.AddHours(-1),
                request.DispatchAt.AddHours(1), "utc", "declared", false, 10m, "pricing-v1", DateTimeOffset.UtcNow));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ExecutionBudgetStore store,
        PlatformModelAttemptBudgetPort port, BudgetAccountId account) : IAsyncDisposable
    {
        public ExecutionBudgetStore Store { get; } = store;
        public PlatformModelAttemptBudgetPort Port { get; } = port;
        public BudgetAccountId Account { get; } = account;

        public static async Task<TestHost> CreateAsync(BudgetEnforcementMode mode)
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
                new(MaxOutputTokens: 100_000, MaxCost: 10m, MaxInputTokensPerRequest: 1_000_000), Identity(), "usd",
                mode);
            var gate = new ModelAttemptBudgetGate(store, new FakeQuotes());
            return new(connection, store, new PlatformModelAttemptBudgetPort(gate), account);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
