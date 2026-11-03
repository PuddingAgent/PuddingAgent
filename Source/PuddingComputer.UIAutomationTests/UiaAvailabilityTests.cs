using Pudding.Contracts.Computer;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.UIAutomationTests;

/// <summary>
/// <see cref="UiaAvailabilityClassifier"/> 的分类表。
/// <para>
/// 锁定的核心是**四态不可压成两态**：
/// 超时 ≠ 不支持，稀疏 ≠ 失败。把它们混起来，调用方就分不清
/// "该换视觉渠道"和"该等一下再试"，而这两种处置的代价完全不同。
/// </para>
/// </summary>
[TestClass]
public sealed class UiaAvailabilityClassifierTests
{
    /// <summary>正常界面 → Supported。</summary>
    [TestMethod]
    public void PlentyOfActionableNodes_IsSupported()
    {
        var result = UiaAvailabilityClassifier.Classify(
            totalNodes: 50, actionableNodes: 12, timedOut: false, error: null);

        Assert.AreEqual(UiaAvailability.Supported, result);
    }

    /// <summary>
    /// **核心用例**：读到了树但可操作节点太少 → Sparse，不是 Unavailable。
    /// <para>
    /// Chromium 就是这种：只暴露 framework Pane，没有任何可操作节点。
    /// 判成 Unavailable 会让人以为"这个应用不支持无障碍"从而放弃；
    /// 判成 Supported 又会让上层以为拿到了可用目标。两种都错。
    /// </para>
    /// </summary>
    [TestMethod]
    public void TooFewActionableNodes_IsSparse()
    {
        var result = UiaAvailabilityClassifier.Classify(
            totalNodes: 7, actionableNodes: 2, timedOut: false, error: null);

        Assert.AreEqual(UiaAvailability.Sparse, result);
    }

    /// <summary>阈值边界：恰好 3 个可操作节点算 Supported。</summary>
    [TestMethod]
    public void ExactlyThresholdActionableNodes_IsSupported()
    {
        var result = UiaAvailabilityClassifier.Classify(
            totalNodes: 10,
            actionableNodes: UiaAvailabilityClassifier.SparseThreshold,
            timedOut: false,
            error: null);

        Assert.AreEqual(UiaAvailability.Supported, result);
    }

    /// <summary>阈值边界：阈下 1 个算 Sparse。</summary>
    [TestMethod]
    public void OneBelowThreshold_IsSparse()
    {
        var result = UiaAvailabilityClassifier.Classify(
            totalNodes: 10,
            actionableNodes: UiaAvailabilityClassifier.SparseThreshold - 1,
            timedOut: false,
            error: null);

        Assert.AreEqual(UiaAvailability.Sparse, result);
    }

    /// <summary>
    /// **核心用例**：超时优先于其它一切判断 —— 它是"没读完"，不是"没有"。
    /// <para>
    /// 即使已经有 0 个节点、还带着错误信息，只要是超时就必须报 TimedOut，
    /// 否则一个慢应用会被误判成"不支持无障碍"。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Timeout_DominatesOtherSignals()
    {
        var result = UiaAvailabilityClassifier.Classify(
            totalNodes: 0, actionableNodes: 0, timedOut: true, error: "boom");

        Assert.AreEqual(UiaAvailability.TimedOut, result);
    }

    /// <summary>有错误且未超时 → Unavailable。</summary>
    [TestMethod]
    public void Error_WithoutTimeout_IsUnavailable()
    {
        var result = UiaAvailabilityClassifier.Classify(
            totalNodes: 40, actionableNodes: 10, timedOut: false, error: "窗口已失效");

        Assert.AreEqual(UiaAvailability.Unavailable, result);
    }

    /// <summary>一个节点都没读到 → Unavailable。</summary>
    [TestMethod]
    public void NoNodes_IsUnavailable()
    {
        var result = UiaAvailabilityClassifier.Classify(
            totalNodes: 0, actionableNodes: 0, timedOut: false, error: null);

        Assert.AreEqual(UiaAvailability.Unavailable, result);
    }

    /// <summary>
    /// 只有 Supported / Sparse 允许上层继续用无障碍结果；
    /// Unavailable / TimedOut 必须换渠道。
    /// </summary>
    [TestMethod]
    public void ProvidesUsableTargets_MatchesTable()
    {
        Assert.IsTrue(UiaAvailability.Supported.ProvidesUsableTargets());
        Assert.IsTrue(UiaAvailability.Sparse.ProvidesUsableTargets());
        Assert.IsFalse(UiaAvailability.Unavailable.ProvidesUsableTargets());
        Assert.IsFalse(UiaAvailability.TimedOut.ProvidesUsableTargets());
    }
}

/// <summary>
/// <see cref="UiaNode.IsActionable"/> 与 <see cref="UiaSnapshot.ActionableCount"/>。
/// </summary>
[TestClass]
public sealed class UiaNodeTests
{
    private static UiaNode Node(
        bool enabled = true,
        bool offscreen = false,
        params string[] patterns) =>
        new("ref1", "id", "name", "Button", "Button",
            new PhysicalRect(0, 0, 10, 10), enabled, offscreen, false, patterns, null);

    /// <summary>可用、可见、有模式 → 可操作。</summary>
    [TestMethod]
    public void EnabledVisibleWithPattern_IsActionable()
    {
        Assert.IsTrue(Node(patterns: "Invoke").IsActionable);
    }

    /// <summary>禁用 → 不可操作。</summary>
    [TestMethod]
    public void Disabled_IsNotActionable()
    {
        Assert.IsFalse(Node(enabled: false, patterns: "Invoke").IsActionable);
    }

    /// <summary>在屏幕外 → 不可操作（点了也点不到）。</summary>
    [TestMethod]
    public void Offscreen_IsNotActionable()
    {
        Assert.IsFalse(Node(offscreen: true, patterns: "Invoke").IsActionable);
    }

    /// <summary>
    /// 没有任何动作模式 → 不可操作。
    /// <para>
    /// 纯展示的 Text 节点属于这一类：它存在于树里、有包围盒，
    /// 但点它什么都不会发生。
    /// </para>
    /// </summary>
    [TestMethod]
    public void NoPatterns_IsNotActionable()
    {
        Assert.IsFalse(Node().IsActionable);
    }

    /// <summary>ActionableCount 只数可操作的。</summary>
    [TestMethod]
    public void Snapshot_CountsOnlyActionable()
    {
        var snapshot = new UiaSnapshot(UiaAvailability.Supported,
        [
            Node(patterns: "Invoke"),
            Node(enabled: false, patterns: "Invoke"),
            Node(offscreen: true, patterns: "Value"),
            Node(),
            Node(patterns: "SelectionItem"),
        ], null);

        Assert.AreEqual(2, snapshot.ActionableCount);
        Assert.HasCount(5, snapshot.Nodes);
    }

    /// <summary>空快照要带上原因，便于诊断。</summary>
    [TestMethod]
    public void EmptySnapshot_CarriesReason()
    {
        var snapshot = UiaSnapshot.Empty(UiaAvailability.Unavailable, "窗口已失效");

        Assert.HasCount(0, snapshot.Nodes);
        Assert.AreEqual(UiaAvailability.Unavailable, snapshot.Availability);
        Assert.AreEqual("窗口已失效", snapshot.Reason);
        Assert.AreEqual(0, snapshot.ActionableCount);
    }
}
