using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingModels;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 binder routing core on real SQLite + fake policy/catalog: missing actor fails typed (never AgentId as caller),
/// Review purpose comes from the existing authority, only explicit InheritPrimary borrows Primary, and the catalog
/// revision/pricing version are frozen into the binding.
/// </summary>
[TestClass]
public sealed class ReviewModelRouterTests
{
    [TestMethod]
    public async Task MissingActor_FailsTyped()
    {
        await using var host = await TestHost.CreateAsync();
        var job = Job() with { ActorId = null };
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () => await host.Router.ResolveAsync(job));
        Assert.AreEqual(ReviewErrorCodes.ActorUnavailable, failure.Code);
    }

    [TestMethod]
    public async Task ReviewPurpose_IsResolvedWithCatalogRevisionAndPriceVersion()
    {
        await using var host = await TestHost.CreateAsync(purposes: [new("Review", new("model-review", false,
            PolicySourceKind.Instance, 1), null, null), new("Primary", new("model-primary", false,
            PolicySourceKind.Instance, 1), null, null)]);
        var binding = await host.Router.ResolveAsync(Job());
        Assert.AreEqual("model-review", binding.ModelId);
        Assert.AreEqual("rev-42", binding.ModelRevision);
        Assert.AreEqual("pricing-v9", binding.PriceVersion);
        Assert.IsFalse(binding.InheritedFromPrimary);
        Assert.AreEqual("ws-1", binding.Identity.WorkspaceId);
        Assert.AreEqual("agent-1", binding.Identity.AgentId);
    }

    [TestMethod]
    public async Task ExplicitInheritPrimary_BorrowsPrimary_ButImplicitDoesNot()
    {
        await using var host = await TestHost.CreateAsync(purposes: [new("Review", new(null, true,
            PolicySourceKind.Instance, 1), null, null), new("Primary", new("model-primary", false,
            PolicySourceKind.Instance, 1), null, null)]);
        var inherited = await host.Router.ResolveAsync(Job());
        Assert.AreEqual("model-primary", inherited.ModelId);
        Assert.IsTrue(inherited.InheritedFromPrimary);

        await using var unconfigured = await TestHost.CreateAsync(purposes: [new("Primary", new("model-primary", false,
            PolicySourceKind.Instance, 1), null, null)]);
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await unconfigured.Router.ResolveAsync(Job()));
        Assert.AreEqual(ModelErrorCodes.PolicyMissing, failure.Code);
    }

    [TestMethod]
    public async Task MissingCatalogRevision_FailsTyped()
    {
        await using var host = await TestHost.CreateAsync(purposes: [new("Review", new("model-unknown", false,
            PolicySourceKind.Instance, 1), null, null)]);
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () => await host.Router.ResolveAsync(Job()));
        Assert.AreEqual(ModelErrorCodes.CapabilityMissing, failure.Code);
    }

    [TestMethod]
    public async Task SnapshotOverload_UsesFrozenActorAndBudgetAccount()
    {
        await using var host = await TestHost.CreateAsync(purposes: [new("Review", new("model-review", false,
            PolicySourceKind.Instance, 1), null, null)]);
        var snapshot = new ReviewInputSnapshot("review-1", Job().Key, "t", [new("criterion-1", "t")],
            [new("evidence-1", EvidenceKind.CheckReport, "check://c", "ref", "hash", DateTimeOffset.UtcNow,
                "controlled-check", false, "0")], EvidenceComplete: true, [], "prompt-v1", "policy-v1", "budget-1",
            DateTimeOffset.UtcNow.AddMinutes(10), ActorId: "actor-1");
        var binding = await host.Router.ResolveAsync(snapshot);
        Assert.AreEqual("model-review", binding.ModelId);
        Assert.AreEqual("actor-1", snapshot.ActorId);
    }

    [TestMethod]
    public async Task SnapshotWithoutActor_FailsTyped()
    {
        await using var host = await TestHost.CreateAsync();
        var job = Job() with { ActorId = null };
        var snapshot = new ReviewInputSnapshot("review-1", job.Key, "t", [new("criterion-1", "t")],
            [new("evidence-1", EvidenceKind.CheckReport, "check://c", "ref", "hash", DateTimeOffset.UtcNow,
                "controlled-check", false, "0")], EvidenceComplete: true, [], "prompt-v1", "policy-v1", "budget-1",
            DateTimeOffset.UtcNow.AddMinutes(10), ActorId: null);
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Router.ResolveAsync(snapshot));
        Assert.AreEqual(ReviewErrorCodes.ActorUnavailable, failure.Code);
    }
    private static ReviewJobView Job() => new("job-1", "review-1",
        new("ws-1", null, "goal-1", 3, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), ReviewJobState.Pending, 0, 0, 7, null, null,
        DateTimeOffset.UtcNow, ActorId: "actor-1");

    private sealed class FakePolicies(ImmutableArray<PurposeModelSetting> purposes) : IAgentModelPolicyStore
    {
        public ValueTask<AgentConfigurationSnapshot> ReadAsync(AgentProfileScope scope, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentConfigurationSnapshot(1, 1, purposes));

        public ValueTask<ModelPolicyWriteResult> WriteAsync(AgentProfileScope scope, UpdateModelPolicyCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeCatalog : IModelCatalogSource
    {
        public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
            => ValueTask.FromResult<ModelCapabilityRecord?>(modelId == "model-unknown"
                ? null
                : new ModelCapabilityRecord(modelId, "rev-42", 128_000, 8_192, ["text"], true, true, "available",
                    InputPerMillion: 1m, CacheHitPerMillion: 0.5m, OutputPerMillion: 2m, Currency: "usd",
                    PriceSource: "declared", ExplicitFree: false, QuoteValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
                    QuoteValidTo: DateTimeOffset.UtcNow.AddDays(1), TimeZoneId: "utc", MaxChargeable: 10m,
                    PricingRuleVersion: "pricing-v9"));

        public ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor,
            int maxCount, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ReviewModelRouter router) : IAsyncDisposable
    {
        public ReviewModelRouter Router { get; } = router;

        public static async Task<TestHost> CreateAsync(PurposeModelSetting[]? purposes = null)
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
            var budgets = new ExecutionBudgetStore(factory);
            await budgets.CreateAccountAsync(new BudgetAccountId("budget-1"), null,
                new(MaxOutputTokens: 100_000, MaxCost: 10m),
                new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd",
                BudgetEnforcementMode.Enforced);
            var policies = new FakePolicies([.. purposes ?? []]);
            var router = new ReviewModelRouter(policies, budgets, new FakeCatalog());
            return new(connection, router);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
