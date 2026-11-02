using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.AutomationTests;

/// <summary>
/// 动作矩阵（设计 §5.2）的逐组合断言。
/// <para>
/// 关键的一条是 <see cref="Every_action_is_classified"/>：它不是验证某个具体格子，
/// 而是验证**没有动作被漏掉**。新增一个 ComputerAction 却忘了归类时，这条会红；
/// 只写"逐个格子断言"的测试对此毫无察觉。
/// </para>
/// </summary>
[TestClass]
public sealed class ComputerActionMatrixTests
{
    private static readonly ComputerAction[] UiaOnly =
    [
        ComputerAction.Invoke,
        ComputerAction.SetValue,
        ComputerAction.Select,
        ComputerAction.Toggle,
        ComputerAction.Scroll,
    ];

    private static readonly ComputerAction[] InputOnly =
    [
        ComputerAction.Click,
        ComputerAction.DoubleClick,
        ComputerAction.RightClick,
        ComputerAction.Move,
        ComputerAction.TypeText,
        ComputerAction.Press,
    ];

    private static readonly ComputerAction[] Unsupported =
    [
        ComputerAction.Drag,
    ];

    [TestMethod]
    public void Uia_only_actions_are_rejected_on_input_backend()
    {
        foreach (var action in UiaOnly)
        {
            Assert.IsTrue(ComputerActionMatrix.IsSupported(action, ComputerBackend.Uia),
                $"{action} 应当在 UIA 后端可用");
            Assert.IsFalse(ComputerActionMatrix.IsSupported(action, ComputerBackend.Input),
                $"{action} 不应当在 Input 后端可用 —— 用像素表达控件语义会丢掉语义信息");
        }
    }

    [TestMethod]
    public void Input_only_actions_are_rejected_on_uia_backend()
    {
        foreach (var action in InputOnly)
        {
            Assert.IsTrue(ComputerActionMatrix.IsSupported(action, ComputerBackend.Input),
                $"{action} 应当在 Input 后端可用");
            Assert.IsFalse(ComputerActionMatrix.IsSupported(action, ComputerBackend.Uia),
                $"{action} 不应当在 UIA 后端可用");
        }
    }

    [TestMethod]
    public void Drag_is_unsupported_on_both_backends()
    {
        foreach (var action in Unsupported)
        {
            Assert.IsFalse(ComputerActionMatrix.IsSupported(action, ComputerBackend.Uia));
            Assert.IsFalse(ComputerActionMatrix.IsSupported(action, ComputerBackend.Input));
            Assert.IsNull(ComputerActionMatrix.SupportedBackend(action),
                "首版不支持的动作必须显式返回 null，而不是挑一个后端尽力而为");
        }
    }

    /// <summary>
    /// 防漂移：每个 ComputerAction 必须被三张表之一覆盖。
    /// 新增动作而不归类时这条会红——这是本文件里最重要的一条。
    /// </summary>
    [TestMethod]
    public void Every_action_is_classified()
    {
        var all = Enum.GetValues<ComputerAction>();
        var covered = UiaOnly.Concat(InputOnly).Concat(Unsupported).ToArray();

        CollectionAssert.AreEquivalent(all, covered,
            $"动作分类与枚举不一致：枚举 {all.Length} 个，已归类 {covered.Length} 个。新增动作时请同步归类。");
        Assert.AreEqual(all.Length, covered.Distinct().Count(), "同一个动作不应出现在多个分类里");
    }

    [TestMethod]
    public void Supported_backend_agrees_with_is_supported()
    {
        foreach (var action in Enum.GetValues<ComputerAction>())
        {
            var backend = ComputerActionMatrix.SupportedBackend(action);
            if (backend is null)
            {
                Assert.IsFalse(ComputerActionMatrix.IsSupported(action, ComputerBackend.Uia));
                Assert.IsFalse(ComputerActionMatrix.IsSupported(action, ComputerBackend.Input));
            }
            else
            {
                Assert.IsTrue(ComputerActionMatrix.IsSupported(action, backend.Value),
                    $"SupportedBackend 返回 {backend} 但 IsSupported 否认它");
                // 恰好一个后端成立
                var other = backend.Value == ComputerBackend.Uia ? ComputerBackend.Input : ComputerBackend.Uia;
                Assert.IsFalse(ComputerActionMatrix.IsSupported(action, other));
            }
        }
    }

    [TestMethod]
    public void Empty_text_clears_content_only_for_set_value()
    {
        Assert.IsTrue(ComputerActionMatrix.EmptyTextClearsContent(ComputerAction.SetValue),
            "set_value 传空串应当是清空");
        Assert.IsFalse(ComputerActionMatrix.EmptyTextClearsContent(ComputerAction.TypeText),
            "type_text 传空串必须是 no-op —— 把它当清空是最常见的静默错误");
        Assert.IsFalse(ComputerActionMatrix.EmptyTextClearsContent(ComputerAction.Press));
    }

    [TestMethod]
    public void Non_mutating_actions_are_exactly_move_and_drag()
    {
        var expected = new[] { ComputerAction.Move, ComputerAction.Drag };
        var actual = Enum.GetValues<ComputerAction>()
            .Where(a => !ComputerActionMatrix.MutatesTarget(a)).ToArray();

        CollectionAssert.AreEquivalent(expected, actual,
            "不改状态的动作应当且只能是 Move 与 Drag");
    }
}
