using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pudding.Api.Contracts;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using AgentConfigurationSnapshot = PuddingModels.Contracts.AgentConfigurationSnapshot;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Reviews;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 live binder end-to-end on real SQLite with a fake provider: Review purpose routing + engine lease + durable
/// P2 reservation are composed; success settles actual usage, provider failure marks Unknown, exhausted budget fails
/// before the provider is called, and the executor maps typed results.
/// </summary>
[TestClass]
public sealed class ReviewEvaluationBinderTests
{
    [TestMethod]
    public async Task BinderReservesExactlyOnce_AndExecutorReturnsTheFrozenResult()
    {
        await using var host = await TestHost.CreateAsync();
        var result = await host.Executor.ReviewAsync(Snapshot(), "attempt-1");
        Assert.AreEqual(ReviewFindingValue.Satisfied, result.Finding);
        Assert.AreEqual("model-review", result.ModelId);
        Assert.AreEqual("rev-42", result.ModelRevision);
        Assert.AreEqual(1, host.Model.Calls, "The provider is called once.");
        var snapshot = await host.Budget.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.IsTrue(snapshot.Cost > 0m);
        Assert.AreEqual(0m, snapshot.UnknownCost);
    }

    [TestMethod]
    public async Task ProviderFailure_MarksTheAttemptUnknown_AndFailsTyped()
    {
        await using var host = await TestHost.CreateAsync();
        host.Model.Throw = true;
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Executor.ReviewAsync(Snapshot(), "attempt-1"));
        Assert.AreEqual(ReviewErrorCodes.ExecutorUnknown, failure.Code);
        var snapshot = await host.Budget.ReadAsync(host.Account);
        Assert.IsTrue(snapshot.UnknownCost > 0m, "A dispatched provider failure is retained as unknown, never refunded.");
        Assert.AreEqual(0, snapshot.ActiveAttempts);
    }

    [TestMethod]
    public async Task ExhaustedBudget_FailsBeforeTheProviderIsCalled()
    {
        await using var host = await TestHost.CreateAsync(maxCost: 0.000001m);
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Executor.ReviewAsync(Snapshot(), "attempt-1"));
        Assert.AreEqual(ReviewErrorCodes.ExecutorUnknown, failure.Code);
        Assert.AreEqual(0, host.Model.Calls, "A rejected reservation must not reach the provider.");
        Assert.AreEqual(0, (await host.Budget.ReadAsync(host.Account)).ActiveAttempts);
    }

    private static ReviewInputSnapshot Snapshot() => new("review-1",
        new("ws-1", null, "goal-1", 1, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        "Build both files.", [new("criterion-1", "Both files exist.")],
        [new("evidence-1", EvidenceKind.CheckReport, "check://build", "check://build/1", "sha", DateTimeOffset.UtcNow,
            "controlled-check", false, "0", "both files exist")],
        EvidenceComplete: true, [], "prompt-v1", "policy-v1", "budget-1", DateTimeOffset.UtcNow.AddMinutes(10),
        ActorId: "actor-1");

    private sealed class FakePolicies : IAgentModelPolicyStore
    {
        public ValueTask<AgentConfigurationSnapshot> ReadAsync(AgentProfileScope scope, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentConfigurationSnapshot(1, 1,
            [
                new("Review", new("model-review", false, PolicySourceKind.Instance, 1), null, null),
                new("Primary", new("model-primary", false, PolicySourceKind.Instance, 1), null, null),
            ]));

        public ValueTask<ModelPolicyWriteResult> WriteAsync(AgentProfileScope scope, UpdateModelPolicyCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeCatalog : IModelCatalogSource
    {
        public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
            => ValueTask.FromResult<ModelCapabilityRecord?>(new(modelId, "rev-42", 128_000, 8_192, ["text"], true,
                true, "available", InputPerMillion: 1m, CacheHitPerMillion: 0.5m, OutputPerMillion: 2m,
                Currency: "usd", PriceSource: "declared", ExplicitFree: false,
                QuoteValidFrom: DateTimeOffset.UtcNow.AddDays(-1), QuoteValidTo: DateTimeOffset.UtcNow.AddDays(1),
                TimeZoneId: "utc", MaxChargeable: 10m, PricingRuleVersion: "pricing-v9"));

        public ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor,
            int maxCount, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeQuotes : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(new("quote-1", request.ModelId, request.Revision, "usd",
                "per-1m-tokens", 1m, 0.5m, 2m, 1_000_000, request.DispatchAt.AddHours(-1),
                request.DispatchAt.AddHours(1), "utc", "declared", false, 10m, "pricing-v9",
                DateTimeOffset.UtcNow));
    }

    private sealed class FakeLeaseSource(FakeLeasedModel model) : IReviewModelLeaseSource
    {
        public ValueTask<PuddingExecutionResources.Contracts.ILeasedModel> AcquireAsync(ReviewModelBinding binding,
            CancellationToken ct = default) => ValueTask.FromResult<PuddingExecutionResources.Contracts.ILeasedModel>(model);
    }

    private sealed class FakeLeasedModel : PuddingExecutionResources.Contracts.ILeasedModel
    {
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public ModelResourceDescriptor Descriptor { get; } = new("review-model", "rev-42", "model-review", 128_000, 8_192,
            2, new ModelPrice(1m, 2m, 0.5m));

        public Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("provider failed");
            dispatch();
            return Task.FromResult(new ModelCompletion(
                """{"decision":"accept","reason":"both files present","confidence":0.9,"checks":[{"criterionId":"criterion-1","outcome":"supported","evidenceIds":["evidence-1"]}]}""",
                new ModelUsage(1_000, 100), "stop"));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ExecutionBudgetStore budget, BudgetAccountId account,
        FakeLeasedModel model, ReviewExecutor executor) : IAsyncDisposable
    {
        public ExecutionBudgetStore Budget { get; } = budget;
        public BudgetAccountId Account { get; } = account;
        public FakeLeasedModel Model { get; } = model;
        public ReviewExecutor Executor { get; } = executor;

        public static async Task<TestHost> CreateAsync(decimal maxCost = 10m)
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
            await budget.CreateAccountAsync(account, null, new(MaxOutputTokens: 100_000, MaxCost: maxCost),
                new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd",
                BudgetEnforcementMode.Enforced);
            var gate = new ModelAttemptBudgetGate(budget, new FakeQuotes());
            var router = new ReviewModelRouter(new FakePolicies(), budget, new FakeCatalog());
            var model = new FakeLeasedModel();
            var binder = new ReviewEvaluationBinder(router, new FakeLeaseSource(model), gate);
            return new(connection, budget, account, model, new ReviewExecutor(binder));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
