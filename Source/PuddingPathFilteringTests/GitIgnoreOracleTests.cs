using System.Text;

namespace PuddingPathFilteringTests;

/// <summary>
/// <b>D3 核心验收</b>：把我们实现的 .gitignore 语义与真实 git 的判定逐条对照。
/// oracle 语料两份，共 192 条路径（要求 ≥100），每条同时断言：
/// <list type="number">
/// <item>结论（ignore / keep）与 git 一致；</item>
/// <item>「定案规则的原文」与 <c>git check-ignore -v</c> 打印的 pattern 逐字一致
///       —— 只对结论会让「恰好结论相同但原因不同」漏网。</item>
/// </list>
/// 明细表写到测试输出目录（<c>U4-4-oracle-detail.tsv</c>），供报告 §6 直接引用。
/// </summary>
[TestClass]
public sealed class GitIgnoreOracleTests
{
    [TestMethod]
    public void Real_workspace_gitignore_agrees_with_git_check_ignore()
    {
        // 冻结的仓库 .gitignore 快照（根 + 唯一的嵌套 .gitignore），基准目录显式给出。
        var root = OracleFixture.ReadPatternSource("real-workspace-root.gitignore.txt");
        var nested = OracleFixture.ReadPatternSource("real-workspace-PuddingPlatformAdmin.gitignore.txt");

        var stack = new IgnoreStack(
            IgnoreFileParser.ParseText(".gitignore", string.Empty, root, IgnoreCase)
                .Concat(IgnoreFileParser.ParseText(
                    "Source/PuddingPlatformAdmin/.gitignore",
                    "Source/PuddingPlatformAdmin",
                    nested,
                    IgnoreCase)));

        var rows = OracleFixture.ReadRows("real-workspace-oracle.tsv");
        Assert.IsGreaterThanOrEqualTo(100, rows.Count, "oracle corpus must cover at least 100 paths");

        var table = OracleFixture.Compare("RealWorkspace（仓库真实 .gitignore 冻结快照）", stack, rows, out var failures);
        WriteDetail("real-workspace", table);

        Assert.IsEmpty(failures, $"{failures.Count}/{rows.Count} rows disagree with git:\n" + string.Join("\n", failures));
    }

    [TestMethod]
    public void Synthetic_workspace_gitignore_agrees_with_git_check_ignore()
    {
        var root = OracleFixture.ReadPatternSource("synthetic-workspace-root.gitignore.txt");
        var sub = OracleFixture.ReadPatternSource("synthetic-workspace-sub.gitignore.txt");

        var stack = new IgnoreStack(
            IgnoreFileParser.ParseText(".gitignore", string.Empty, root, IgnoreCase)
                .Concat(IgnoreFileParser.ParseText("sub/.gitignore", "sub", sub, IgnoreCase)));

        var rows = OracleFixture.ReadRows("synthetic-workspace-oracle.tsv");
        var table = OracleFixture.Compare("SyntheticWorkspace（边界语义语料）", stack, rows, out var failures);
        WriteDetail("synthetic-workspace", table);

        Assert.IsEmpty(failures, $"{failures.Count}/{rows.Count} rows disagree with git:\n" + string.Join("\n", failures));
    }

    /// <summary>两份语料合计必须 ≥100 条（任务书 §D3 的抽样下限）。</summary>
    [TestMethod]
    public void Oracle_corpus_size_is_at_least_one_hundred_paths()
    {
        var total = OracleFixture.ReadRows("real-workspace-oracle.tsv").Count
                    + OracleFixture.ReadRows("synthetic-workspace-oracle.tsv").Count;

        Assert.IsGreaterThanOrEqualTo(100, total, $"frozen oracle covers {total} paths");
    }

    /// <summary>
    /// 语料必须真的覆盖「取反」：否则删掉 <c>!</c> 处理也能全绿（假绿）。
    /// 这里断言两份语料里都存在「因取反而 keep」的行。
    /// </summary>
    [TestMethod]
    public void Oracle_corpus_contains_negation_decided_rows()
    {
        foreach (var file in new[] { "real-workspace-oracle.tsv", "synthetic-workspace-oracle.tsv" })
        {
            var rows = OracleFixture.ReadRows(file);
            var negatedKeep = rows.Count(r => !r.ExpectedIgnored && r.Pattern.StartsWith('!'));
            Assert.IsGreaterThan(0, negatedKeep, $"{file} must contain at least one '!' re-included path");
        }
    }

    /// <summary>
    /// 语料必须真的覆盖「目录剪枝压过取反」：<c>docs/**</c> + <c>!docs/keep/**</c> 下
    /// <c>docs/keep/y.md</c> 仍为 ignore（gitignore(5) 的父目录规则）。
    /// </summary>
    [TestMethod]
    public void Oracle_corpus_contains_parent_directory_pruning_rows()
    {
        var rows = OracleFixture.ReadRows("synthetic-workspace-oracle.tsv");
        var pruned = rows.Single(r => r.Path == "docs/keep/y.md");
        Assert.IsTrue(pruned.ExpectedIgnored, "git must keep docs/keep/y.md ignored despite !docs/keep/**");
        Assert.AreEqual("docs/**", pruned.Pattern);
    }

    /// <summary>
    /// oracle 的运行环境是 Windows（git 2.53.0.windows.2，<c>core.ignoreCase=true</c>，已实测）。
    /// 两份语料都含「大小写不同仍命中」的行（<c>caseprobe.txt</c> → <c>CASEPROBE.TXT</c>），
    /// 所以 <b>必须</b>用 <c>ignoreCase: true</c> 才能与 git 一致 —— 该行不是装饰性的。
    /// </summary>
    private const bool IgnoreCase = true;

    /// <summary>把 <c>core.ignoreCase</c> 的证据直接钉在测试里（原始 git 输出见报告 §6）。</summary>
    [TestMethod]
    public void Windows_case_folding_is_required_to_match_git()
    {
        var root = OracleFixture.ReadPatternSource("synthetic-workspace-root.gitignore.txt");

        var folding = new IgnoreStack(IgnoreFileParser.ParseText(".gitignore", string.Empty, root, ignoreCase: true));
        Assert.IsTrue(folding.IsIgnored("CASEPROBE.TXT", isDirectory: false),
            "core.ignoreCase=true means pattern 'caseprobe.txt' also matches 'CASEPROBE.TXT'");

        var strict = new IgnoreStack(IgnoreFileParser.ParseText(".gitignore", string.Empty, root, ignoreCase: false));
        Assert.IsFalse(strict.IsIgnored("CASEPROBE.TXT", isDirectory: false),
            "a 取反验证：关闭折叠后该路径不再被忽略，所以上面的断言确实是折叠在起作用");
    }

    private void WriteDetail(string name, string table)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"U4-4-oracle-detail-{name}.tsv");
        File.WriteAllText(path, table.Replace("\r\n", "\n"), new UTF8Encoding(false));
        TestContext.WriteLine($"detail table -> {path}");
    }

    public TestContext TestContext { get; set; } = null!;
}
