namespace PuddingPathFilteringTests;

/// <summary>通配符匹配的边界语义（每条都对应 git 的一条可观测行为，见 GitWildcard 的类注释）。</summary>
[TestClass]
public sealed class GitWildcardTests
{
    [DataTestMethod]
    [DataRow("*.log", "x.log", true)]
    [DataRow("*.log", "a/x.log", false, DisplayName = "单个星不跨 /")]
    [DataRow("a*b", "ab", true)]
    [DataRow("a*b", "a/b", false)]
    [DataRow("a?c", "abc", true)]
    [DataRow("a?c", "a/c", false, DisplayName = "问号不跨 /")]
    [DataRow("a?c", "ac", false)]
    public void SingleStarAndQuestionDoNotCrossSeparator(string pattern, string text, bool expected)
        => Assert.AreEqual(expected, GitWildcard.IsMatch(pattern, text));

    [DataTestMethod]
    [DataRow("**/foo", "foo", true, DisplayName = "**/ 可匹配零个目录")]
    [DataRow("**/foo", "a/foo", true)]
    [DataRow("**/foo", "a/b/foo", true)]
    [DataRow("**/foo", "a/foo/b", false)]
    [DataRow("a/**/b", "a/b", true)]
    [DataRow("a/**/b", "a/m/b", true)]
    [DataRow("a/**/b", "a/m/n/b", true)]
    [DataRow("docs/**", "docs/x.md", true)]
    [DataRow("docs/**", "docs/keep/y.md", true)]
    [DataRow("p**q/r.txt", "pXq/r.txt", true, DisplayName = "非边界的 ** 退化为 *")]
    [DataRow("p**q/r.txt", "p/q/r.txt", false)]
    public void DoubleStarSemantics(string pattern, string text, bool expected)
        => Assert.AreEqual(expected, GitWildcard.IsMatch(pattern, text));

    [DataTestMethod]
    [DataRow("[abc].txt", "a.txt", true)]
    [DataRow("[abc].txt", "d.txt", false)]
    [DataRow("[a-c]rng.txt", "brng.txt", true)]
    [DataRow("[a-c]rng.txt", "zrng.txt", false)]
    [DataRow("[^a-c]neg.txt", "zneg.txt", true)]
    [DataRow("[^a-c]neg.txt", "aneg.txt", false)]
    [DataRow("[!abc]x.txt", "ax.txt", false)]
    [DataRow("[0-9].dat", "5.dat", true)]
    [DataRow("[]]lit.txt", "]lit.txt", true, DisplayName = "字符类首位的 ] 是字面量")]
    [DataRow("q?estion.txt", "q1estion.txt", true)]
    public void CharacterClasses(string pattern, string text, bool expected)
        => Assert.AreEqual(expected, GitWildcard.IsMatch(pattern, text));

    [TestMethod]
    public void UnterminatedClass_never_matches()
    {
        Assert.IsFalse(GitWildcard.IsMatch("uncl[", "uncl["));
        Assert.IsFalse(GitWildcard.IsMatch("uncl[", "unclx"));
    }

    [TestMethod]
    public void EscapesAndEscapedMetacharacters()
    {
        Assert.IsTrue(GitWildcard.IsMatch(@"hash\#file", "hash#file"));
        Assert.IsTrue(GitWildcard.IsMatch(@"\!bang.txt", "!bang.txt"));
        Assert.IsTrue(GitWildcard.IsMatch(@"esc\ aped.txt", "esc aped.txt"));
        Assert.IsFalse(GitWildcard.IsMatch("trailing\\", "trailing\\"));
    }

    [TestMethod]
    public void BracesAreLiteral()
    {
        Assert.IsTrue(GitWildcard.IsMatch("a{b}c", "a{b}c"));
        Assert.IsFalse(GitWildcard.IsMatch("a{b}c", "abc"));
    }

    [TestMethod]
    public void CaseFoldingIsOptIn()
    {
        Assert.IsFalse(GitWildcard.IsMatch("*.LOG", "x.log"));
        Assert.IsTrue(GitWildcard.IsMatch("*.LOG", "x.log", ignoreCase: true));
    }
}
