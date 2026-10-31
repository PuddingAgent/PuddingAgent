using PuddingGrep;

namespace PuddingGrepTests;

/// <summary>
/// 三态匹配（<see cref="GrepMatcher.Match"/>）的契约：
/// <b>超时不得被折成「没命中」</b>。四态齐全 —— 命中 / 未命中 / 超时 / 模式非法抛。
/// <para>
/// 为什么要单独守：<c>IsMatch</c> / <c>FindAll</c> 在灾难性回溯时是**抛** <c>RegexMatchTimeoutException</c> 的，
/// 调用方若图省事写 <c>catch { return NoMatch; }</c>，就会把「跑不完」说成「确实没有」——
/// 那正是空洞否定（R0 要消灭的东西）。本文件把正确折法钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class GrepMatchOutcomeTests
{
    /// <summary>能稳定触发灾难性回溯的输入：一长串 a 后跟一个不能匹配的尾巴。</summary>
    private static string PathologicalLine() => new string('a', 60) + "!";

    [TestMethod]
    public void Match_LiteralHit_ReturnsMatched_WithSpans()
    {
        var result = GrepMatcher.Match(GrepPattern.Literal("needle"), "a needle b");

        Assert.AreEqual(GrepMatchOutcome.Matched, result.Outcome, "字面量命中应给出 Matched");
        Assert.IsTrue(result.HasMatch);
        Assert.HasCount(1, result.Spans);
        Assert.AreEqual(2, result.Spans[0].Index);
        Assert.AreEqual(6, result.Spans[0].Length);
    }

    [TestMethod]
    public void Match_LiteralMiss_ReturnsNoMatch_WithoutSpans()
    {
        var result = GrepMatcher.Match(GrepPattern.Literal("needle"), "nothing here");

        Assert.AreEqual(GrepMatchOutcome.NoMatch, result.Outcome, "确实没命中时才是 NoMatch");
        Assert.IsFalse(result.HasMatch);
        Assert.IsEmpty(result.Spans);
    }

    [TestMethod]
    public void Match_RegexCatastrophicBacktracking_ReturnsTimeout_NotException()
    {
        var pattern = GrepPattern.Regex("(a+)+$");

        var result = GrepMatcher.Match(pattern, PathologicalLine(), TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(
            GrepMatchOutcome.Timeout,
            result.Outcome,
            "超时必须表达为 Timeout —— 折成 NoMatch 等于把「跑不完」说成「确实没有」");
        Assert.IsFalse(result.HasMatch, "Timeout 不是命中");
        Assert.IsEmpty(result.Spans, "Timeout 不产生 span");
    }

    [TestMethod]
    public void Match_InvalidPattern_StillThrows()
    {
        var pattern = GrepPattern.Regex("([unclosed");
        Assert.IsNotNull(pattern.ValidationError, "control: 该模式应被判为非法");

        Assert.Throws<ArgumentException>(
            () => GrepMatcher.Match(pattern, "anything"),
            "模式坏了是调用方错误 —— 不得折进三态，否则「模式写错」会被读成「没有匹配」");
    }

    [TestMethod]
    public void Matched_RejectsEmptySpans()
    {
        Assert.Throws<ArgumentException>(
            () => GrepLineMatch.Matched([]),
            "空 spans 应表达为 NoMatch；Matched(空) 是自相矛盾的状态");
    }
}
