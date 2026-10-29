using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="CodeOutlineService"/> 的单元测试 —— 补的是**特征测试覆盖不到的分支**。
/// <para>
/// 4 条 <c>code_outline</c> 特征测试走的都是「文件正常」这条路；但抽取时我把
/// <b>截断决策</b>（超过 <see cref="CodeOutlineService.MaxTopLevelTypes"/> 个顶层类型）
/// 和<b>严重语法错误判定</b>都搬进了服务 —— 这两条分支此前<b>零覆盖</b>，
/// 搬动等于在没人看守的地方改了行为。这里把它们钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeOutlineServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "outline-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结论。
        }
    }

    [TestMethod]
    public async Task MoreTopLevelTypesThanLimit_IsTruncated_ButTotalCountStaysReal()
    {
        const int total = CodeOutlineService.MaxTopLevelTypes + 5;
        var sb = new StringBuilder();
        for (var i = 0; i < total; i++)
        {
            sb.AppendLine($"public class C{i} {{ }}");
        }

        var path = Path.Combine(_root, "Many.cs");
        await File.WriteAllTextAsync(path, sb.ToString());

        var outcome = await new CodeOutlineService().OutlineAsync(path, "Many.cs", CancellationToken.None);

        Assert.IsTrue(outcome.Success, outcome.Error);

        // 节点被截断，但总数必须是**真实值** —— 上层靠它告诉调用方"还有更多"，不能一起截掉。
        Assert.AreEqual(CodeOutlineService.MaxTopLevelTypes, outcome.CSharpNodes.Count);
        Assert.AreEqual(total, outcome.TotalCount);
        Assert.IsTrue(outcome.Truncated);
    }

    [TestMethod]
    public async Task SyntaxErrorsPresent_ButEnoughNodes_ReturnsOutlineWithFlag()
    {
        // 有 Error 诊断、但可解析节点 >= 5 ⇒ 不应走「严重语法错误」分支，
        // 而应给出 outline 并置 HasSyntaxErrors=true。
        // 这条同时覆盖两个盲区：Count() < 5 的子条件、以及 HasSyntaxErrors 恒 false 的变异。
        var path = Path.Combine(_root, "PartiallyBroken.cs");
        await File.WriteAllTextAsync(path, """
            public class A { }
            public class B { void M() { int x = ; } }
            """);

        var outcome = await new CodeOutlineService().OutlineAsync(path, "PartiallyBroken.cs", CancellationToken.None);

        Assert.IsTrue(outcome.Success, outcome.Error);
        Assert.IsTrue(outcome.HasSyntaxErrors, "有语法错误却没标记，等于告诉调用方结果可信。");
    }

    [TestMethod]
    public async Task FewTopLevelTypes_IsNotTruncated()
    {
        var path = Path.Combine(_root, "Few.cs");
        await File.WriteAllTextAsync(path, "public class A { }\npublic class B { }");

        var outcome = await new CodeOutlineService().OutlineAsync(path, "Few.cs", CancellationToken.None);

        Assert.IsTrue(outcome.Success, outcome.Error);
        Assert.AreEqual(2, outcome.CSharpNodes.Count);
        Assert.AreEqual(2, outcome.TotalCount);
        Assert.IsFalse(outcome.Truncated);
    }

    [TestMethod]
    public async Task SeverelyBrokenSource_ReportsSyntaxErrorPreview_InsteadOfEmptyOutline()
    {
        // 有 Error 诊断、又几乎没有可解析节点 ⇒ 应当明确说"语法错误"，而不是给一个空 outline
        // （空 outline 会被读成"这文件里没有类型"，是另一种事实）。
        var path = Path.Combine(_root, "Broken.cs");
        await File.WriteAllTextAsync(path, "@@@@");

        var outcome = await new CodeOutlineService().OutlineAsync(path, "Broken.cs", CancellationToken.None);

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Error, "严重语法错误");
    }
}
