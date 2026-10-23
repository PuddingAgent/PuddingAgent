using PuddingContextPruning;

namespace PuddingContextPruningTests;

/// <summary>
/// S2 配置校验测试：每一条非法配置都必须**在开跑之前**炸出来。
///
/// 为什么值得单独一个文件：这些参数一旦被下游从配置文件注入，静默接受一个错值
/// （比如 <c>MinReclaimRatio = 1.5</c>）会让剪枝永远不生效而且没有任何日志，
/// 属于最贵的「静默失效」。因此这里逐条锁死参数名，便于调用方定位。
/// </summary>
[TestClass]
public sealed class PruneOptionsValidationTests
{
    private static void AssertRejected(Action action, string parameterName)
    {
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(action);
        StringAssert.Contains(exception.Message, parameterName, $"异常必须点名参数 {parameterName}");
    }

    /// <summary>跨字段约束（字段各自合法、但组合起来矛盾）不可归因到单个值，因此用 InvalidOperationException。</summary>
    private static void AssertConflictingCombinationRejected(Action action, string parameterName)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(action);
        StringAssert.Contains(exception.Message, parameterName, $"异常必须点名参数 {parameterName}");
    }

    [TestMethod]
    public void Defaults_Are_Valid_And_Match_The_Frozen_Contract()
    {
        var options = new DeterministicPruneOptions();
        options.Validate();

        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(8, options.ProtectedTailMessages);
        Assert.AreEqual(8_000, options.MinToolResultChars);
        Assert.AreEqual(1_600, options.ToolResultHeadChars);
        Assert.AreEqual(400, options.ToolResultTailChars);
        Assert.AreEqual(512, options.MinDuplicateChars);
        Assert.AreEqual(4_000, options.MinToolArgumentChars);
        Assert.AreEqual(512, options.ToolArgumentHeadChars);
        Assert.AreEqual(2_000, options.MinReclaimTokens);
        Assert.AreEqual(0.05d, options.MinReclaimRatio);
    }

    [TestMethod]
    public void Negative_ProtectedTailMessages_Is_Rejected()
        => AssertRejected(
            () => new DeterministicPruneOptions { ProtectedTailMessages = -1 }.Validate(),
            nameof(DeterministicPruneOptions.ProtectedTailMessages));

    [TestMethod]
    public void NonPositive_MinToolResultChars_Is_Rejected()
        => AssertRejected(
            () => new DeterministicPruneOptions { MinToolResultChars = 0 }.Validate(),
            nameof(DeterministicPruneOptions.MinToolResultChars));

    [TestMethod]
    public void Head_Plus_Tail_Not_Below_MinToolResultChars_Is_Rejected()
        => AssertConflictingCombinationRejected(
            () => new DeterministicPruneOptions
            {
                MinToolResultChars = 1_000,
                ToolResultHeadChars = 800,
                ToolResultTailChars = 400,
            }.Validate(),
            nameof(DeterministicPruneOptions.ToolResultHeadChars));

    [TestMethod]
    public void NonPositive_MinDuplicateChars_Is_Rejected()
        => AssertRejected(
            () => new DeterministicPruneOptions { MinDuplicateChars = 0 }.Validate(),
            nameof(DeterministicPruneOptions.MinDuplicateChars));

    [TestMethod]
    public void MinToolArgumentChars_Not_Above_Head_Chars_Is_Rejected()
        => AssertConflictingCombinationRejected(
            () => new DeterministicPruneOptions { MinToolArgumentChars = 512 }.Validate(),
            nameof(DeterministicPruneOptions.MinToolArgumentChars));

    [TestMethod]
    public void Negative_MinReclaimTokens_Is_Rejected()
        => AssertRejected(
            () => new DeterministicPruneOptions { MinReclaimTokens = -1 }.Validate(),
            nameof(DeterministicPruneOptions.MinReclaimTokens));

    [TestMethod]
    public void MinReclaimRatio_Outside_Zero_To_One_Is_Rejected()
    {
        AssertRejected(
            () => new DeterministicPruneOptions { MinReclaimRatio = 1.5d }.Validate(),
            nameof(DeterministicPruneOptions.MinReclaimRatio));

        AssertRejected(
            () => new DeterministicPruneOptions { MinReclaimRatio = -0.01d }.Validate(),
            nameof(DeterministicPruneOptions.MinReclaimRatio));

        // NaN 与任何数比较都为 false，若不用「取反区间」写法会静默通过 —— 这里锁住该写法。
        AssertRejected(
            () => new DeterministicPruneOptions { MinReclaimRatio = double.NaN }.Validate(),
            nameof(DeterministicPruneOptions.MinReclaimRatio));
    }

    [TestMethod]
    public void Negative_ToolResultHeadChars_Is_Rejected()
        => AssertRejected(
            () => new DeterministicPruneOptions { ToolResultHeadChars = -1 }.Validate(),
            nameof(DeterministicPruneOptions.ToolResultHeadChars));
}
