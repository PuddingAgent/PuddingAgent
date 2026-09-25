using PuddingFullTextIndex.Cli;

namespace PuddingFullTextIndex.Cli.Tests;

/// <summary>
/// <c>plan</c>：A1（干跑零写入 + 数值与磁盘一致）、A2（多 scope / 嵌套拒绝）、<c>--json</c> 形状、预算只做报表。
/// <para>
/// ⚠️ 本文件的**每个** plan 用例都显式传 <c>--index-root</c> 到临时索引根：
/// M1 变异（"plan 顺带创建索引根"）一旦落回，若此处缺省就会去动生产索引根 —— 这是红线守卫。
/// </para>
/// </summary>
[TestClass]
public sealed class SupplyCliPlanTests
{
    /// <summary>A1：<c>plan</c> 打印的 FileCount/CorpusBytes 与磁盘一致，且索引根递归条目快照差 == 0。</summary>
    [TestMethod]
    public void Plan_Is_A_Dry_Run_That_Writes_Nothing_And_Matches_The_Disk()
    {
        using var fixture = new CliFixture();
        var alphaPath = fixture.Write("alpha.cs", "class Alpha { }");          // 15 字节
        var docPath = fixture.Write("notes.md", "# Doc");                      // 5 字节
        fixture.Write(Path.Combine("bin", "ignored.cs"), "class Ignored { }"); // 噪声目录：被剪枝
        fixture.Write("image.png", "not indexable");                           // 非白名单
        fixture.Write("empty.cs", string.Empty);                               // 空文件：引擎也不索引

        var host = new CliTestHost { BuildBehaviour = (_, _) => throw new InvalidOperationException("plan 不得触发构建。") };

        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "前置：临时索引根必须一开始就不存在");
        var before = CliFixture.CaptureEntries(fixture.IndexRoot);
        Assert.IsEmpty(before);

        var run = fixture.RunWith(host, "plan", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual(string.Empty, run.StdErr, "成功的 plan 不得往 stderr 写东西");

        // ① 与磁盘实际一致（期望值在测试里从磁盘现算，不抄 CLI 的输出）
        var expectedFileCount = 2;
        var expectedCorpusBytes = new FileInfo(alphaPath).Length + new FileInfo(docPath).Length;
        Assert.AreEqual(expectedFileCount, run.IntValue("scope[0].fileCount"));
        Assert.AreEqual(expectedCorpusBytes, run.LongValue("scope[0].corpusBytes"));
        Assert.IsGreaterThan(expectedCorpusBytes, run.LongValue("scope[0].predictedIndexBytes"), "预测索引体积现实地大于语料体积");

        // ② 零写入：目录不存在 + 递归快照差 == 0 + 无租约目录
        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "plan 不得创建索引根目录");
        Assert.IsEmpty(CliFixture.CaptureEntries(fixture.IndexRoot), "索引根下新增条目必须为 0");
        Assert.IsFalse(
            Directory.Exists(Path.Combine(fixture.IndexRoot, ".supply-leases")),
            "plan 不得写租约");

        // ③ 不得触发构建
        Assert.IsNotNull(host.SuppliedBuilder, "plan 仍应装配构建端口（组合根完整性）");
        Assert.AreEqual(0, host.SuppliedBuilder.BuildCallCount, "plan 不得调用 builder");

        Assert.AreEqual("accepted", run.Value("result"));
        Assert.AreEqual("explicit", run.Value("index-root-source"));
        Assert.AreEqual(Path.GetFullPath(fixture.IndexRoot), run.Value("index-root"));
    }

    /// <summary>A2 前半：不重叠的两个 scope ⇒ 两份结果都 Accepted，退出码 0。</summary>
    [TestMethod]
    public void Plan_Accepts_Two_Non_Overlapping_Scopes()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");
        fixture.WriteTo(fixture.OtherCorpus, "b.cs", "class B { }");

        var run = fixture.Run(
            "plan",
            "--scope", fixture.Corpus,
            "--scope", fixture.OtherCorpus,
            "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual("true", run.Value("accepted"));
        Assert.AreEqual(2, run.IntValue("scope-count"));
        Assert.AreEqual(0, run.IntValue("rejection-count"));
        Assert.AreEqual(1, run.IntValue("scope[0].fileCount"));
        Assert.AreEqual(1, run.IntValue("scope[1].fileCount"));
        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "plan 零写入：两个 scope 也不得建索引根");
    }

    /// <summary>A2 后半：嵌套 scope ⇒ 拒绝项含正确原因，退出码 2。</summary>
    [TestMethod]
    public void Plan_Rejects_Nested_Scopes_With_The_Right_Reason_And_Exit_Code()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");
        var nested = fixture.CreateSubDirectory("nested");

        var run = fixture.Run(
            "plan",
            "--scope", fixture.Corpus,
            "--scope", nested,
            "--index-root", fixture.IndexRoot);

        Assert.AreEqual(2, run.ExitCode, "有拒绝项 ⇒ 退出码 2");
        Assert.AreEqual("false", run.Value("accepted"));
        Assert.AreEqual(1, run.IntValue("rejection-count"));
        Assert.AreEqual("Nested", run.Value("rejection[0].reason"));
        StringAssert.Contains(run.Value("rejection[0].message"), "嵌套");
        Assert.AreEqual(1, run.IntValue("scope-count"), "被接受的 scope 仍如实可见（不隐藏可用部分）");
        Assert.AreEqual("rejected", run.Value("result"));
        Assert.AreEqual(2, run.IntValue("exit-code"), "打印的退出码必须与实际返回一致");
    }

    [TestMethod]
    public void Plan_Rejects_A_Missing_Scope_Directory_With_Exit_Code_2()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("plan", "--scope", fixture.MissingDirectory, "--index-root", fixture.IndexRoot);

        Assert.AreEqual(2, run.ExitCode);
        Assert.AreEqual("NotFound", run.Value("rejection[0].reason"));
        Assert.AreEqual(0, run.IntValue("scope-count"));
    }

    /// <summary>预算超限只做报表（硬限属 A2）⇒ 不影响退出码。</summary>
    [TestMethod]
    public void Plan_Reports_A_Budget_Overrun_Without_Rejecting()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");

        var run = fixture.Run(
            "plan", "--scope", fixture.Corpus, "--budget-bytes", "1", "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual("1", run.Value("budget-bytes"));
        Assert.AreEqual("false", run.Value("scope[0].withinBudget"));
        Assert.IsGreaterThan(1L, run.LongValue("scope[0].predictedIndexBytes"));
        Assert.AreEqual("accepted", run.Value("result"));
    }

    [TestMethod]
    public void Plan_Json_Has_The_Expected_Shape()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");

        var run = fixture.Run("plan", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--json");

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        using var json = run.Json();
        var root = json.RootElement;

        Assert.AreEqual("plan", root.GetProperty("command").GetString());
        Assert.AreEqual(Path.GetFullPath(fixture.IndexRoot), root.GetProperty("indexRoot").GetString());
        Assert.IsTrue(root.GetProperty("indexRootExplicit").GetBoolean());
        Assert.IsTrue(root.GetProperty("accepted").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(string.IsNullOrWhiteSpace(root.GetProperty("owner").GetString()));

        var scope = root.GetProperty("scopes")[0];
        Assert.AreEqual(1, scope.GetProperty("fileCount").GetInt32());
        Assert.IsTrue(scope.GetProperty("withinBudget").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("rejections").GetArrayLength());
    }

    [TestMethod]
    public void Plan_Json_Marks_Rejections_And_Exit_Code_2()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("plan", "--scope", fixture.MissingDirectory, "--index-root", fixture.IndexRoot, "--json");

        Assert.AreEqual(2, run.ExitCode);
        using var json = run.Json();
        var root = json.RootElement;

        Assert.IsFalse(root.GetProperty("accepted").GetBoolean());
        Assert.AreEqual(2, root.GetProperty("exitCode").GetInt32());
        var rejection = root.GetProperty("rejections")[0];
        Assert.AreEqual("NotFound", rejection.GetProperty("reason").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(rejection.GetProperty("message").GetString()));
    }

    /// <summary>夹具自己的护栏（红线）：绝不允许指向真实数据根。</summary>
    [TestMethod]
    public void Fixture_Never_Points_At_The_Real_Index_Root()
    {
        using var fixture = new CliFixture();

        Assert.IsTrue(
            fixture.IndexRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "CLI 测试的索引根必须落在系统临时目录内");
        Assert.IsFalse(fixture.Root.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase));
    }
}
