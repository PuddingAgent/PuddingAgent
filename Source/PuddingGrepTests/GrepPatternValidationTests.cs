using PuddingGrep;

namespace PuddingGrepTests;

/// <summary>
/// 失败语义：模式无效必须**显式失败**，不能被当成「没有匹配」。
/// 这是错误否定的源头 —— 上层无法区分「搜了没结果」与「模式根本用不了」。
/// </summary>
[TestClass]
public sealed class GrepPatternValidationTests
{
    [TestMethod]
    public void EmptyLiteral_IsRejected()
    {
        var pattern = GrepPattern.Literal("");

        Assert.IsNotNull(pattern.ValidationError);
        Assert.ThrowsExactly<ArgumentException>(() => GrepMatcher.IsMatch(pattern, "anything"));
    }

    [TestMethod]
    public void EmptyRegex_IsRejected()
    {
        var pattern = GrepPattern.Regex("");

        Assert.IsNotNull(pattern.ValidationError);
        Assert.ThrowsExactly<ArgumentException>(() => GrepMatcher.IsMatch(pattern, "anything"));
    }

    [TestMethod]
    public void InvalidRegex_IsRejectedAtConstruction()
    {
        var pattern = GrepPattern.Regex("(unclosed");

        Assert.IsNotNull(pattern.ValidationError);
        StringAssert.Contains(pattern.ValidationError!, "invalid regex");
        Assert.ThrowsExactly<ArgumentException>(() => GrepMatcher.FindAll(pattern, "x"));
    }

    [TestMethod]
    public void ValidPatterns_HaveNoValidationError()
    {
        Assert.IsNull(GrepPattern.Literal("x").ValidationError);
        Assert.IsNull(GrepPattern.Regex(@"\d+").ValidationError);
    }

    /// <summary>
    /// 灾难性回溯必须被超时打断，而不是挂住调用线程。
    /// 用 `(a+)+$` 对一长串 a 加一个 b —— 经典指数回溯。
    /// </summary>
    [TestMethod]
    public void CatastrophicBacktracking_TimesOutInsteadOfHanging()
    {
        var pattern = GrepPattern.Regex("(a+)+$");
        var line = new string('a', 40) + "b";

        Assert.ThrowsExactly<System.Text.RegularExpressions.RegexMatchTimeoutException>(
            () => GrepMatcher.IsMatch(pattern, line, TimeSpan.FromMilliseconds(50)));
    }
}
