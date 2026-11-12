using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5 evidence reader on real SQLite: only finished controlled check reports become hashed CheckReport
/// evidence; pending records never count; a criterion without a finished report keeps the snapshot incomplete;
/// a missing contract/goal returns SnapshotRequired instead of a fabricated snapshot.
/// </summary>
[TestClass]
public sealed class ReviewEvidenceReaderTests
{
    [TestMethod]
    public async Task FinishedCheckReportsBecomeHashedEvidence()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertCheckAsync("check-1", "criterion-1", "finished", """{"exitCode":0,"summary":"both files"}""");
        var reader = host.Reader(new FakeObjectives("Build both files.", [new("criterion-1", "Both files exist.")]));

        var result = await reader.ReadAsync(Job());
        Assert.IsNull(result.ReasonCode);
        var snapshot = result.Snapshot!;
        Assert.AreEqual("Build both files.", snapshot.Objective);
        Assert.AreEqual(1, snapshot.Evidence.Length);
        Assert.AreEqual(EvidenceKind.CheckReport, snapshot.Evidence[0].Kind);
        Assert.AreEqual("0", snapshot.Evidence[0].CheckExitCode);
        Assert.AreEqual("controlled-check", snapshot.Evidence[0].Producer);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Evidence[0].ContentHash));
        Assert.IsTrue(snapshot.EvidenceComplete);
        Assert.AreEqual(0, snapshot.MissingReasons.Length);
        Assert.AreEqual("policy-v1", snapshot.PromptVersion);
        Assert.AreEqual("actor-1", snapshot.ActorId, "The trusted actor travels with the frozen snapshot.");
    }

    [TestMethod]
    public async Task PendingRecordsNeverCount_AndMissingCriteriaKeepTheSnapshotIncomplete()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertCheckAsync("check-pending", "criterion-1", "pending", null);
        var reader = host.Reader(new FakeObjectives("Build both files.",
            [new("criterion-1", "Both files exist."), new("criterion-2", "Tests recorded.")]));

        var snapshot = (await reader.ReadAsync(Job())).Snapshot!;
        Assert.AreEqual(0, snapshot.Evidence.Length, "A pending check report is not evidence.");
        Assert.IsFalse(snapshot.EvidenceComplete);
        CollectionAssert.AreEquivalent(new[] { "criterion-not-checked:criterion-1", "criterion-not-checked:criterion-2" },
            snapshot.MissingReasons.ToArray());
    }

    [TestMethod]
    public async Task MissingContractOrGoal_RequiresANewSnapshot()
    {
        await using var host = await TestHost.CreateAsync();
        var withoutContract = await host.Reader(new FakeObjectives(null, [])).ReadAsync(Job());
        Assert.IsNull(withoutContract.Snapshot);
        Assert.AreEqual(ReviewErrorCodes.SnapshotRequired, withoutContract.ReasonCode);

        var noGoal = Job() with { Key = Job().Key with { GoalRunId = null, ParentWorkId = "work-1" } };
        var noGoalResult = await host.Reader(new FakeObjectives("x", [new("c", "t")])).ReadAsync(noGoal);
        Assert.AreEqual(ReviewErrorCodes.SnapshotRequired, noGoalResult.ReasonCode);
    }

    [TestMethod]
    public async Task ReportHashIsStableForTheSameStoredReport()
    {
        await using var host = await TestHost.CreateAsync();
        const string report = """{"summary":"same"}""";
        await host.InsertCheckAsync("check-1", "criterion-1", "finished", report);
        var reader = host.Reader(new FakeObjectives("t", [new("criterion-1", "t")]));
        var first = (await reader.ReadAsync(Job())).Snapshot!;
        var second = (await reader.ReadAsync(Job())).Snapshot!;
        Assert.AreEqual(first.Evidence[0].ContentHash, second.Evidence[0].ContentHash);
    }

    [TestMethod]
    public async Task FinishedReport_ExposesABoundedContentExcerpt()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertCheckAsync("check-1", "criterion-1", "finished", """{"exitCode":0,"summary":"both files exist"}""");
        var snapshot = (await host.Reader(new FakeObjectives("Build both files.",
            [new("criterion-1", "Both files exist.")])).ReadAsync(Job())).Snapshot!;
        Assert.AreEqual("both files exist", snapshot.Evidence[0].ContentExcerpt);
        Assert.IsFalse(snapshot.Evidence[0].ContentTruncated);
        Assert.IsNull(snapshot.Evidence[0].ContentUnavailableReason);
    }

    [TestMethod]
    public async Task LargeReportBody_IsTruncated_AndUnreadableReport_IsExplicit()
    {
        await using var host = await TestHost.CreateAsync();
        var large = new string('x', 5_000);
        await host.InsertCheckAsync("check-1", "criterion-1", "finished",
            $$"""{"summary":"{{large}}"}""");
        var truncated = (await host.Reader(new FakeObjectives("t", [new("criterion-1", "t")]))
            .ReadAsync(Job())).Snapshot!;
        Assert.AreEqual(4_096, truncated.Evidence[0].ContentExcerpt!.Length);
        Assert.IsTrue(truncated.Evidence[0].ContentTruncated);

        await host.InsertCheckAsync("check-2", "criterion-1", "finished", "not-json");
        var unreadable = (await host.Reader(new FakeObjectives("t", [new("criterion-1", "t")]))
            .ReadAsync(Job())).Snapshot!;
        Assert.AreEqual(1, unreadable.Evidence.Length, "An unreadable report is version-rejected, not rendered.");
        Assert.IsTrue(unreadable.MissingReasons.Any(r => r.StartsWith("evidence-version-mismatch:") && r.EndsWith("report-unreadable")));
    }
    [TestMethod]
    public async Task ReportWithMismatchedFrozenVersions_IsNotEvidence()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertCheckAsync("check-1", "criterion-1", "finished",
            """{"exitCode":0,"summary":"x","objectiveVersion":"old-objective","artifactSetHash":"old-artifacts"}""");
        var snapshot = (await host.Reader(new FakeObjectives("t", [new("criterion-1", "t")])).ReadAsync(Job())).Snapshot!;
        Assert.AreEqual(0, snapshot.Evidence.Length, "A historical exit 0 for another version is not evidence.");
        Assert.IsFalse(snapshot.EvidenceComplete);
        Assert.IsTrue(snapshot.MissingReasons.Any(r => r.StartsWith("evidence-version-mismatch:criterion-1")));
    }

    [TestMethod]
    public async Task ReportWithMatchingFrozenVersions_IsEvidence()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InsertCheckAsync("check-1", "criterion-1", "finished",
            """{"exitCode":0,"objectiveVersion":"objective-v1","acceptanceContractVersion":"acceptance-v1","artifactSetHash":"artifact-hash"}""");
        var snapshot = (await host.Reader(new FakeObjectives("t", [new("criterion-1", "t")])).ReadAsync(Job())).Snapshot!;
        Assert.AreEqual(1, snapshot.Evidence.Length);
        Assert.IsTrue(snapshot.EvidenceComplete);
    }
    private static ReviewJobView Job() => new("job-1", "review-1",
        new("ws-1", null, "goal-1", 3, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), ReviewJobState.Pending, 0, 0, 7, null, null,
        DateTimeOffset.UtcNow, ActorId: "actor-1");

    private sealed class FakeObjectives(string? objective, ImmutableArray<ReviewCriterion> criteria)
        : IReviewObjectiveSource
    {
        public ValueTask<(string, ImmutableArray<ReviewCriterion>)?> LoadAsync(ReviewLogicalKey key,
            CancellationToken ct = default)
            => ValueTask.FromResult<(string, ImmutableArray<ReviewCriterion>)?>(objective is null
                ? null
                : (objective, criteria));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory)
        : IAsyncDisposable
    {
        public ReviewEvidenceReader Reader(IReviewObjectiveSource objectives)
            => new(new GoalCheckRecordStore(factory), objectives);

        public async Task InsertCheckAsync(string checkId, string criterionId, string status, string? report)
        {
            await using var db = factory.CreateDbContext();
            db.GoalCheckRecords.Add(new GoalCheckRecordEntity
            {
                CheckRecordId = checkId,
                DedupKey = $"{checkId}|dedup",
                GoalRunId = "goal-1",
                ActivationEpoch = 3,
                IterationNo = 2,
                CheckId = checkId,
                CriterionId = criterionId,
                CriterionRevision = 1,
                Status = status,
                Attempt = 1,
                Priority = 0,
                ReportJson = report,
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
