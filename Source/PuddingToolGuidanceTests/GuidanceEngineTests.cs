using PuddingToolGuidance;

namespace PuddingToolGuidanceTests;

/// <summary>
/// 求值器的三条契约：配额上限、按规则去重、规则抛异常不得毁掉整次求值。
/// </summary>
[TestClass]
public sealed class GuidanceEngineTests
{
    /// <summary>永远命中同一条建议的假规则。</summary>
    private sealed class AlwaysRule(string id) : IGuidanceRule
    {
        public string Id => id;

        public GuidanceAdvice? Evaluate(GuidanceInput input) => new(id, "msg-" + id);
    }

    /// <summary>抛异常的规则，用来验证兜底。</summary>
    private sealed class ThrowingRule : IGuidanceRule
    {
        public string Id => "boom";

        public GuidanceAdvice? Evaluate(GuidanceInput input) => throw new InvalidOperationException("boom");
    }

    private static readonly GuidanceInput AnyInput = new("shell", "cmd", "dir");

    [TestMethod]
    public void Default_max_advices_is_three()
    {
        var engine = new GuidanceEngine(
        [
            new AlwaysRule("r1"), new AlwaysRule("r2"), new AlwaysRule("r3"), new AlwaysRule("r4")
        ]);

        var summary = engine.Evaluate(AnyInput);

        Assert.HasCount(3, summary.Advices, "默认最多 3 条，避免提示挤占上下文");
    }

    [TestMethod]
    public void Duplicate_rule_ids_are_removed()
    {
        var engine = new GuidanceEngine(
        [
            new AlwaysRule("same"), new AlwaysRule("same")
        ], maxAdvices: 5);

        Assert.HasCount(1, engine.Evaluate(AnyInput).Advices);
    }

    [TestMethod]
    public void Throwing_rule_does_not_break_evaluation()
    {
        var engine = new GuidanceEngine([new ThrowingRule(), new AlwaysRule("ok")], maxAdvices: 5);

        var summary = engine.Evaluate(AnyInput);

        Assert.HasCount(1, summary.Advices, "抛异常的规则应被跳过，其余规则照常生效");
        Assert.AreEqual("ok", summary.Advices[0].RuleId);
    }

    [TestMethod]
    public void No_advice_returns_empty_and_keeps_output_untouched()
    {
        var engine = new GuidanceEngine([]);

        var summary = engine.Evaluate(AnyInput);

        Assert.IsFalse(summary.HasAdvice);
        Assert.AreEqual("original output", summary.AppendTo("original output"));
    }

    [TestMethod]
    public void Append_adds_note_after_output_without_touching_it()
    {
        var engine = new GuidanceEngine([new AlwaysRule("r1")]);

        var text = engine.Evaluate(AnyInput).AppendTo("line1");

        Assert.StartsWith("line1", text);
        StringAssert.Contains(text, "NOTE: msg-r1");
    }

    [TestMethod]
    public void Append_on_empty_output_returns_only_notes()
    {
        var engine = new GuidanceEngine([new AlwaysRule("r1")]);

        var text = engine.Evaluate(AnyInput).AppendTo("");

        Assert.StartsWith("NOTE: ", text);
    }
}
