using System.Reflection;
using PuddingGrep;
using PuddingGrepTests;

namespace PuddingGrepTests;

/// <summary>
/// 口径对照：内核的判定必须与「直接用 BCL」的结果逐条一致。
/// 这不是重复实现，而是把 BCL 当 oracle —— 内核一旦偷偷改了语义（大小写、文化、重叠），这里就会红。
/// </summary>
[TestClass]
public sealed class GrepMatcherOracleTests
{
    public static IEnumerable<object[]> Lines => new[]
    {
        new object[] { "needle" },
        new object[] { "NEEDLE" },
        new object[] { "a needle b" },
        new object[] { "needleneedle" },
        new object[] { "中文关键字测试" },
        new object[] { "" },
        new object[] { "   " },
        new object[] { "regex.dots.here" },
        new object[] { @"back\slash" },
        new object[] { "(parens)" },
    };

    [TestMethod]
    [DynamicData(nameof(Lines))]
    public void Literal_Matches_SameAs_BCL_IndexOf(string line)
    {
        var pattern = GrepPattern.Literal("needle");
        var expected = line.IndexOf("needle", StringComparison.OrdinalIgnoreCase) >= 0;

        Assert.AreEqual(expected, GrepMatcher.IsMatch(pattern, line),
            $"字面量匹配必须与 OrdinalIgnoreCase 的 IndexOf 一致；line='{line}'");
    }

    [TestMethod]
    [DynamicData(nameof(Lines))]
    public void Literal_CaseSensitive_Matches_SameAs_Ordinal_IndexOf(string line)
    {
        var pattern = GrepPattern.Literal("needle", caseSensitive: true);
        var expected = line.IndexOf("needle", StringComparison.Ordinal) >= 0;

        Assert.AreEqual(expected, GrepMatcher.IsMatch(pattern, line),
            $"区分大小写时必须与 Ordinal 的 IndexOf 一致；line='{line}'");
    }

    [TestMethod]
    [DynamicData(nameof(Lines))]
    public void Regex_Matches_SameAs_BCL_Regex(string line)
    {
        var pattern = GrepPattern.Regex(@"n[ee]{2}dle");
        var expected = System.Text.RegularExpressions.Regex.IsMatch(
            line, @"n[ee]{2}dle",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        Assert.AreEqual(expected, GrepMatcher.IsMatch(pattern, line),
            $"正则匹配必须与 BCL Regex 一致；line='{line}'");
    }

    /// <summary>字面量不得被当作正则：`.` 只能匹配真正的点号。</summary>
    [TestMethod]
    public void Literal_IsNotTreatedAsRegex()
    {
        var pattern = GrepPattern.Literal("a.b");

        Assert.IsTrue(GrepMatcher.IsMatch(pattern, "xa.by"));
        Assert.IsFalse(GrepMatcher.IsMatch(pattern, "axby"), "字面量的点号不能匹配任意字符。");
    }

    /// <summary>FindAll 的跨度必须与 BCL 一致，且不重叠。</summary>
    [TestMethod]
    public void FindAll_Literal_SpansMatchAndDoNotOverlap()
    {
        var pattern = GrepPattern.Literal("aa");
        var line = "aaaa";

        var spans = GrepMatcher.FindAll(pattern, line);

        Assert.HasCount(2, spans, "非重叠语义：aaaa 里 aa 只能命中两次。");
        Assert.AreEqual(0, spans[0].Index);
        Assert.AreEqual(2, spans[1].Index);
    }

    [TestMethod]
    public void FindAll_Regex_MatchesBclIndexes()
    {
        var pattern = GrepPattern.Regex(@"\d+");
        var line = "a12b345c6";

        var spans = GrepMatcher.FindAll(pattern, line);
        var expected = System.Text.RegularExpressions.Regex.Matches(line, @"\d+")
            .Select(m => (m.Index, m.Length))
            .ToArray();

        CollectionAssert.AreEqual(
            expected,
            spans.Select(s => (s.Index, s.Length)).ToArray());
    }
}
