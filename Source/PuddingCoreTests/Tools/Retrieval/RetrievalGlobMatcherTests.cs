// ADR-089 U0 残差 G1：RetrievalGlobMatcher（统一 glob 匹配合同）单测。
// 覆盖任务书第 6 节全部 14 条必测用例：空 glob 匹配一切、通配符语义（* 可为零长、? 恰一个、
// 不跨分隔符）、** 前缀剥离与分隔符归一、精确语义（反证实现 A/B/C 三套既有语义的分叉）、
// 大小写控制、[] 字面、Unicode 文件名，以及正则缓存键稳定性（1000 次重复调用结果恒定）。
using PuddingCode.Tools.Retrieval;

namespace PuddingCoreTests.Tools.Retrieval;

/// <summary>RetrievalGlobMatcher 的合同行为单测（internal 可见性经 InternalsVisibleTo 授予）。</summary>
[TestClass]
public sealed class RetrievalGlobMatcherTests
{
    // ── 用例 1：null / 空 / 全空白 glob → 匹配一切 ────────────────────────

    [TestMethod]
    public void NullOrEmptyOrWhitespaceGlob_Matches_Everything()
    {
        Assert.IsTrue(RetrievalGlobMatcher.Matches("anything.txt", null, glob: null));
        Assert.IsTrue(RetrievalGlobMatcher.Matches("anything.txt", "x/y/anything.txt", string.Empty));
        Assert.IsTrue(RetrievalGlobMatcher.Matches("anything.txt", null, "   "));
        Assert.IsTrue(RetrievalGlobMatcher.Matches("任意文件.md", "深/目录/任意文件.md", "\t"));

        // 匹配一切属于「无通配符」空合同，HasWildcards 必须为 false。
        Assert.IsFalse(RetrievalGlobMatcher.HasWildcards(null));
        Assert.IsFalse(RetrievalGlobMatcher.HasWildcards(string.Empty));
        Assert.IsFalse(RetrievalGlobMatcher.HasWildcards("a.b"));
    }

    // ── 用例 2：*.cs 匹配 a.cs，不匹配 a.txt ─────────────────────────────

    [TestMethod]
    public void StarExtension_Matches_Extension_And_Rejects_Other()
    {
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("a.cs", "*.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("AnyName.cs", "*.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("a.txt", "*.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("cs", "*.cs")); // 前缀非通配
    }

    // ── 用例 3：*.txt 不匹配 a.txtx（显式反证 Win32 怪癖，实现 B）─────────

    [TestMethod]
    public void StarTxt_Does_Not_Match_Longer_Extension_Unlike_Win32()
    {
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("a.txt", "*.txt"));
        // Win32 searchPattern 的 *.txt 会错误命中 a.txtx（8.3 短名怪癖）；本合同必须拒绝。
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("a.txtx", "*.txt"));
    }

    // ── 用例 4：*.* 匹配无扩展名的 Makefile（显式反证实现 A 的 MatchesSimpleExpression）──

    [TestMethod]
    public void StarDotStar_Matches_Extensionless_File_Unlike_SimpleExpression()
    {
        // 实现 A 对 *.* 要求文件名含点，会拒绝 Makefile；本合同把 *.* 视为「含点或不限」→ 命中。
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("Makefile", "*.*"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("README", "*.*"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("report.pdf", "*.*"));
    }

    // ── 用例 5：? 恰好匹配一个字符 ───────────────────────────────────────

    [TestMethod]
    public void QuestionMark_Matches_Exactly_One_Char()
    {
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("ab.cs", "a?.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("a.cs", "a?.cs"));  // 零字符
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("abc.cs", "a?.cs")); // 两个字符
    }

    // ── 用例 6：* 可匹配零长度 ───────────────────────────────────────────

    [TestMethod]
    public void Star_Can_Match_Zero_Length()
    {
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("a.cs", "a*.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("abc.cs", "a*.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("ba.cs", "a*.cs"));
    }

    // ── 用例 7：** / **\ 前缀剥离后等价于剥离目标 ─────────────────────────

    [TestMethod]
    public void DoubleStar_Prefix_Stripped_To_Equivalent_Glob()
    {
        Assert.AreEqual("*.cs", RetrievalGlobMatcher.Normalize("**/*.cs"));
        Assert.AreEqual("*.cs", RetrievalGlobMatcher.Normalize("**\\*.cs"));
        Assert.AreEqual(string.Empty, RetrievalGlobMatcher.Normalize("**"));
        Assert.AreEqual(string.Empty, RetrievalGlobMatcher.Normalize("**/"));

        // 三种写法行为一致：**/a.cs 语义为「任意深度下的 a.cs」→ 文件名语义。
        Assert.AreEqual(
            RetrievalGlobMatcher.MatchesFileName("a.cs", "*.cs"),
            RetrievalGlobMatcher.Matches("a.cs", "Src/Nested/a.cs", "**/*.cs"));
        Assert.AreEqual(
            RetrievalGlobMatcher.MatchesFileName("a.cs", "*.cs"),
            RetrievalGlobMatcher.Matches("a.cs", "Src/Nested/a.cs", "**\\*.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.Matches("a.cs", "Src/Nested/a.cs", "**/*.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.Matches("a.txt", "Src/Nested/a.txt", "**/*.cs"));

        // ** 剥离后为空 → 匹配一切（规范 2 + 规范 1）。
        Assert.IsTrue(RetrievalGlobMatcher.Matches("任何", "任意/路径/任何", "**"));
    }

    // ── 用例 8：含分隔符 → 按相对路径匹配；无分隔符 → 只看文件名 ─────────

    [TestMethod]
    public void Glob_With_Separator_Matches_RelativePath_Only()
    {
        Assert.IsTrue(RetrievalGlobMatcher.Matches("a.cs", "Source/a.cs", "Source/*.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.Matches("a.cs", "Other/a.cs", "Source/*.cs"));
        // 相对路径未提供时不能假装命中。
        Assert.IsFalse(RetrievalGlobMatcher.Matches("a.cs", null, "Source/*.cs"));

        // 规范 4：glob 无分隔符 → 只匹配文件名，相对路径中的目录部分不参与。
        Assert.IsTrue(RetrievalGlobMatcher.Matches("a.cs", "Other/a.cs", "*.cs"));
    }

    // ── 用例 9：反斜杠归一（glob 与目标两侧）─────────────────────────────

    [TestMethod]
    public void Backslash_Is_Normalized_On_Both_Glob_And_Target()
    {
        Assert.IsTrue(RetrievalGlobMatcher.Matches("a.cs", "Source\\a.cs", "Source\\*.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.Matches("a.cs", "Source\\a.cs", "Source/*.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesRelativePath("Source\\Sub\\a.cs", "Source/Sub/*.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.Matches("a.cs", "Other\\a.cs", "Source\\*.cs"));
    }

    // ── 用例 10：无通配符 → 精确语义（反证实现 C 的子串包含）──────────────

    [TestMethod]
    public void NoWildcard_Glob_Is_Exact_Not_Substring()
    {
        // 实现 C 无通配时做子串包含，会把 Program.cs 判为命中 "Program"；本合同必须精确。
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("Program.cs", "Program"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("Program.cs", "Program.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("MyProgram.cs", "Program.cs"));

        // 相对路径目标同理：精确比较作用于归一化后的整条路径。
        Assert.IsFalse(RetrievalGlobMatcher.MatchesRelativePath("Src/Program.cs", "Program.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesRelativePath("Src/Program.cs", "Src/Program.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesRelativePath("Src\\Program.cs", "Src/Program.cs"));
    }

    // ── 用例 11：大小写由 ignoreCase 控制（默认 true）────────────────────

    [TestMethod]
    public void Case_Handling_Controlled_By_IgnoreCase_Parameter()
    {
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("a.cs", "*.CS", ignoreCase: false));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("a.cs", "*.CS")); // 默认 ignoreCase: true
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("a.cs", "A.CS", ignoreCase: false));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("a.cs", "A.CS"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("A.cs", "a.cs", ignoreCase: false));
    }

    // ── 用例 12：[ ] 按字面处理（{ } 同理）───────────────────────────────

    [TestMethod]
    public void Brackets_Are_Treated_Literally()
    {
        Assert.IsFalse(RetrievalGlobMatcher.HasWildcards("a[1].cs")); // [] 不算通配符
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("a[1].cs", "a[1].cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("a1.cs", "a[1].cs"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("a{b}.cs", "a{b}.cs"));
    }

    // ── 用例 13：中文 / Unicode 文件名 ───────────────────────────────────

    [TestMethod]
    public void Unicode_FileNames_Supported()
    {
        Assert.IsTrue(RetrievalGlobMatcher.MatchesFileName("测试1.md", "测试?.md"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("测试12.md", "测试?.md"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesFileName("测试.md", "测试?.md"));
        Assert.IsTrue(RetrievalGlobMatcher.Matches("设计文档.md", "Docs/架构/设计文档.md", "**/设计文档.md"));
    }

    // ── 用例 14：缓存键稳定性（同键 1000 次重复调用结果恒定，穿插多组参数暴露缓存键错误）──

    [TestMethod]
    public void RegexCache_Key_Stability_Under_Repeated_Calls()
    {
        var cases = new (string File, string? RelativePath, string Glob, bool IgnoreCase, bool Expected)[]
        {
            ("a.cs", null, "*.cs", true, true),
            ("a.cs", null, "*.cs", false, true),
            ("a.CS", null, "*.cs", true, true),
            ("a.CS", null, "*.cs", false, false), // 敏感模式下大小写不同 → 不命中
            ("a.cs", "Src/a.cs", "Src/*.cs", true, true),
            ("a.cs", "Other/a.cs", "Src/*.cs", true, false),
            ("a.cs", "Src/Deep/a.cs", "Src/*.cs", true, false), // * 不跨分隔符
            ("Makefile", null, "*.*", true, true),
            ("a.txtx", null, "*.txt", true, false),
        };

        for (var iteration = 0; iteration < 1000; iteration++)
        {
            foreach (var (file, relativePath, glob, ignoreCase, expected) in cases)
            {
                Assert.AreEqual(
                    expected,
                    RetrievalGlobMatcher.Matches(file, relativePath, glob, ignoreCase),
                    $"iteration={iteration}, glob='{glob}', ignoreCase={ignoreCase}");
            }
        }
    }

    // ── 规范 8 补充：* 与 ? 不跨越路径分隔符 ─────────────────────────────

    [TestMethod]
    public void Star_And_Question_Do_Not_Cross_Path_Separators()
    {
        Assert.IsTrue(RetrievalGlobMatcher.MatchesRelativePath("Source/x/a.cs", "Source/*/a.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesRelativePath("Source/x/y/a.cs", "Source/*/a.cs"));
        Assert.IsTrue(RetrievalGlobMatcher.MatchesRelativePath("Source/b.cs", "Source/?.cs"));
        Assert.IsFalse(RetrievalGlobMatcher.MatchesRelativePath("Source/x/b.cs", "Source/?.cs"));
    }
}
