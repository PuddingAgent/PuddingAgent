namespace PuddingPathFilteringTests;

/// <summary>忽略栈的整体判定语义：后命中者胜出、取反、祖先剪枝、嵌套 .gitignore 优先级。</summary>
[TestClass]
public sealed class IgnoreStackTests
{
    private static readonly bool IgnoreCase = false;

    private static IgnoreStack Stack(params (string Base, string Line)[] entries)
        => new(entries.SelectMany(e => IgnoreFileParser.Parse("test", e.Base, new[] { e.Line }, IgnoreCase)));

    [TestMethod]
    public void LastMatchingRuleWins()
    {
        var stack = Stack((string.Empty, "*.log"), (string.Empty, "!keep.log"));

        Assert.IsTrue(stack.IsIgnored("x.log", isDirectory: false));
        Assert.IsFalse(stack.IsIgnored("keep.log", isDirectory: false));
    }

    [TestMethod]
    public void OrderMatters_LaterRuleOverridesEarlierNegation()
    {
        var stack = Stack((string.Empty, "!keep.log"), (string.Empty, "*.log"));

        Assert.IsTrue(stack.IsIgnored("keep.log", isDirectory: false),
            "a negation placed before the ignoring rule must not win");
    }

    [TestMethod]
    public void DeeperGitIgnoreFileOverridesRoot()
    {
        var stack = Stack(
            (string.Empty, "p.log"),
            (string.Empty, "sub/**"),
            ("sub", "!p.log"));

        Assert.IsTrue(stack.IsIgnored("p.log", isDirectory: false));
        Assert.IsFalse(stack.IsIgnored("sub/p.log", isDirectory: false),
            "the nested .gitignore has higher precedence than the root one");
    }

    [TestMethod]
    public void ParentDirectoryExclusionCannotBeNegated()
    {
        var stack = Stack((string.Empty, "docs/**"), (string.Empty, "!docs/keep/**"));

        Assert.IsTrue(stack.IsIgnored("docs/keep/y.md", isDirectory: false),
            "gitignore(5): a file cannot be re-included when its parent directory is excluded");

        // 只有当目录本身没被排除时，取反才生效 —— 这是 oracle 实测到的对照面。
        var other = Stack((string.Empty, ".axoCover/*"), (string.Empty, "!.axoCover/settings.json"));
        Assert.IsFalse(other.IsIgnored(".axoCover/settings.json", isDirectory: false));
        Assert.IsTrue(other.IsIgnored(".axoCover/other.json", isDirectory: false));
    }

    [TestMethod]
    public void DirectoryOnlyRulesNeedDirectoryKind()
    {
        var stack = Stack((string.Empty, "logs/"));

        Assert.IsTrue(stack.IsIgnored("logs", isDirectory: true));
        Assert.IsTrue(stack.IsIgnored("logs/x.txt", isDirectory: false), "the ancestor directory is pruned");
        Assert.IsFalse(stack.IsIgnored("logs", isDirectory: false),
            "a trailing-slash rule must not match a file of the same name");
    }

    [TestMethod]
    public void NonAnchoredRulesMatchTheBasenameAtAnyDepth()
    {
        var stack = Stack((string.Empty, "node_modules"));

        Assert.IsTrue(stack.IsIgnored("a/b/node_modules", isDirectory: true));
        Assert.IsTrue(stack.IsIgnored("node_modules/pkg/x.js", isDirectory: false));
        Assert.IsFalse(stack.IsIgnored("a/node_modules_keep/x.js", isDirectory: false));
    }

    [TestMethod]
    public void AnchoredRulesAreRelativeToTheGitIgnoreDirectory()
    {
        var stack = Stack(("sub", "inner/keep.txt"));

        Assert.IsTrue(stack.IsIgnored("sub/inner/keep.txt", isDirectory: false),
            "a pattern containing '/' is relative to the .gitignore's own directory");
        Assert.IsFalse(stack.IsIgnored("sub/a/inner/keep.txt", isDirectory: false),
            "an anchored pattern does not float down into deeper directories");
    }

    [TestMethod]
    public void EmptyStackNeverIgnores()
    {
        Assert.IsFalse(IgnoreStack.Empty.IsIgnored("anything/at/all.cs", isDirectory: false));
        Assert.AreEqual(0, IgnoreStack.Empty.Count);
        Assert.IsFalse(IgnoreStack.Empty.IsIgnored(string.Empty, isDirectory: true));
    }

    [TestMethod]
    public void Reason_namesTheDecidingRule()
    {
        var self = Stack((string.Empty, "*.pdb"));
        Assert.IsTrue(self.IsIgnored("bin/x.pdb", isDirectory: false, out var selfReason));
        StringAssert.Contains(selfReason!, "*.pdb");
        Assert.DoesNotContain("ancestor", selfReason);

        // 目录专属规则命中「祖先目录」时，原因里要写明是哪一个祖先定的案。
        var ancestor = Stack((string.Empty, "bin/"));
        Assert.IsTrue(ancestor.IsIgnored("bin/x.pdb", isDirectory: false, out var ancestorReason));
        StringAssert.Contains(ancestorReason!, "ancestor 'bin'");
    }

    [TestMethod]
    public void TryDecide_reportsNegatedRuleForKeep()
    {
        var stack = Stack((string.Empty, "*.log"), (string.Empty, "!keep.log"));

        Assert.IsFalse(stack.TryDecide("keep.log", isDirectory: false, out var rule, out _));
        Assert.IsNotNull(rule);
        Assert.AreEqual("!keep.log", rule!.RawLine);
        Assert.IsTrue(rule.Negated);
    }
}
