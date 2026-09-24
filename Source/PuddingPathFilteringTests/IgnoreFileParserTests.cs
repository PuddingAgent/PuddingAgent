namespace PuddingPathFilteringTests;

/// <summary>行级解析语义（对应 gitignore(5) 与 oracle 实测的边角行为）。</summary>
[TestClass]
public sealed class IgnoreFileParserTests
{
    private static IgnoreRule? Parse(string line) => IgnoreFileParser.ParseLine("test", string.Empty, 1, line);

    [TestMethod]
    public void CommentsAndBlankLinesAreNotRules()
    {
        Assert.IsNull(Parse("# a comment"));
        Assert.IsNull(Parse(string.Empty));
        Assert.IsNull(Parse("   "), "a line of only spaces collapses to empty");
        Assert.IsNull(Parse("/"), "a lone '/' has nothing left after stripping");
    }

    [TestMethod]
    public void HashAndBangCanBeEscaped()
    {
        var hash = Parse(@"\#literal");
        Assert.IsNotNull(hash);
        Assert.AreEqual("#literal", hash!.Pattern);

        var bang = Parse(@"\!literal");
        Assert.IsNotNull(bang);
        Assert.AreEqual("!literal", bang!.Pattern);
        Assert.IsFalse(bang.Negated);
    }

    [TestMethod]
    public void NegationIsRecognised()
    {
        var rule = Parse("!keep.log");
        Assert.IsNotNull(rule);
        Assert.IsTrue(rule!.Negated);
        Assert.AreEqual("keep.log", rule.Pattern);
    }

    [TestMethod]
    public void TrailingSpacesAreTrimmedButEscapedOnesSurvive()
    {
        Assert.AreEqual("trail.txt", IgnoreFileParser.TrimTrailingSpaces("trail.txt   "));

        // 被转义的行尾空格不被裁掉 —— 注意反斜杠本身<b>保留</b>在行里（git 也是这么打印的，
        // 反斜杠由 wildmatch 在匹配时解释），oracle 实测 git 打印的 pattern 正是 `trailsp\ `。
        Assert.AreEqual(@"trailsp\ ", IgnoreFileParser.TrimTrailingSpaces(@"trailsp\ "));
        Assert.AreEqual(@"trailsp\", IgnoreFileParser.TrimTrailingSpaces(@"trailsp\"), "a trailing backslash stops trimming");

        var rule = Parse("trail.txt   ");
        Assert.AreEqual("trail.txt", rule!.Pattern);
        Assert.AreEqual("trail.txt", rule.RawLine, "the diagnostic text is the trimmed line");
    }

    [TestMethod]
    public void LeadingSpacesAreSignificant()
    {
        // oracle 实测：模式「   sptest.txt」只命中带三个前导空格的同名路径。
        var rule = Parse("   sptest.txt");
        Assert.IsNotNull(rule);
        Assert.AreEqual("   sptest.txt", rule!.Pattern);
        Assert.IsTrue(rule.Matches("   sptest.txt", isDirectory: false));
        Assert.IsFalse(rule.Matches("sptest.txt", isDirectory: false));
        Assert.IsFalse(rule.Matches("  sptest.txt", isDirectory: false),
            "two leading spaces is a different filename than three");
    }

    [TestMethod]
    public void TrailingSlashMeansDirectoryOnly()
    {
        var rule = Parse("logs/");
        Assert.IsNotNull(rule);
        Assert.IsTrue(rule!.DirectoryOnly);
        Assert.AreEqual("logs", rule.Pattern);
        Assert.IsFalse(rule.Anchored, "a trailing slash alone does not make the pattern anchored");
    }

    [TestMethod]
    public void LeadingOrMiddleSlashMeansAnchored()
    {
        var leading = Parse("/root-only/");
        Assert.IsNotNull(leading);
        Assert.IsTrue(leading!.Anchored);
        Assert.IsTrue(leading.DirectoryOnly);
        Assert.AreEqual("root-only", leading.Pattern);

        var middle = Parse("sub-*/x.txt");
        Assert.IsNotNull(middle);
        Assert.IsTrue(middle!.Anchored);
        Assert.AreEqual("sub-*/x.txt", middle.Pattern);
    }

    [TestMethod]
    public void LineNumbersAndSourceArePreserved()
    {
        var rules = IgnoreFileParser.Parse("x/.gitignore", "x", new[] { "# c", "*.log", "!keep.log" });

        Assert.HasCount(2, rules);
        Assert.AreEqual(2, rules[0].LineNumber);
        Assert.AreEqual(3, rules[1].LineNumber);
        Assert.AreEqual("x/.gitignore", rules[0].Source);
        Assert.AreEqual("x", rules[0].BaseDirectory);
    }

    [TestMethod]
    public void CrlfAndBareCrAreHandled()
    {
        var rules = IgnoreFileParser.ParseText("t", string.Empty, "*.log\r\n!keep.log\r\n");

        Assert.HasCount(2, rules);
        Assert.AreEqual("*.log", rules[0].RawLine);
        Assert.AreEqual("!keep.log", rules[1].RawLine);
        Assert.IsTrue(rules[1].Negated);
    }

    [TestMethod]
    public void DiagnosicPatternKeepsTheNegationPrefix()
    {
        // 与 `git check-ignore -v` 的 pattern 字段逐字对齐（含 '!'）
        Assert.AreEqual("!keep.log", Parse("!keep.log")!.DiagnosticPattern);
        Assert.AreEqual("trail.txt", Parse("trail.txt  ")!.DiagnosticPattern);
    }
}
