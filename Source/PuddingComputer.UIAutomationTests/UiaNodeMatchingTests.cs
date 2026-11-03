using Pudding.Contracts.Computer;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.UIAutomationTests;

/// <summary>
/// <see cref="UiaNodeMatching"/> —— 把任务里的 <see cref="ElementSelector"/>
/// 与观察到的 <see cref="UiaNode"/> 接起来。
/// <para>
/// 这一跳是整条链路里第一次能判定"目标找到了没有"。锁的重点有两个：
/// </para>
/// <list type="number">
/// <item><b>空选择器必须不匹配任何东西</b> —— 让它匹配一切会在调用方漏填时
///   静默选中第一个节点，那比报错危险得多。</item>
/// <item><b>命中多个时不能替调用方挑</b> —— 静默取第一个会让"条件写松了"
///   永远不被发现。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class UiaNodeMatchingTests
{
    private static UiaNode Node(
        string? automationId = null,
        string name = "",
        string controlType = "Button",
        string className = "Button") =>
        new("n0", automationId, name, controlType, className,
            new PhysicalRect(0, 0, 10, 10), IsEnabled: true, IsOffscreen: false,
            HasKeyboardFocus: false, Patterns: ["Invoke"], ParentRef: null);

    /// <summary>按 AutomationId 精确匹配。</summary>
    [TestMethod]
    public void Matches_ByAutomationId()
    {
        var node = Node(automationId: "CalculatorResults");

        Assert.IsTrue(node.Matches(new ElementSelector(AutomationId: "CalculatorResults")));
        Assert.IsFalse(node.Matches(new ElementSelector(AutomationId: "num1Button")));
    }

    /// <summary>
    /// **实测驱动**：记事本编辑区没有 AutomationId，只能靠 ControlType + ClassName。
    /// </summary>
    [TestMethod]
    public void Matches_WithoutAutomationId_ByTypeAndClass()
    {
        var node = Node(automationId: null, name: "文本编辑器",
            controlType: "Document", className: "RichEditD2DPT");

        Assert.IsTrue(node.Matches(new ElementSelector(
            ControlType: "Document", ClassName: "RichEditD2DPT")));
    }

    /// <summary>多字段是 AND：任一不符即不匹配。</summary>
    [TestMethod]
    public void Matches_MultipleFieldsAreAnd()
    {
        var node = Node(automationId: "x", controlType: "Button", className: "Button");

        Assert.IsFalse(node.Matches(new ElementSelector(
            AutomationId: "x", ControlType: "Text")), "ControlType 不符应失败");
    }

    /// <summary>
    /// **核心用例**：空选择器不得匹配任何节点。
    /// <para>
    /// 如果返回 true，调用方漏填字段时就会静默拿到第一个节点 —— 点错目标的起点。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Matches_EmptySelector_NeverMatches()
    {
        var node = Node(automationId: "anything");

        Assert.IsFalse(node.Matches(new ElementSelector()));
        Assert.IsFalse(node.Matches(new ElementSelector(AutomationId: "  ")));
    }

    /// <summary>
    /// 大小写策略：类型标识忽略大小写，应用给的标识精确比较。
    /// </summary>
    [TestMethod]
    public void Matches_CaseSensitivityDiffersByField()
    {
        var node = Node(automationId: "CalculatorResults",
            name: "显示为 0", controlType: "Text", className: "TextBlock");

        // 类型标识：忽略大小写
        Assert.IsTrue(node.Matches(new ElementSelector(ControlType: "text")));
        Assert.IsTrue(node.Matches(new ElementSelector(ClassName: "textblock")));

        // 应用标识与名称：精确
        Assert.IsFalse(node.Matches(new ElementSelector(AutomationId: "calculatorresults")));
        Assert.IsFalse(node.Matches(new ElementSelector(Name: "显示为 1")));
    }

    /// <summary>FindAll 返回全部命中项。</summary>
    [TestMethod]
    public void FindAll_ReturnsEveryMatch()
    {
        var snapshot = new UiaSnapshot(UiaAvailability.Supported,
        [
            Node(automationId: "a", controlType: "Button"),
            Node(automationId: "b", controlType: "Button"),
            Node(automationId: "c", controlType: "Text"),
        ], null);

        var hits = snapshot.FindAll(new ElementSelector(ControlType: "Button"));

        Assert.HasCount(2, hits);
    }

    /// <summary>唯一命中时 TryFindUnique 成功并给出该节点。</summary>
    [TestMethod]
    public void TryFindUnique_OnSingleMatch_Succeeds()
    {
        var snapshot = new UiaSnapshot(UiaAvailability.Supported,
        [
            Node(automationId: "target"),
            Node(automationId: "other"),
        ], null);

        bool ok = snapshot.TryFindUnique(new ElementSelector(AutomationId: "target"),
            out var match, out var reason);

        Assert.IsTrue(ok);
        Assert.IsNotNull(match);
        Assert.AreEqual("target", match.AutomationId);
        Assert.IsNull(reason);
    }

    /// <summary>零命中时失败并给出可读原因（含快照节点数，便于诊断）。</summary>
    [TestMethod]
    public void TryFindUnique_OnNoMatch_FailsWithReason()
    {
        var snapshot = new UiaSnapshot(UiaAvailability.Supported, [Node(automationId: "a")], null);

        bool ok = snapshot.TryFindUnique(new ElementSelector(AutomationId: "missing"),
            out var match, out var reason);

        Assert.IsFalse(ok);
        Assert.IsNull(match);
        Assert.IsNotNull(reason);
        Assert.Contains("未找到", reason);
    }

    /// <summary>
    /// **核心用例**：命中多个时必须失败，不能替调用方挑第一个。
    /// <para>
    /// 静默取第一个会让"选择器写得太松"永远不被发现，直到某次点错目标。
    /// </para>
    /// </summary>
    [TestMethod]
    public void TryFindUnique_OnMultipleMatches_FailsWithReason()
    {
        var snapshot = new UiaSnapshot(UiaAvailability.Supported,
        [
            Node(automationId: "dup", controlType: "Button"),
            Node(automationId: "dup", controlType: "Button"),
        ], null);

        bool ok = snapshot.TryFindUnique(new ElementSelector(AutomationId: "dup"),
            out var match, out var reason);

        Assert.IsFalse(ok);
        Assert.IsNull(match);
        Assert.IsNotNull(reason);
        Assert.Contains("不唯一", reason);
    }

    /// <summary>空选择器走 TryFindUnique 时也失败，而不是返回第一个节点。</summary>
    [TestMethod]
    public void TryFindUnique_EmptySelector_Fails()
    {
        var snapshot = new UiaSnapshot(UiaAvailability.Supported, [Node(automationId: "a")], null);

        bool ok = snapshot.TryFindUnique(new ElementSelector(), out var match, out var reason);

        Assert.IsFalse(ok);
        Assert.IsNull(match);
        Assert.Contains("选择器为空", reason!);
    }
}
