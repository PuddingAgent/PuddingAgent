using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services.StorageManagement;

/// <summary>
/// 平台库页回收决策的契约。
/// </summary>
/// <remarks>
/// 为什么这些断言值得存在：平台库当前 <c>auto_vacuum=NONE</c>，`PRAGMA incremental_vacuum`
/// 在这种库上**静默无效**（返回成功、零回收）。如果决策层不在这里拒绝执行，
/// 维护循环就会每轮记一条“已维护”而实际什么都没回收 —— 那是最难发现的假治理。
/// </remarks>
[TestClass]
public sealed class PlatformStorageReclaimPolicyTests
{
    [TestMethod]
    public void NonIncrementalMode_IsRejected_EvenWhenPagesAreFree()
    {
        var decision = PlatformStorageReclaimPolicy.Evaluate(
            freelistCount: 1_373_668,
            autoVacuumMode: 0); // NONE

        Assert.IsFalse(decision.ShouldReclaim,
            "auto_vacuum=NONE 的库上 incremental_vacuum 是静默 no-op，必须拒绝执行，不得假装回收");
        Assert.AreEqual(ReclaimSkipReason.NotIncrementalMode, decision.SkipReason);
        Assert.AreEqual(0, decision.PagesToReclaim);
    }

    [TestMethod]
    public void NonIncrementalMode_WinsOverWriterBusy_SoTheReasonIsNotMisattributed()
    {
        // 顺序契约：模式不对时，报“写者忙”会让下一个人去等空闲窗口——
        // 而窗口等到了也同样是零回收。必须报模式。
        var decision = PlatformStorageReclaimPolicy.Evaluate(
            freelistCount: 1000,
            autoVacuumMode: 0,
            writerBusy: true);

        Assert.AreEqual(ReclaimSkipReason.NotIncrementalMode, decision.SkipReason,
            "模式错误必须优先于写者忙上报，否则会误导成“等空闲就好”");
        Assert.IsFalse(decision.ShouldReclaim);
    }

    [TestMethod]
    public void WriterBusy_IsReported_OnAnIncrementalDatabase()
    {
        var decision = PlatformStorageReclaimPolicy.Evaluate(
            freelistCount: 1000,
            autoVacuumMode: PlatformStorageReclaimPolicy.IncrementalAutoVacuumMode,
            writerBusy: true);

        Assert.AreEqual(ReclaimSkipReason.WriterBusy, decision.SkipReason);
        Assert.IsFalse(decision.ShouldReclaim);
    }

    [TestMethod]
    public void EmptyFreelist_IsSkipped_NotTreatedAsSuccess()
    {
        var decision = PlatformStorageReclaimPolicy.Evaluate(
            freelistCount: 0,
            autoVacuumMode: PlatformStorageReclaimPolicy.IncrementalAutoVacuumMode);

        Assert.AreEqual(ReclaimSkipReason.NoFreePages, decision.SkipReason);
        Assert.IsFalse(decision.ShouldReclaim);
    }

    [TestMethod]
    public void FreelistSmallerThanBatch_ReclaimsExactlyTheFreelist()
    {
        var decision = PlatformStorageReclaimPolicy.Evaluate(
            freelistCount: 300,
            autoVacuumMode: PlatformStorageReclaimPolicy.IncrementalAutoVacuumMode,
            batchPages: 1024);

        Assert.IsTrue(decision.ShouldReclaim);
        Assert.AreEqual(ReclaimSkipReason.None, decision.SkipReason);
        Assert.AreEqual(300, decision.PagesToReclaim);
    }

    [TestMethod]
    public void FreelistLargerThanBatch_IsBoundedByTheBatch()
    {
        var decision = PlatformStorageReclaimPolicy.Evaluate(
            freelistCount: 1_373_668,
            autoVacuumMode: PlatformStorageReclaimPolicy.IncrementalAutoVacuumMode,
            batchPages: 1024);

        Assert.IsTrue(decision.ShouldReclaim);
        Assert.AreEqual(1024, decision.PagesToReclaim,
            "单批必须被上界截断：维护是背景行为，不能一口气吃下整个 freelist 长时间占住写锁");
    }

    [TestMethod]
    public void NonPositiveBatch_IsRejectedAsACallerError()
    {
        foreach (var batch in new[] { 0, -1 })
        {
            var decision = PlatformStorageReclaimPolicy.Evaluate(
                freelistCount: 10_000,
                autoVacuumMode: PlatformStorageReclaimPolicy.IncrementalAutoVacuumMode,
                batchPages: batch);

            Assert.AreEqual(ReclaimSkipReason.InvalidBatchSize, decision.SkipReason,
                $"batchPages={batch} 应被判为调用方参数错误");
            Assert.IsFalse(decision.ShouldReclaim);
        }
    }

    [TestMethod]
    public void FullAutoVacuumMode_IsAlsoRejected_BecauseOnlyIncrementalSupportsBatching()
    {
        // 1 = FULL 也不支持 PRAGMA incremental_vacuum 的分批语义；只有 2 可以。
        var decision = PlatformStorageReclaimPolicy.Evaluate(
            freelistCount: 5000,
            autoVacuumMode: 1);

        Assert.AreEqual(ReclaimSkipReason.NotIncrementalMode, decision.SkipReason);
    }

    [TestMethod]
    public void DefaultBatch_IsSmallEnoughToKeepTheWriteLockShort()
    {
        // 4096 字节/页 × 1024 页 = 4 MiB。写成断言是为了防止有人把它调成“一次吃完”。
        Assert.AreEqual(1024, PlatformStorageReclaimPolicy.DefaultBatchPages);
        Assert.AreEqual(2, PlatformStorageReclaimPolicy.IncrementalAutoVacuumMode);
    }
}
