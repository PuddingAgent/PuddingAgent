using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-04 auto review submission on real SQLite: a Goal completion candidate creates exactly one required review job
/// with the frozen key/actor/Goal-root budget; re-submission is idempotent; invalid or actor-less input is rejected.
/// </summary>
[TestClass]
public sealed class GoalReviewSubmissionServiceTests
{
    [TestMethod]
    public async Task Submit_CreatesOneRequiredJob_WithTrustedActorAndGoalRootBudget()
    {
        await using var host = await TestHost.CreateAsync();
        var result = await host.Service.SubmitAsync(Request());
        Assert.IsTrue(result.Created);
        Assert.AreEqual(result.JobId, (await host.Jobs.GetAsync(result.JobId))!.JobId);
        var job = await host.Jobs.GetAsync(result.JobId);
        Assert.AreEqual(ReviewJobState.Pending, job!.State);
        Assert.IsTrue(job.Required);
        Assert.AreEqual("actor-1", job.ActorId);
        Assert.AreEqual("goal:goal-1", job.BudgetAccountId);
        Assert.AreEqual("goal-1", job.Key.GoalRunId);
        Assert.AreEqual("1", job.Key.ObjectiveVersion);
    }

    [TestMethod]
    public async Task Submit_IsIdempotent()
    {
        await using var host = await TestHost.CreateAsync();
        var first = await host.Service.SubmitAsync(Request());
        var second = await host.Service.SubmitAsync(Request());
        Assert.AreEqual(first.JobId, second.JobId);
        Assert.IsFalse(second.Created);
    }

    [TestMethod]
    public async Task Submit_RejectsMissingActorOrInvalidIdentity()
    {
        await using var host = await TestHost.CreateAsync();
        var noActor = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Service.SubmitAsync(Request() with { ActorId = "" }));
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, noActor.Code);
        var badDeadline = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Service.SubmitAsync(Request() with { Deadline = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, badDeadline.Code);
    }

    [TestMethod]
    public async Task Submit_RejectsSameKeyWithChangedArtifactSet()
    {
        await using var host = await TestHost.CreateAsync();
        var first = await host.Service.SubmitAsync(Request());
        var changed = await host.Service.SubmitAsync(Request() with { ArtifactSetHash = "other-artifacts" });
        Assert.AreNotEqual(first.JobId, changed.JobId,
            "A changed artifact set is a new logical key, so a new review job is created.");
    }

    private static GoalReviewSubmissionRequest Request() => new("goal-1", "ws-1", "task-1", 3, 2, 1,
        "acceptance-v1", "artifact-hash", "policy-v1", "actor-1", "goal:goal-1",
        DateTimeOffset.UtcNow.AddMinutes(30));

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ReviewJobStore jobs,
        GoalReviewSubmissionService service) : IAsyncDisposable
    {
        public ReviewJobStore Jobs { get; } = jobs;
        public GoalReviewSubmissionService Service { get; } = service;

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var jobs = new ReviewJobStore(factory);
            return new(connection, jobs, new GoalReviewSubmissionService(jobs));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
