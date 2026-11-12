using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Reviews;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 P2 durable pool lifecycle: parent cancel and per-call timeout end observation with a typed error while the
/// dispatched attempt stays Unknown (never refunded); the provider receives a linked cancellation token.
/// </summary>
[TestClass]
public sealed class DurableModelResourcePoolTests
{
    [TestMethod]
    public async Task CallTimeout_EndsObservationAsModelTimeout_AndKeepsUnknown()
    {
        await using var host = await TestHost.CreateAsync();
        var model = new HangingModel();
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Pool.InvokeAsync(host.Scope, model,
                new ModelResourceRequest("call-1", "instruction", "input", 64, TimeoutMilliseconds: 20)));
        Assert.AreEqual("model.timeout", failure.Code);
        Assert.IsTrue(model.TokenCanceled, "The provider must observe the linked cancellation token.");
        var snapshot = await host.Budget.ReadAsync(host.Account);
        Assert.IsTrue(snapshot.UnknownCost > 0m);
    }

    [TestMethod]
    public async Task ParentCancellation_EndsObservationAsExecutionRevoked()
    {
        await using var host = await TestHost.CreateAsync();
        using var parentCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var scope = host.NewScope(parentCts.Token);
        var model = new HangingModel();
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Pool.InvokeAsync(scope, model,
                new ModelResourceRequest("call-1", "instruction", "input", 64, TimeoutMilliseconds: 30_000)));
        Assert.AreEqual("execution.revoked", failure.Code);
        Assert.IsTrue((await host.Budget.ReadAsync(host.Account)).UnknownCost > 0m);
    }

    private sealed class HangingModel : ILeasedModel
    {
        public bool TokenCanceled { get; private set; }
        public ModelResourceDescriptor Descriptor { get; } = new("review:model-review:rev-42", "rev-42", "model-review",
            128_000, 8_192, 2, new ModelPrice(1m, 2m, 0.5m));

        public async Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch,
            CancellationToken ct)
        {
            dispatch();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                TokenCanceled = true;
                throw;
            }
            return new ModelCompletion("never", new ModelUsage(1, 1), "stop");
        }
    }

    private sealed class FakeQuotes : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(new("quote-1", request.ModelId, request.Revision, "usd",
                "per-1m-tokens", 1m, 0.5m, 2m, 1_000_000, request.DispatchAt.AddHours(-1),
                request.DispatchAt.AddHours(1), "utc", "declared", false, 10m, "pricing-v9", DateTimeOffset.UtcNow));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ExecutionBudgetStore budget, BudgetAccountId account,
        DurableModelResourcePool pool, IExecutionResourceScope scope, HangingModel model) : IAsyncDisposable
    {
        public ExecutionBudgetStore Budget { get; } = budget;
        public BudgetAccountId Account { get; } = account;
        public DurableModelResourcePool Pool { get; } = pool;
        public IExecutionResourceScope Scope { get; } = scope;
        public HangingModel Model { get; } = model;

        public IExecutionResourceScope NewScope(CancellationToken parent)
            => new PuddingExecutionResources.ExecutionResourceScope(
                new ExecutionResourceIdentity("ws-1", "agent-1", "session-1", "run-1"), new ExecutionResourceLimits(),
                DateTimeOffset.UtcNow.AddMinutes(5), parent);

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
            var budget = new ExecutionBudgetStore(factory);
            var account = new BudgetAccountId("budget-1");
            await budget.CreateAccountAsync(account, null, new(MaxOutputTokens: 100_000, MaxCost: 10m),
                new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd",
                BudgetEnforcementMode.Enforced);
            var gate = new ModelAttemptBudgetGate(budget, new FakeQuotes());
            var pool = new DurableModelResourcePool(gate, account.Value, [],
                new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd", "pricing-v9",
                "review");
            var scope = new PuddingExecutionResources.ExecutionResourceScope(
                new ExecutionResourceIdentity("ws-1", "agent-1", "session-1", "run-1"), new ExecutionResourceLimits(),
                DateTimeOffset.UtcNow.AddMinutes(5));
            return new(connection, budget, account, pool, scope, new HangingModel());
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
