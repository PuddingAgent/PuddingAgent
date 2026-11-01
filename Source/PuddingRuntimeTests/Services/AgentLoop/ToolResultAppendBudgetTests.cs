using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 变换链的追加额度：整条链共用一个上限，防止「谁都往工具输出里塞」把上下文悄悄挤占。
/// <para>
/// 边界刻意收窄：只限制**追加**；Hook 缩短或等长改写不受影响 —— 否则「压缩输出」的 Hook
/// 会被额度误判成超限。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolResultAppendBudgetTests
{
    private const int Cap = ToolResultAppendBudget.MaxAppendedChars;

    [TestMethod]
    public void No_change_is_not_over_budget()
    {
        Assert.IsFalse(ToolResultAppendBudget.IsOverBudget(100, 100));
        Assert.AreEqual(0, ToolResultAppendBudget.AppendedChars(100, 100));
    }

    [TestMethod]
    public void Small_append_is_within_budget()
    {
        Assert.IsFalse(ToolResultAppendBudget.IsOverBudget(100, 100 + Cap));
        Assert.AreEqual(Cap, ToolResultAppendBudget.AppendedChars(100, 100 + Cap));
    }

    [TestMethod]
    public void Append_past_cap_is_over_budget()
    {
        Assert.IsTrue(ToolResultAppendBudget.IsOverBudget(100, 100 + Cap + 1));
    }

    [TestMethod]
    public void Shrinking_output_is_not_over_budget()
    {
        // 压缩型 Hook 会让输出变短：额度只针对追加，不能被误判。
        Assert.IsFalse(ToolResultAppendBudget.IsOverBudget(10_000, 200));
        Assert.AreEqual(0, ToolResultAppendBudget.AppendedChars(10_000, 200));
    }

    [TestMethod]
    public void Empty_original_still_measures_from_zero()
    {
        Assert.IsFalse(ToolResultAppendBudget.IsOverBudget(0, Cap));
        Assert.IsTrue(ToolResultAppendBudget.IsOverBudget(0, Cap + 1));
    }
}
