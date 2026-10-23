using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class WarmPrefixSoftReclaimAdmissionTests
{
    [TestMethod]
    public void SoftMaintenanceWithTinyReclaim_IsSkipped()
    {
        // 已用 120k tokens，计划只回收 1.5k（低于 5% = 6k，也低于绝对门槛之外的 6k 门槛）。
        Assert.IsTrue(WarmPrefixSoftReclaimAdmission.ShouldSkip(
            removedTokenEstimate: 1_500,
            initialUsedTokens: 120_000,
            requiresSynchronousProtection: false,
            forcePreparation: false));
    }

    [TestMethod]
    public void SoftMaintenanceWithMeaningfulReclaim_IsNotSkipped()
    {
        Assert.IsFalse(WarmPrefixSoftReclaimAdmission.ShouldSkip(
            removedTokenEstimate: 40_000,
            initialUsedTokens: 120_000,
            requiresSynchronousProtection: false,
            forcePreparation: false));
    }

    [TestMethod]
    public void HardProtectionAlwaysRuns_EvenWhenReclaimIsTiny()
    {
        // 越界时跳过只等于把失败推给下一跳，必须压缩。
        Assert.IsFalse(WarmPrefixSoftReclaimAdmission.ShouldSkip(
            removedTokenEstimate: 1,
            initialUsedTokens: 900_000,
            requiresSynchronousProtection: true,
            forcePreparation: false));
    }

    [TestMethod]
    public void ForcedPreparationAlwaysRuns_EvenWhenReclaimIsTiny()
    {
        Assert.IsFalse(WarmPrefixSoftReclaimAdmission.ShouldSkip(
            removedTokenEstimate: 1,
            initialUsedTokens: 900_000,
            requiresSynchronousProtection: false,
            forcePreparation: true));
    }

    [TestMethod]
    public void FloorUsesLargerOfAbsoluteAndRatio()
    {
        // 小上下文：绝对门槛 2000 占优。
        Assert.AreEqual(2_000, WarmPrefixSoftReclaimAdmission.ResolveFloor(10_000));
        // 大上下文：5% 占优（120k × 5% = 6k）。
        Assert.AreEqual(6_000, WarmPrefixSoftReclaimAdmission.ResolveFloor(120_000));
        // 负值/零不产生负门槛。
        Assert.AreEqual(2_000, WarmPrefixSoftReclaimAdmission.ResolveFloor(0));
    }
}
