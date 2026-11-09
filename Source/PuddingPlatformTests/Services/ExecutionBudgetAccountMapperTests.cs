using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 P2 canonical account mapping on real SQLite: deterministic Goal/Work/Task account ids, explicit existing
/// parent references, ancestors excluding the account itself, idempotent exact resolution and a hard conflict for any
/// changed field (no silent reuse, no widening, no mode downgrade, no implicit parent creation).
/// </summary>
[TestClass]
public sealed class ExecutionBudgetAccountMapperTests
{
    [TestMethod]
    public async Task GoalWorkAndNestedWork_AreMappedWithExplicitParents_AndAncestorsExcludeTheLeaf()
    {
        await using var host = await TestHost.CreateAsync();
        var goal = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.GoalRun, "goal-1"));
        Assert.AreEqual("goal:goal-1", goal.AccountId.Value);
        Assert.IsFalse(goal.Ancestors.IsDefault, "A root must return a non-default empty ancestor path.");
        Assert.AreEqual(0, goal.Ancestors.Length);

        var work = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-1") with
        {
            ParentAccountId = goal.AccountId,
        });
        Assert.AreEqual("work:work-1", work.AccountId.Value);
        CollectionAssert.AreEqual(new[] { new BudgetAccountId("goal:goal-1") }, work.Ancestors.ToArray());

        var child = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-2") with
        {
            ParentAccountId = work.AccountId,
        });
        CollectionAssert.AreEqual(
            new[] { new BudgetAccountId("goal:goal-1"), new BudgetAccountId("work:work-1") },
            child.Ancestors.ToArray());

        Assert.AreEqual(3, (await host.Store.ListAccountIdsAsync()).Length,
            "Resolving descendants must not create extra accounts.");
    }

    [TestMethod]
    public async Task LegalRepeatedResolution_ReturnsTheSameFrozenBinding()
    {
        await using var host = await TestHost.CreateAsync();
        var descriptor = Descriptor(BudgetAccountKind.GoalRun, "goal-1") with
        {
            Limits = new(MaxOutputTokens: 1_000, MaxCost: 1m, MaxCalls: 4),
            Mode = BudgetEnforcementMode.Enforced,
        };

        var first = await host.Mapper.ResolveAsync(descriptor);
        var second = await host.Mapper.ResolveAsync(descriptor);

        Assert.AreEqual(first.AccountId, second.AccountId);
        Assert.AreEqual(first.Identity, second.Identity);
        Assert.AreEqual(first.Mode, second.Mode);
        Assert.AreEqual(BudgetEnforcementMode.Enforced, second.Mode);
        Assert.AreEqual(first.CostUnit, second.CostUnit);
        CollectionAssert.AreEqual(first.Ancestors.ToArray(), second.Ancestors.ToArray());
        var snapshot = await host.Store.ReadAsync(second.AccountId);
        Assert.AreEqual(1m, snapshot.Limits.MaxCost);
        Assert.AreEqual(4, snapshot.Limits.MaxCalls);
        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length);
    }

    [TestMethod]
    public async Task ConflictingResolution_IsRejectedInsteadOfSilentlyReusingTheOldAccount()
    {
        await using var host = await TestHost.CreateAsync();
        var descriptor = Descriptor(BudgetAccountKind.GoalRun, "goal-1") with
        {
            Limits = new(MaxCost: 1m, MaxCalls: 4),
            Mode = BudgetEnforcementMode.Enforced,
        };
        var created = await host.Mapper.ResolveAsync(descriptor);

        foreach (var conflicting in new[]
                 {
                     descriptor with { Limits = descriptor.Limits with { MaxCost = 100m } },
                     descriptor with { Limits = descriptor.Limits with { MaxCalls = 99 } },
                     descriptor with { Mode = BudgetEnforcementMode.Legacy },
                     descriptor with { AgentId = "agent-2" },
                     descriptor with { SessionId = "session-2" },
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
                await host.Mapper.ResolveAsync(conflicting));
            Assert.AreEqual(BudgetErrorCodes.Conflict, failure.Code);
        }

        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length);
        var snapshot = await host.Store.ReadAsync(created.AccountId);
        Assert.AreEqual(1m, snapshot.Limits.MaxCost, "A conflicting re-resolution must not widen the frozen limit.");
        Assert.AreEqual(4, snapshot.Limits.MaxCalls);
        Assert.AreEqual(BudgetEnforcementMode.Enforced,
            await host.Store.GetEnforcementModeAsync(created.AccountId));
        Assert.AreEqual(0, snapshot.Revision);
    }

    [TestMethod]
    public async Task WorkAndTaskRoots_AreTypedCorrectly_AndCreateNoGoalAccount()
    {
        await using var host = await TestHost.CreateAsync();
        var workRoot = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-root"));
        Assert.AreEqual("work:work-root", workRoot.AccountId.Value);
        Assert.AreEqual(0, workRoot.Ancestors.Length);

        var task = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Task, "task-1"));
        Assert.AreEqual("task:task-1", task.AccountId.Value);
        Assert.AreEqual(0, task.Ancestors.Length);

        var workUnderTask = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-task-1") with
        {
            ParentAccountId = task.AccountId,
        });
        CollectionAssert.AreEqual(new[] { task.AccountId }, workUnderTask.Ancestors.ToArray());

        var ids = await host.Store.ListAccountIdsAsync();
        Assert.AreEqual(3, ids.Length);
        Assert.IsFalse(ids.Any(id => id.Value.StartsWith("goal:", StringComparison.Ordinal)),
            "An independent Work/Task root must not materialize a Goal account.");
    }

    [TestMethod]
    public async Task MissingDeclaredParent_CreatesNeitherTheParentNorTheChild()
    {
        await using var host = await TestHost.CreateAsync();
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-1") with
            {
                ParentAccountId = new BudgetAccountId("goal:missing"),
            }));
        Assert.AreEqual(BudgetErrorCodes.AccountMissing, failure.Code);
        Assert.AreEqual(0, (await host.Store.ListAccountIdsAsync()).Length,
            "The mapper must not create the missing parent account.");
    }

    [TestMethod]
    public async Task InvalidDescriptorOrIdKindPairing_IsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        foreach (var invalid in new[]
                 {
                     Descriptor(BudgetAccountKind.GoalRun, "") with { RootId = "" },
                     Descriptor(BudgetAccountKind.GoalRun, "goal-1") with { RootId = "goal,1" },
                     Descriptor(BudgetAccountKind.GoalRun, "goal-1") with { RootId = " goal-1" },
                     Descriptor(BudgetAccountKind.GoalRun, "goal-1") with { Mode = (BudgetEnforcementMode)99 },
                     Descriptor(BudgetAccountKind.Work, "work-1") with { Limits = null! },
                     Descriptor(BudgetAccountKind.GoalRun, "goal-1") with
                     {
                         ParentAccountId = new BudgetAccountId("goal:goal-1"),
                     },
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
                await host.Mapper.ResolveAsync(invalid));
            Assert.AreEqual(BudgetErrorCodes.AccountInvalid, failure.Code);
        }

        Assert.AreEqual(0, (await host.Store.ListAccountIdsAsync()).Length);
    }

    private static BudgetAccountDescriptor Descriptor(BudgetAccountKind kind, string rootId) => new(kind, rootId,
        "ws-1", "agent-1", "session-1", new(MaxOutputTokens: 100_000, MaxCost: 10m), "usd",
        BudgetEnforcementMode.Enforced);

    private static DbContextOptions<PlatformDbContext> Options(SqliteConnection connection)
        => new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ExecutionBudgetStore store,
        ExecutionBudgetAccountMapper mapper) : IAsyncDisposable
    {
        public ExecutionBudgetStore Store { get; } = store;
        public ExecutionBudgetAccountMapper Mapper { get; } = mapper;

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var factory = new TestDbContextFactory(Options(connection));
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var store = new ExecutionBudgetStore(factory);
            return new(connection, store, new ExecutionBudgetAccountMapper(store));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
