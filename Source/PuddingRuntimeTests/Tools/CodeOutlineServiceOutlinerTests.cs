using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;
// 两个 OutlineNode 同名：这里要的是注册表侧的那个（C# 侧的是 PuddingRuntime.Services.Tools.OutlineNode）。
using OutlineNode = PuddingCodeIntelligence.Contracts.OutlineNode;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="CodeOutlineService"/> 的**多语言（注册表 outliner）分支**测试。
/// <para>
/// 前面两组测试走的都是 <c>.cs</c> 路径。outliner 分支在抽取时同样被搬动过，
/// 却一直没人看守 —— 而它有三条容易搞错的错误语义：
/// 引擎报失败、引擎返回零节点、以及"有注册表但文件类型不被支持"。
/// 三者都必须报错，<b>不能返回空 outline</b>（空 outline 会被读成"这里没有结构"）。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeOutlineServiceOutlinerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "outline-outliner-" + Guid.NewGuid().ToString("N"));
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
    public async Task RegistryBackedFile_ReturnsOutlinerOriginAndNodes()
    {
        var path = Write("doc.md", "# Title\n");
        var nodes = new List<OutlineNode>
        {
            new("Title", CodeSymbolKind.Unknown, 1, 1),
        };
        var service = new CodeOutlineService(new FakeRegistry(new FakeOutliner(new OutlineResult(true, path, nodes))));

        var outcome = await service.OutlineAsync(path, "doc.md", CancellationToken.None);

        Assert.IsTrue(outcome.Success, outcome.Error);
        Assert.AreEqual(CodeOutlineOrigin.RegisteredOutliner, outcome.Origin);
        Assert.AreEqual(1, outcome.OutlinerNodes.Count);
        Assert.AreEqual(1, outcome.TotalCount, "outliner 路径的 TotalCount 必须是真实节点数，不能顺手写 0。");
        Assert.AreEqual(0, outcome.CSharpNodes.Count, "outliner 分支不得往 C# 节点里塞东西。");
        Assert.IsFalse(outcome.Truncated, "outliner 分支不做截断 —— 截断是 C# 路径的历史行为。");
    }

    [TestMethod]
    public async Task OutlinerFailure_IsReportedNotSwallowed()
    {
        var path = Write("doc.md", "# Title\n");
        var result = new OutlineResult(false, path, [], "parser exploded");
        var service = new CodeOutlineService(new FakeRegistry(new FakeOutliner(result)));

        var outcome = await service.OutlineAsync(path, "MY-DISPLAY-NAME.md", CancellationToken.None);

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Error, "parser exploded");
        StringAssert.Contains(outcome.Error, "MY-DISPLAY-NAME.md", "错误信息要回显用户给的路径，不是绝对路径。");
        Assert.DoesNotContain(_root, outcome.Error, "错误信息不得泄露绝对路径。");
    }

    [TestMethod]
    public async Task OutlinerReturnsZeroNodes_IsReportedNotTreatedAsEmptyOutline()
    {
        var path = Write("doc.md", "   \n");
        var result = new OutlineResult(true, path, []);
        var service = new CodeOutlineService(new FakeRegistry(new FakeOutliner(result)));

        var outcome = await service.OutlineAsync(path, "doc.md", CancellationToken.None);

        Assert.IsFalse(outcome.Success, "零节点是「没解析出东西」，不是「这个文件没有结构」——必须报错。");
        StringAssert.Contains(outcome.Error, "没有发现结构声明");
    }

    [TestMethod]
    public async Task RegistryPresentButUnsupported_ReportsUnsupportedType()
    {
        // 注册表存在、但对该文件类型说不支持 —— 必须走到 Fallback，
        // 而不是“有注册表就一定能 outline”。旧夹具 IsSupported 恒 true，这条分支零覆盖。
        var path = Write("doc.zzz", "whatever");
        var service = new CodeOutlineService(
            new FakeRegistry(new FakeOutliner(new OutlineResult(true, path, [])), isSupported: false));

        var outcome = await service.OutlineAsync(path, "doc.zzz", CancellationToken.None);

        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Error, "不支持此文件类型");
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeRegistry(IFileOutliner outliner, bool isSupported = true) : IFileOutlinerRegistry
    {
        public IFileOutliner? GetOutliner(string filePath) => outliner;

        public bool IsSupported(string filePath) => isSupported;
    }

    private sealed class FakeOutliner(OutlineResult result) : IFileOutliner
    {
        public IReadOnlyList<string> SupportedExtensions => [".md"];

        public Task<OutlineResult> OutlineAsync(
            string filePath,
            string sourceCode,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
