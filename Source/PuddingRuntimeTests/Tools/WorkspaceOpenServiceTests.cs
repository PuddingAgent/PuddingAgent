using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;
using RegistryOutlineNode = PuddingCodeIntelligence.Contracts.OutlineNode;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="WorkspaceOpenService"/> v1 的行为测试。
/// <para>
/// 重点不是"能读文件"，而是三条**策略**：路径必须落在工作区子树内、MaxBytes 是总字节、
/// outline 失败要原样透出而不是返回空树。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceOpenServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ws-open-" + Guid.NewGuid().ToString("N"));
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
    public async Task PathOutsideWorkspace_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".cs");
        await File.WriteAllTextAsync(outside, "public class X { }");

        try
        {
            var service = NewService(outline: null);

            var result = await service.OpenAsync(Request(outside, WorkspaceOpenView.Excerpt), CancellationToken.None);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, "不在工作区");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    public async Task Excerpt_ReturnsRequestedSliceAndRealTotalLineCount()
    {
        var path = Path.Combine(_root, "a.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 10).Select(i => $"line{i}"));

        var result = await NewService(null)
            .OpenAsync(Request("a.txt", WorkspaceOpenView.Excerpt, startLine: 3, lineCount: 2), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        var payload = (ExcerptPayload)result.Payload!;
        CollectionAssert.AreEqual(new[] { "line3", "line4" }, payload.Lines.ToArray());
        Assert.AreEqual(3, payload.StartLine);
        Assert.AreEqual(10, payload.TotalLineCount, "总行数必须是文件真实行数，不能等于切片长度。");
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task Excerpt_RespectsMaxBytesAsTotalBudget()
    {
        var path = Path.Combine(_root, "big.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 50).Select(i => new string('x', 40)));

        var result = await NewService(null)
            .OpenAsync(Request("big.txt", WorkspaceOpenView.Excerpt, startLine: 1, lineCount: 50, maxBytes: 200), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        var payload = (ExcerptPayload)result.Payload!;
        Assert.IsTrue(payload.Lines.Count is > 0 and < 50, "总字节上限必须真的截住，不能整段放行。");
        Assert.IsTrue(result.Truncated);
        StringAssert.Contains(result.TruncatedReason, "MaxBytes");
    }

    [TestMethod]
    public async Task Outline_DelegatesAndMapsRegistryNodes()
    {
        var path = Path.Combine(_root, "doc.md");
        await File.WriteAllTextAsync(path, "# Title");

        var outcome = CodeOutlineOutcome.FromOutliner([new RegistryOutlineNode("Title", CodeSymbolKind.Unknown, 1, 1)]);
        var service = NewService(new FakeOutlineService(outcome));

        var result = await service.OpenAsync(Request("doc.md", WorkspaceOpenView.Outline), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        var payload = (OutlinePayload)result.Payload!;
        Assert.AreEqual(1, payload.Nodes.Count);
        Assert.AreEqual("Title", payload.Nodes[0].Name);
    }

    [TestMethod]
    public async Task Outline_FailureIsPropagatedNotSwallowed()
    {
        var path = Path.Combine(_root, "doc.md");
        await File.WriteAllTextAsync(path, "# Title");

        var service = NewService(new FakeOutlineService(CodeOutlineOutcome.Failed("(文件中没有发现结构声明: doc.md)")));

        var result = await service.OpenAsync(Request("doc.md", WorkspaceOpenView.Outline), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "没有发现结构声明");
        Assert.IsNull(result.Payload, "失败不得带负载。");
    }

    [TestMethod]
    public async Task MissingPathArgument_IsRejected()
    {
        var result = await NewService(null)
            .OpenAsync(Request(path: null, WorkspaceOpenView.Excerpt), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "path is required");
    }

    [TestMethod]
    public async Task PathEqualToWorkspaceRoot_IsAllowed()
    {
        // 回归守卫：路径等于工作区根本身时必须放行。
        // 早期版本只比前缀（root + 分隔符），"." 解析成根后被误判为「不在工作区内」。
        var service = new WorkspaceOpenService(null, new FixedScopeResolver(_root), new CodeProjectMapService());

        var result = await service.OpenAsync(
            new WorkspaceOpenRequest("ws-1", null, ".", WorkspaceOpenView.Map, Depth: 1),
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
    }

    [TestMethod]
    public async Task Map_ReturnsDirectoryTreeFromService()
    {
        var dir = Path.Combine(_root, "src");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "A.cs"), "public class A { }");

        var service = new WorkspaceOpenService(null, new FixedScopeResolver(_root), new CodeProjectMapService());

        var result = await service.OpenAsync(
            new WorkspaceOpenRequest("ws-1", null, "src", WorkspaceOpenView.Map, Depth: 2),
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        var payload = (MapPayload)result.Payload!;
        Assert.IsTrue(payload.TotalFileCount >= 1, "目录里有一个 .cs，总文件数不能是 0。");
    }

    [TestMethod]
    public async Task Map_WithoutService_IsExplicitNotRegistered()
    {
        var result = await NewService(null)
            .OpenAsync(new WorkspaceOpenRequest("ws-1", null, ".", WorkspaceOpenView.Map), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "ICodeProjectMapService is not registered");
    }

    private WorkspaceOpenService NewService(ICodeOutlineService? outline) =>
        new(outline, new FixedScopeResolver(_root));

    private static WorkspaceOpenRequest Request(
        string? path,
        WorkspaceOpenView view,
        int? startLine = null,
        int? lineCount = null,
        int maxBytes = 64 * 1024) =>
        new("ws-1", null, path, view, startLine, lineCount, null, maxBytes);

    private sealed class FakeOutlineService(CodeOutlineOutcome outcome) : ICodeOutlineService
    {
        public Task<CodeOutlineOutcome> OutlineAsync(string fullPath, string displayPath, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }

    private sealed class FixedScopeResolver(string root) : ICodeIndexScopeResolver
    {
        public Task<ScopeResolution?> ResolveAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScopeResolution?>(Build(workspaceId));

        public Task<ScopeResolution> ResolveAndEnsureAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Build(workspaceId));

        private ScopeResolution Build(string workspaceId) => new(
            new CodeIndexScope(workspaceId, "proj-1", root, ScopeState.Active, ScopeSource.Auto),
            IsAutoDetected: true,
            IsNewlyCreated: false);
    }
}
