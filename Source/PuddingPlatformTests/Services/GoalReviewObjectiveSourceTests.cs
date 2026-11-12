using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5 objective source on real SQLite: objective from the goal run, criteria from the versioned acceptance
/// contract, and a typed "not ready" (null) for a missing contract, malformed version or empty criteria.
/// </summary>
[TestClass]
public sealed class GoalReviewObjectiveSourceTests
{
    private static readonly string CriteriaJson = """
        [{"id":"criterion-1","requirement":"Both files exist.","revision":1,"required":true},
         {"id":"criterion-2","requirement":"Tests recorded.","revision":1,"required":true}]
        """;

    [TestMethod]
    public async Task ReadsObjectiveFromGoalRunAndCriteriaFromContract()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertGoalAsync("Build both files.", objectiveVersion: 2);
        await host.InsertContractAsync(objectiveVersion: 2, CriteriaJson);

        var result = await host.Source().LoadAsync(Key());
        Assert.IsNotNull(result);
        Assert.AreEqual("Build both files.", result.Value.Objective);
        Assert.AreEqual(2, result.Value.Criteria.Length);
        Assert.AreEqual("criterion-1", result.Value.Criteria[0].CriterionId);
        Assert.AreEqual("Both files exist.", result.Value.Criteria[0].Text);
    }

    [TestMethod]
    public async Task MissingContractOrMalformedVersionIsNotReady()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertGoalAsync("Build both files.", objectiveVersion: 2);
        Assert.IsNull(await host.Source().LoadAsync(Key()));

        await host.InsertContractAsync(objectiveVersion: 2, "[]");
        Assert.IsNull(await host.Source().LoadAsync(Key()), "An empty criteria contract is not ready.");

        await host.InsertContractAsync(objectiveVersion: 2, CriteriaJson);
        Assert.IsNull(await host.Source().LoadAsync(Key() with { ObjectiveVersion = "not-an-int" }));
        Assert.IsNull(await host.Source().LoadAsync(Key() with { GoalRunId = null, ParentWorkId = "work-1" }));
    }

    [TestMethod]
    public async Task EmptyGoalObjectiveIsNotReady()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertGoalAsync("", objectiveVersion: 2);
        await host.InsertContractAsync(objectiveVersion: 2, CriteriaJson);
        Assert.IsNull(await host.Source().LoadAsync(Key()));
    }

    private static ReviewLogicalKey Key() => new("ws-1", null, "goal-1", 1, 2, "2", "acceptance-v1",
        "artifact-hash", "policy-v1");

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory)
        : IAsyncDisposable
    {
        public GoalReviewObjectiveSource Source() => new(new GoalAcceptanceContractStore(factory), factory);

        public async Task InsertGoalAsync(string objective, int objectiveVersion)
        {
            await using var db = factory.CreateDbContext();
            db.GoalRuns.Add(new GoalRunEntity
            {
                GoalRunId = "goal-1",
                WorkspaceId = "ws-1",
                CurrentConversationId = "conv-1",
                AgentInstanceId = "agent-1",
                Objective = objective,
                ObjectiveVersion = objectiveVersion,
                ActivationEpoch = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        public async Task InsertContractAsync(int objectiveVersion, string criteriaJson)
        {
            await using var db = factory.CreateDbContext();
            db.GoalAcceptanceContracts.Add(new GoalAcceptanceContractEntity
            {
                ContractId = $"contract-{objectiveVersion}-{criteriaJson.Length}",
                GoalRunId = "goal-1",
                ActivationEpoch = 1,
                ObjectiveVersion = objectiveVersion,
                ContractVersion = 1,
                CriteriaJson = criteriaJson,
                ChecksJson = "[]",
                Source = "bounded_planning",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return new(connection, factory);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
