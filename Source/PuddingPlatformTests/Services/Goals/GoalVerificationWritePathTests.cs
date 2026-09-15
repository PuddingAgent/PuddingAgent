using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>共享单个内存 SQLite 连接的 Context 工厂；与生产一致，每次调用新建 Context。</summary>
internal static class GoalWritePathHarness
{
    private sealed class SharedConnectionFactory(SqliteConnection connection)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options);
    }

    internal static async Task<(SqliteConnection Connection, IDbContextFactory<PlatformDbContext> Factory)>
        CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        return (connection, new SharedConnectionFactory(connection));
    }
}

/// <summary>
/// ADR-092 §5.1（G92-1 写入侧）：验收合同持久化必须幂等，内容变化才递增版本；
/// 空条件允许落库但仍是空合同（不得变成通过）。
/// </summary>
[TestClass]
public sealed class GoalAcceptanceContractStoreTests
{
    private static GoalCriterion Criterion(string id, string hash = "hash-a") => new()
    {
        Id = id,
        Revision = 1,
        Requirement = "目标要求的单元测试必须全部通过",
        Required = true,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = "checks/regression.md#L1",
        DefinitionHash = hash,
        InputRefs = ["Source"],
        ExecutorRole = "core",
    };

    private static GoalCheckSpec Spec(string hash = "hash-a") => new()
    {
        CheckId = "check-1",
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = "checks/regression.md#L1",
        DefinitionHash = hash,
        InputRefs = ["Source"],
        InputFingerprint = "fp-1",
        ExecutorRole = "core",
        ExpectedTestCount = 5,
    };

    [TestMethod]
    public async Task Save_InitialContract_PersistsCriteriaAndChecksAtVersionOne()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalAcceptanceContractStore(factory);

        var saved = await store.SaveAsync(
            "goal-1", 1, 3, "plan-fingerprint-1", [Criterion("criterion-1")], [Spec()]);

        Assert.AreEqual(1, saved.ContractVersion);
        Assert.AreEqual("gc-goal-1-1-3", saved.ContractId);

        var loaded = await store.LoadAsync("goal-1", 1, 3);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, GoalVerificationPersistence.ReadCriteria(loaded.CriteriaJson).Count);
        Assert.AreEqual(1, GoalVerificationPersistence.ReadChecks(loaded.ChecksJson).Count);
    }

    [TestMethod]
    public async Task Save_UnchangedContent_IsIdempotentAndKeepsVersion()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalAcceptanceContractStore(factory);

        await store.SaveAsync("goal-1", 1, 3, "plan-1", [Criterion("criterion-1")], [Spec()]);
        var second = await store.SaveAsync("goal-1", 1, 3, "plan-1", [Criterion("criterion-1")], [Spec()]);

        Assert.AreEqual(1, second.ContractVersion);
    }

    [TestMethod]
    public async Task Save_ChangedDefinition_BumpsContractVersion()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalAcceptanceContractStore(factory);

        await store.SaveAsync("goal-1", 1, 3, "plan-1", [Criterion("criterion-1")], [Spec("hash-a")]);
        var bumped = await store.SaveAsync("goal-1", 1, 3, "plan-1", [Criterion("criterion-1", "hash-b")], [Spec("hash-b")]);

        Assert.AreEqual(2, bumped.ContractVersion);
    }

    [TestMethod]
    public async Task Save_EmptyCriteria_IsPersistedAsEmptyContract_NotAsPassing()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalAcceptanceContractStore(factory);

        await store.SaveAsync("goal-1", 1, 3, null, [], []);

        var loaded = await store.LoadAsync("goal-1", 1, 3);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadCriteria(loaded.CriteriaJson).Count);
    }
}

/// <summary>
/// ADR-092 §6.2（G92-1 写入侧）：verification/check 生命周期。
/// 红线：没有真正运行出的报告就不存在 finished；租约可回收；去重键复用执行结果。
/// </summary>
[TestClass]
public sealed class GoalCheckRecordStoreTests
{
    private static GoalCheckSpec Spec(
        string checkId = "check-1",
        string hash = "hash-a",
        string fingerprint = "fp-1") => new()
    {
        CheckId = checkId,
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Kind = GoalVerificationSpecKinds.Test,
        DefinitionRef = "checks/regression.md#L1",
        DefinitionHash = hash,
        InputRefs = ["Source"],
        InputFingerprint = fingerprint,
        ExecutorRole = "core",
        ExpectedTestCount = 5,
    };

    private static GoalCheckReport Report(
        string status = GoalCriterionResultStatuses.Passed,
        string hash = "hash-a",
        string fingerprint = "fp-1") => new()
    {
        CheckId = "check-1",
        CriterionId = "criterion-1",
        CriterionRevision = 1,
        Status = status,
        EvidenceRefs = ["check:check-1:report:artifact-1"],
        InputFingerprint = fingerprint,
        DefinitionHash = hash,
        ReportRef = "artifact://goal-check-report-1",
        ExitCode = 0,
        ExecutedTestCount = 5,
        PassedTestCount = 5,
        FailedTestCount = 0,
        HasUnfinishedBackgroundProcess = false,
        RunnerId = "goal-check-runner",
        InvocationId = "invocation-1",
        ReportedAtUtc = DateTimeOffset.UtcNow,
    };

    [TestMethod]
    public async Task Enqueue_IsIdempotentForTheSameDefinition()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        Assert.AreEqual(1, await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]));
        Assert.AreEqual(0, await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]));

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(GoalCheckRecordStatuses.Pending, records[0].Status);
        Assert.AreEqual(0, records[0].Attempt);
    }

    [TestMethod]
    public async Task Enqueue_ReusesExistingRecordForTheSameDedupKey()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec("check-1")]);
        var created = await store.EnqueueAsync("goal-1", 1, 2, "work_unit", [Spec("check-2")]);

        Assert.AreEqual(0, created);
        Assert.AreEqual(1, (await store.ReadForEpochAsync("goal-1", 1)).Count);
    }

    [TestMethod]
    public async Task Enqueue_DefinitionChange_InvalidatesFinishedReport()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]);
        var leased = await store.LeaseAsync("runner-1", "goal-1", 1, TimeSpan.FromMinutes(1), 4);
        Assert.AreEqual(1, leased.Count);
        Assert.IsTrue(await store.FinishAsync(leased[0].CheckRecordId, "runner-1", Report()));

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec(hash: "hash-b")]);

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(GoalCheckRecordStatuses.Pending, records[0].Status);
        Assert.IsNull(records[0].ReportJson);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadReports(records).Count);
    }

    [TestMethod]
    public async Task Lease_ReclaimsExpiredLeaseAndCountsAttempts()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]);
        var first = await store.LeaseAsync("runner-1", "goal-1", 1, TimeSpan.FromSeconds(-1), 4);
        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, first[0].Attempt);

        var second = await store.LeaseAsync("runner-2", "goal-1", 1, TimeSpan.FromMinutes(1), 4);

        Assert.AreEqual(1, second.Count);
        Assert.AreEqual("runner-2", second[0].LeaseOwner);
        Assert.AreEqual(2, second[0].Attempt);
    }

    [TestMethod]
    public async Task Finish_WithoutHoldingTheLease_IsRejected()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]);
        var leased = await store.LeaseAsync("runner-1", "goal-1", 1, TimeSpan.FromMinutes(1), 4);

        Assert.IsFalse(await store.FinishAsync(leased[0].CheckRecordId, "runner-2", Report()));

        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(GoalCheckRecordStatuses.Leased, records[0].Status);
        Assert.IsNull(records[0].ReportJson);
    }

    [TestMethod]
    public async Task Finish_WithUnrunReport_ReturnsRecordToPending()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]);
        var leased = await store.LeaseAsync("runner-1", "goal-1", 1, TimeSpan.FromMinutes(1), 4);

        var finished = await store.FinishAsync(
            leased[0].CheckRecordId,
            "runner-1",
            Report(GoalCriterionResultStatuses.Pending));

        Assert.IsFalse(finished);
        var records = await store.ReadForEpochAsync("goal-1", 1);
        Assert.AreEqual(GoalCheckRecordStatuses.Pending, records[0].Status);
        Assert.IsNull(records[0].ReportJson);
        Assert.IsNull(records[0].LeaseOwner);
    }

    [TestMethod]
    public async Task Finish_WithRealReport_IsReadableThroughTheCapsuleReadPath()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]);
        var leased = await store.LeaseAsync("runner-1", "goal-1", 1, TimeSpan.FromMinutes(1), 4);
        Assert.IsTrue(await store.FinishAsync(leased[0].CheckRecordId, "runner-1", Report()));

        var reports = GoalVerificationPersistence.ReadReports(await store.ReadForEpochAsync("goal-1", 1));

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual("goal-check-runner", reports[0].RunnerId);
        Assert.AreEqual("invocation-1", reports[0].InvocationId);
        Assert.AreEqual(5, reports[0].ExecutedTestCount);
        Assert.AreEqual(0, reports[0].FailedTestCount);
    }

    [TestMethod]
    public async Task Lease_DoesNotClaimAnotherEpoch()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;
        var store = new GoalCheckRecordStore(factory);

        await store.EnqueueAsync("goal-1", 1, 1, "work_unit", [Spec()]);

        Assert.AreEqual(0, (await store.LeaseAsync("runner-1", "goal-1", 2, TimeSpan.FromMinutes(1), 4)).Count);
    }
}
