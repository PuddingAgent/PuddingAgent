using Pudding.Contracts.Computer;
using PuddingComputer.Benchmark;
using PuddingComputer.BenchmarkRunner;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.BenchmarkRunnerTests;

/// <summary>
/// <see cref="OracleEvaluator"/> —— 把 oracle 与快照对上，给出三态判定。
/// <para>
/// 锁定的核心是**不确定必须报 Unknown，不能猜成 Pass**。
/// 猜对一次会让整套判定失去可信度，而信任一旦丢了，判定表再准也没人信。
/// </para>
/// </summary>
[TestClass]
public sealed class OracleEvaluatorTests
{
    private static UiaNode Node(
        string? automationId = null,
        string name = "",
        string controlType = "Button",
        string className = "Button",
        bool enabled = true,
        bool offscreen = false,
        bool focused = false,
        params string[] patterns) =>
        new("n0", automationId, name, controlType, className,
            new PhysicalRect(0, 0, 10, 10), enabled, offscreen, focused,
            patterns.Length > 0 ? patterns : ["Invoke"], ParentRef: null);

    private static UiaSnapshot Snapshot(params UiaNode[] nodes) =>
        new(UiaAvailability.Supported, nodes, null);

    // ---------- name 属性 ----------

    [TestMethod]
    public void NameEndsWith_Matches_IsPass()
    {
        var snapshot = Snapshot(Node(automationId: "CalculatorResults", name: "显示为 42"));
        var oracle = new UiaPropertyOracle(
            new ElementSelector(AutomationId: "CalculatorResults"), "name", "endsWith", "42");

        var eval = OracleEvaluator.Evaluate(oracle, snapshot);

        Assert.AreEqual(OracleVerdict.Pass, eval.Verdict);
        Assert.Contains("显示为 42", eval.Evidence);
    }

    [TestMethod]
    public void NameEndsWith_NotMatching_IsFail()
    {
        var snapshot = Snapshot(Node(automationId: "CalculatorResults", name: "显示为 0"));
        var oracle = new UiaPropertyOracle(
            new ElementSelector(AutomationId: "CalculatorResults"), "name", "endsWith", "42");

        var eval = OracleEvaluator.Evaluate(oracle, snapshot);

        Assert.AreEqual(OracleVerdict.Fail, eval.Verdict);
    }

    [TestMethod]
    public void NameEquals_IsExact()
    {
        var snapshot = Snapshot(Node(automationId: "x", name: "Pudding"));

        Assert.AreEqual(OracleVerdict.Pass,
            OracleEvaluator.Evaluate(new UiaPropertyOracle(
                new ElementSelector(AutomationId: "x"), "name", "equals", "Pudding"), snapshot).Verdict);

        Assert.AreEqual(OracleVerdict.Fail,
            OracleEvaluator.Evaluate(new UiaPropertyOracle(
                new ElementSelector(AutomationId: "x"), "name", "equals", "pudding"), snapshot).Verdict,
            "值比较应大小写敏感");
    }

    /// <summary>操作符大小写不敏感 —— 清单是人写的，容易写错大小写。</summary>
    [TestMethod]
    public void Operator_IsCaseInsensitive()
    {
        var snapshot = Snapshot(Node(automationId: "x", name: "abc-42"));

        var eval = OracleEvaluator.Evaluate(new UiaPropertyOracle(
            new ElementSelector(AutomationId: "x"), "name", "ENDSWITH", "42"), snapshot);

        Assert.AreEqual(OracleVerdict.Pass, eval.Verdict);
    }

    // ---------- 不确定必须 Unknown ----------

    /// <summary>**核心用例**：元素找不到 → Unknown，不是 Fail。</summary>
    [TestMethod]
    public void ElementNotFound_IsUnknown_NotFail()
    {
        var snapshot = Snapshot(Node(automationId: "other"));
        var oracle = new UiaPropertyOracle(
            new ElementSelector(AutomationId: "missing"), "name", "equals", "x");

        var eval = OracleEvaluator.Evaluate(oracle, snapshot);

        Assert.AreEqual(OracleVerdict.Unknown, eval.Verdict,
            "找不到元素说明没观察到，不等於「确定不符合」");
    }

    /// <summary>**核心用例**：不支持的属性 → Unknown，不是 Fail。</summary>
    [TestMethod]
    public void UnsupportedProperty_IsUnknown()
    {
        var snapshot = Snapshot(Node(automationId: "x", name: "anything"));
        var oracle = new UiaPropertyOracle(
            new ElementSelector(AutomationId: "x"), "value", "equals", "anything");

        var eval = OracleEvaluator.Evaluate(oracle, snapshot);

        Assert.AreEqual(OracleVerdict.Unknown, eval.Verdict);
        Assert.Contains("尚未支持", eval.Evidence);
    }

    /// <summary>**核心用例**：命中多个 → Unknown，不替调用方挑。</summary>
    [TestMethod]
    public void MultipleMatches_IsUnknown()
    {
        var snapshot = Snapshot(
            Node(automationId: "dup", name: "a"),
            Node(automationId: "dup", name: "b"));
        var oracle = new UiaPropertyOracle(
            new ElementSelector(AutomationId: "dup"), "name", "equals", "a");

        var eval = OracleEvaluator.Evaluate(oracle, snapshot);

        Assert.AreEqual(OracleVerdict.Unknown, eval.Verdict);
        Assert.Contains("不唯一", eval.Evidence);
    }

    /// <summary>不支持的 oracle 类型 → Unknown。</summary>
    [TestMethod]
    public void UnsupportedOracleType_IsUnknown()
    {
        var snapshot = Snapshot(Node(automationId: "x"));

        var eval = OracleEvaluator.Evaluate(new VisualRegionOracle(0, 0, 10, 10, 0.01), snapshot);

        Assert.AreEqual(OracleVerdict.Unknown, eval.Verdict);
    }

    // ---------- 元素状态 ----------

    [TestMethod]
    public void StateEnabled_True_IsPass()
    {
        var snapshot = Snapshot(Node(automationId: "x", enabled: true));
        var oracle = new ElementStateOracle(new ElementSelector(AutomationId: "x"), "enabled", true);

        Assert.AreEqual(OracleVerdict.Pass, OracleEvaluator.Evaluate(oracle, snapshot).Verdict);
    }

    [TestMethod]
    public void StateEnabled_False_IsFail()
    {
        var snapshot = Snapshot(Node(automationId: "x", enabled: false));
        var oracle = new ElementStateOracle(new ElementSelector(AutomationId: "x"), "enabled", true);

        Assert.AreEqual(OracleVerdict.Fail, OracleEvaluator.Evaluate(oracle, snapshot).Verdict);
    }

    [TestMethod]
    public void StateActionable_ReflectsEnabledAndPatterns()
    {
        var actionable = Snapshot(Node(automationId: "x", enabled: true, patterns: "Invoke"));
        var notActionable = Snapshot(Node(automationId: "x", enabled: false, patterns: "Invoke"));

        var oracle = new ElementStateOracle(new ElementSelector(AutomationId: "x"), "actionable", true);

        Assert.AreEqual(OracleVerdict.Pass, OracleEvaluator.Evaluate(oracle, actionable).Verdict);
        Assert.AreEqual(OracleVerdict.Fail, OracleEvaluator.Evaluate(oracle, notActionable).Verdict);
    }

    [TestMethod]
    public void UnsupportedState_IsUnknown()
    {
        var snapshot = Snapshot(Node(automationId: "x"));
        var oracle = new ElementStateOracle(new ElementSelector(AutomationId: "x"), "selected", true);

        var eval = OracleEvaluator.Evaluate(oracle, snapshot);

        Assert.AreEqual(OracleVerdict.Unknown, eval.Verdict);
    }

    // ---------- 用实测数据 ----------

    /// <summary>
    /// 用 2026-11-03 实测的计算器数据复算，确认判定链在真实结构上成立。
    /// </summary>
    [TestMethod]
    public void RealCalculatorFixture_ProducesExpectedVerdicts()
    {
        var snapshot = Snapshot(
            Node(automationId: "CalculatorResults", name: "显示为 0", controlType: "Text", className: "TextBlock"),
            Node(automationId: "CalculatorExpression", name: "表达式为 ", controlType: "Text", className: "TextBlock"),
            Node(automationId: "plusButton", name: "加", controlType: "Button"));

        // 显示为 0，不是 42
        Assert.AreEqual(OracleVerdict.Fail, OracleEvaluator.Evaluate(
            new UiaPropertyOracle(new ElementSelector(AutomationId: "CalculatorResults"),
                "name", "endsWith", "42"), snapshot).Verdict);

        // 显示为 0，endsWith "0" —— 这条会通过（见报告中的"碰巧通过"）
        Assert.AreEqual(OracleVerdict.Pass, OracleEvaluator.Evaluate(
            new UiaPropertyOracle(new ElementSelector(AutomationId: "CalculatorResults"),
                "name", "endsWith", "0"), snapshot).Verdict);

        // 表达式里没有 +
        Assert.AreEqual(OracleVerdict.Fail, OracleEvaluator.Evaluate(
            new UiaPropertyOracle(new ElementSelector(AutomationId: "CalculatorExpression"),
                "name", "contains", "+"), snapshot).Verdict);
    }
}
