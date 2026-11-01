using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 无效循环检测：目标是「该停就停」。
/// <para>
/// 两个方向都要测：只测「能识别循环」会把正常节奏误判（交替读写被当空转）；
/// 只测「不误判」会退化成永远不拦（该停不停，一直烧 token）。
/// </para>
/// <para>
/// 实测教训：第一版按「窗口内出现次数」判定，交替模式 a,b,a,b,a,b 里 a 出现 3 次就被判循环
/// —— 而现实中「读 A → 改 B → 再读 A」是正常节奏。改成**连续**重复后两者都能正确区分。
/// </para>
/// </summary>
[TestClass]
public sealed class RepeatedActionGuardTests
{
    [TestMethod]
    public void Three_identical_actions_in_a_row_is_a_loop()
    {
        var guard = new RepeatedActionGuard();

        Assert.IsFalse(guard.Record("read:D:\\a.cs"));
        Assert.IsFalse(guard.Record("read:D:\\a.cs"));
        Assert.IsTrue(guard.Record("read:D:\\a.cs"), "同一动作连续第三次即判循环");
    }

    [TestMethod]
    public void Alternating_actions_are_not_a_loop()
    {
        var guard = new RepeatedActionGuard();

        for (var i = 0; i < 6; i++)
            Assert.IsFalse(guard.Record(i % 2 == 0 ? "read:a" : "edit:b"),
                "交替不是空转：它可能是正常的取数-处理节奏");
    }

    [TestMethod]
    public void Different_arguments_count_as_different_actions()
    {
        var guard = new RepeatedActionGuard();

        Assert.IsFalse(guard.Record("read:a"));
        Assert.IsFalse(guard.Record("read:b"));
        Assert.IsFalse(guard.Record("read:c"), "读不同文件不是循环");
    }

    [TestMethod]
    public void Streak_resets_when_action_changes()
    {
        var guard = new RepeatedActionGuard();

        guard.Record("a");
        guard.Record("a");
        guard.Record("b");            // 打断
        Assert.AreEqual(1, guard.Streak);
        Assert.IsFalse(guard.Record("a"), "打断后重新计数，不能累加成 3");
    }

    [TestMethod]
    public void Blank_signature_neither_counts_nor_breaks_the_streak()
    {
        var guard = new RepeatedActionGuard();

        Assert.IsFalse(guard.Record("x"));
        Assert.IsFalse(guard.Record("x"));
        Assert.IsFalse(guard.Record("   "), "空动作不参与判定");
        Assert.IsTrue(guard.Record("x"), "空动作不得洗掉已有的连续计数");
    }

    [TestMethod]
    public void Reset_clears_history()
    {
        var guard = new RepeatedActionGuard();
        guard.Record("x");
        guard.Record("x");

        guard.Reset();

        Assert.AreEqual(0, guard.Streak);
        Assert.IsFalse(guard.Record("x"), "重置后重新计数");
    }

    [TestMethod]
    public void Invalid_threshold_is_rejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RepeatedActionGuard(threshold: 1));
    }
}
