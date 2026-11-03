using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.UIAutomationTests;

/// <summary>
/// 预热判据。这些用例钉住的是「什么时候该继续等」与「什么时候必须停」，
/// 而不是等待本身能否成功 —— 等待用尽后照常采集，绝不算作成功。
/// </summary>
[TestClass]
public sealed class UiaStabilizationTests
{
    [TestMethod]
    public void ShouldKeepWarming_StopsAsSoonAsChildrenAppear()
    {
        // 一旦读到子节点立刻停：否则每次采集都会白等一个窗口期。
        Assert.IsFalse(UiaStabilization.ShouldKeepWarming(
            childrenSeen: 1,
            elapsedInWarmup: TimeSpan.Zero,
            warmupWindow: TimeSpan.FromMilliseconds(800)));
    }

    [TestMethod]
    public void ShouldKeepWarming_WaitsWhileStillEmpty()
    {
        Assert.IsTrue(UiaStabilization.ShouldKeepWarming(
            childrenSeen: 0,
            elapsedInWarmup: TimeSpan.FromMilliseconds(100),
            warmupWindow: TimeSpan.FromMilliseconds(800)));
    }

    [TestMethod]
    public void ShouldKeepWarming_StopsWhenWindowExhausted()
    {
        // 窗口用尽必须停：预热是等待，不是保证；用尽后照常采集并按实际结果分类。
        Assert.IsFalse(UiaStabilization.ShouldKeepWarming(
            childrenSeen: 0,
            elapsedInWarmup: TimeSpan.FromMilliseconds(800),
            warmupWindow: TimeSpan.FromMilliseconds(800)));
    }

    [TestMethod]
    public void ShouldKeepWarming_ZeroWindowNeverWaits()
    {
        Assert.IsFalse(UiaStabilization.ShouldKeepWarming(
            childrenSeen: 0,
            elapsedInWarmup: TimeSpan.Zero,
            warmupWindow: TimeSpan.Zero));
    }

    [TestMethod]
    public void EffectiveWarmup_NeverExceedsCaptureTimeout()
    {
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(300),
            UiaStabilization.EffectiveWarmup(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(800)));

        Assert.AreEqual(
            TimeSpan.FromMilliseconds(800),
            UiaStabilization.EffectiveWarmup(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(800)));
    }

    [TestMethod]
    public void EffectiveWarmup_ZeroConfiguredMeansNoWarmup()
    {
        Assert.AreEqual(
            TimeSpan.Zero,
            UiaStabilization.EffectiveWarmup(TimeSpan.FromSeconds(3), TimeSpan.Zero));
    }
}
