using PuddingFullTextIndex.Cli;

namespace PuddingFullTextIndex.Cli.Tests;

/// <summary>
/// 用法错误族（规格 A3 的 <c>未知命令 ⇒ 5</c> + §3 R2 的"未知命令或非法参数 ⇒ 打印用法到 stderr 并返回 5"）。
/// 每个用例都断言：退出码 5、stderr 有可读原因、stdout 为空（用法错误不得产出任何"业务输出"）。
/// </summary>
[TestClass]
public sealed class SupplyCliParsingTests
{
    [TestMethod]
    public void NoArguments_Is_A_Usage_Error()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run();

        Assert.AreEqual(5, run.ExitCode);
        StringAssert.Contains(run.StdErr, "usage-error");
        StringAssert.Contains(run.StdErr, "缺失命令");
        Assert.AreEqual(string.Empty, run.StdOut, "用法错误不得产出业务输出");
    }

    [TestMethod]
    public void Unknown_Command_Is_A_Usage_Error()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("frobnicate", "--scope", fixture.Corpus);

        Assert.AreEqual(5, run.ExitCode);
        StringAssert.Contains(run.StdErr, "未知命令 'frobnicate'");
        StringAssert.Contains(run.StdErr, "plan|status|build|cancel");
        Assert.AreEqual(string.Empty, run.StdOut);
    }

    [TestMethod]
    public void Plan_Without_Scope_Is_A_Usage_Error()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("plan", "--index-root", fixture.IndexRoot);

        Assert.AreEqual(5, run.ExitCode);
        StringAssert.Contains(run.StdErr, "plan 至少需要一个 --scope");
        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "用法错误不得创建索引根");
    }

    [TestMethod]
    public void Plan_Rejects_Options_It_Does_Not_Support()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("plan", "--scope", fixture.Corpus, "--wait", "--index-root", fixture.IndexRoot);

        Assert.AreEqual(5, run.ExitCode);
        StringAssert.Contains(run.StdErr, "plan 不支持 --wait");
    }

    [TestMethod]
    public void Build_Without_Scope_Is_A_Usage_Error()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("build", "--index-root", fixture.IndexRoot);

        Assert.AreEqual(5, run.ExitCode);
        StringAssert.Contains(run.StdErr, "build 至少需要一个 --scope");
    }

    /// <summary>A6：<c>build</c> 缺 <c>--index-root</c> ⇒ 退出码 5 且<b>不执行任何构建</b>（替身计数 == 0）。</summary>
    [TestMethod]
    public void Build_Without_Index_Root_Is_A_Usage_Error_And_Never_Builds()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { }");

        // 先钉住解析层：解析必须失败（这一步在下面那次 CLI 调用之前跑 —— 断言失败即中断，
        // 因此绝不可能出现"解析没拒绝、于是 build 落到了引擎默认索引根"的危险路径）。
        var parsed = SupplyCommandLine.Parse(["build", "--scope", fixture.Corpus]);
        Assert.IsFalse(parsed.Succeeded, "缺失 --index-root 的 build 必须在解析期就被拒绝");
        StringAssert.Contains(parsed.Error, "--index-root");

        var host = new CliTestHost { BuildBehaviour = (_, _) => throw new InvalidOperationException("build 不得执行。") };

        var run = fixture.RunWith(host, "build", "--scope", fixture.Corpus);

        Assert.AreEqual(5, run.ExitCode);
        StringAssert.Contains(run.StdErr, "--index-root");
        Assert.AreEqual(0, host.BuilderFactoryCalls, "用法错误不得装配构建端口");
        Assert.AreEqual(0, host.InventoryFactoryCalls);
        Assert.AreEqual(0, host.LeaseFactoryCalls);
        Assert.AreEqual(0, host.EngineFactoryCalls);
        Assert.IsNull(host.SuppliedBuilder, "用法错误不得造出构建替身，更不得调用它");
    }

    [TestMethod]
    public void Budget_Must_Be_A_Positive_Integer()
    {
        using var fixture = new CliFixture();

        foreach (var bad in new[] { "abc", "0", "-5", "1.5" })
        {
            var run = fixture.Run("plan", "--scope", fixture.Corpus, "--budget-bytes", bad, "--index-root", fixture.IndexRoot);
            Assert.AreEqual(5, run.ExitCode, $"--budget-bytes '{bad}' 必须是用法错误");
            StringAssert.Contains(run.StdErr, "--budget-bytes");
        }

        Assert.IsFalse(Directory.Exists(fixture.IndexRoot));
    }

    [TestMethod]
    public void Options_Requiring_A_Value_Reject_A_Missing_Value()
    {
        using var fixture = new CliFixture();

        var missingScopeValue = fixture.Run("plan", "--scope");
        Assert.AreEqual(5, missingScopeValue.ExitCode);
        StringAssert.Contains(missingScopeValue.StdErr, "--scope 缺少数值");

        var optionAsValue = fixture.Run("plan", "--scope", "--json");
        Assert.AreEqual(5, optionAsValue.ExitCode);
        StringAssert.Contains(optionAsValue.StdErr, "--scope 缺少数值");
    }

    [TestMethod]
    public void Cancel_Requires_Job_And_Rejects_Other_Options()
    {
        using var fixture = new CliFixture();

        var noJob = fixture.Run("cancel");
        Assert.AreEqual(5, noJob.ExitCode);
        StringAssert.Contains(noJob.StdErr, "cancel 必须给出 --job");

        var withScope = fixture.Run("cancel", "--job", "job-1", "--scope", fixture.Corpus);
        Assert.AreEqual(5, withScope.ExitCode);
        StringAssert.Contains(withScope.StdErr, "cancel 不支持 --scope");
    }

    [TestMethod]
    public void Status_Rejects_Write_Oriented_Options()
    {
        using var fixture = new CliFixture();

        var wait = fixture.Run("status", "--scope", fixture.Corpus, "--wait");
        Assert.AreEqual(5, wait.ExitCode);
        StringAssert.Contains(wait.StdErr, "status 不支持 --wait");

        var budget = fixture.Run("status", "--scope", fixture.Corpus, "--budget-bytes", "1024");
        Assert.AreEqual(5, budget.ExitCode);
        StringAssert.Contains(budget.StdErr, "status 不支持 --budget-bytes");
    }

    [TestMethod]
    public void Unknown_Option_Is_A_Usage_Error()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("plan", "--scope", fixture.Corpus, "--nope");

        Assert.AreEqual(5, run.ExitCode);
        StringAssert.Contains(run.StdErr, "选项 '--nope' 不被支持");
    }

    [TestMethod]
    public void Usage_Error_Prints_The_Exit_Code_Contract_To_Stderr()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("status", "--wait");

        Assert.AreEqual(5, run.ExitCode);
        foreach (var code in new[] { "0  成功", "2  被拒", "3  本进程无法完成", "4  执行未成功", "5  用法错误" })
            StringAssert.Contains(run.StdErr, code);
    }
}
