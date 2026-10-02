using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// 消费者输入指纹生产者门禁：指纹必须**稳定且有意义** —— 永远变会让每轮都重绑（白读盘），
/// 永远不变会漏掉真实变化。同时：没有工程文件时必须是**确定性**标记，不能用随机值。
/// </summary>
[TestClass]
public sealed class LanguageCodeSourceConsumerInputProviderTests : IDisposable
{
    private const string WorkspaceId = "ws-consumer";
    private const string ScopeId = "scope-consumer";

    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-consumer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestMethod]
    public async Task OneConsumerPerRegisteredLanguageWithStableSortedInputs()
    {
        var provider = new LanguageCodeSourceConsumerInputProvider(
            [new FakeIndexer("TypeScript/JavaScript"), new FakeIndexer("C#"), new FakeIndexer("Python")]);

        var inputs = await provider.GetConsumerInputsAsync(WorkspaceId, ScopeId);

        CollectionAssert.AreEqual(
            new[] { "C#", "Python", "TypeScript/JavaScript" },
            inputs.Select(input => input.ProviderId).ToArray(),
            "消费者顺序必须稳定（否则每轮的指纹字符串会抖）");
        Assert.IsTrue(inputs.All(input => !string.IsNullOrWhiteSpace(input.ParserPolicyFingerprint)));
        Assert.IsTrue(inputs.All(input => input.SemanticInputFingerprint == LanguageCodeSourceConsumerInputProvider.NoSemanticInputsMarker));
    }

    [TestMethod]
    public async Task SemanticInputFingerprintChangesOnlyWhenTheProjectFilesChange()
    {
        var projectFile = Path.Combine(_root, "Demo.csproj");
        await File.WriteAllTextAsync(projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var before = await LanguageCodeSourceConsumerInputProvider.ComputeSemanticFingerprintForRootAsync(_root);
        var again = await LanguageCodeSourceConsumerInputProvider.ComputeSemanticFingerprintForRootAsync(_root);
        Assert.AreEqual(before, again, "没变就必须稳定：否则每轮扫描都会把全部路径判成需重绑");

        await File.WriteAllTextAsync(projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><LangVersion>preview</LangVersion></PropertyGroup></Project>");
        var after = await LanguageCodeSourceConsumerInputProvider.ComputeSemanticFingerprintForRootAsync(_root);

        Assert.AreNotEqual(before, after, "工程文件变了就是有效的语义输入变化 ⇒ 只重新绑定");

        // 源码正文变化**不算**语义输入变化（否则每次改一个 .cs 都会把整仓判成需重绑）。
        await File.WriteAllTextAsync(Path.Combine(_root, "Service.cs"), "class Service { }");
        var withSource = await LanguageCodeSourceConsumerInputProvider.ComputeSemanticFingerprintForRootAsync(_root);
        Assert.AreEqual(after, withSource);
    }

    [TestMethod]
    public async Task MissingSemanticInputsAreDeterministic()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.AreEqual(
            LanguageCodeSourceConsumerInputProvider.NoSemanticInputsMarker,
            await LanguageCodeSourceConsumerInputProvider.ComputeSemanticFingerprintForRootAsync(empty));
        Assert.AreEqual(
            LanguageCodeSourceConsumerInputProvider.NoSemanticInputsMarker,
            await LanguageCodeSourceConsumerInputProvider.ComputeSemanticFingerprintForRootAsync(Path.Combine(_root, "missing")));
    }

    [TestMethod]
    public void SemanticInputFilesAreRecognised()
    {
        Assert.IsTrue(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile("Demo.csproj"));
        Assert.IsTrue(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile("App.sln"));
        Assert.IsTrue(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile("tsconfig.json"));
        Assert.IsTrue(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile("package.json"));
        Assert.IsTrue(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile("pyproject.toml"));
        Assert.IsTrue(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile("Directory.Build.props"));
        Assert.IsFalse(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile("Service.cs"));
        Assert.IsFalse(LanguageCodeSourceConsumerInputProvider.IsSemanticInputFile(null));
    }

    [TestMethod]
    public async Task TheProviderUsesTheScopeRootFromTheProjectRegistry()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var provider = new LanguageCodeSourceConsumerInputProvider(
            [new FakeIndexer("C#")],
            new FakeProjectRegistry(new CodeProjectRecord(WorkspaceId, ScopeId, _root, CodeProjectStatus.Active)));

        var inputs = await provider.GetConsumerInputsAsync(WorkspaceId, ScopeId);
        var expected = await LanguageCodeSourceConsumerInputProvider.ComputeSemanticFingerprintForRootAsync(_root);

        Assert.AreEqual(expected, inputs.Single().SemanticInputFingerprint);

        // 同一个 provider 第二次调用结果一致（无随机成分）。
        var second = await provider.GetConsumerInputsAsync(WorkspaceId, ScopeId);
        Assert.AreEqual(inputs.Single().SemanticInputFingerprint, second.Single().SemanticInputFingerprint);
    }

    [TestMethod]
    public async Task NoRegisteredLanguageMeansNoConsumers()
    {
        var provider = new LanguageCodeSourceConsumerInputProvider([]);

        Assert.IsEmpty(await provider.GetConsumerInputsAsync(WorkspaceId, ScopeId));
        Assert.AreEqual(0, provider.ProviderCount);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp 目录清理是 best-effort
        }
    }

    private sealed class FakeIndexer : ILanguageCodeIndexer
    {
        public FakeIndexer(string language) => Language = language;

        public string Language { get; }

        public IReadOnlyCollection<string> SupportedExtensions => [".fake"];

        public Task<CodeIndexResult> IndexWorkspaceAsync(
            CodeWorkspaceDescriptor workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));

        public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));
    }

    private sealed class FakeProjectRegistry : ICodeProjectRegistry
    {
        private readonly CodeProjectRecord _project;

        public FakeProjectRegistry(CodeProjectRecord project) => _project = project;

        public Task<CodeIndexResult> AddProjectAsync(
            CodeProjectAddRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));

        public Task<CodeIndexResult> RemoveProjectAsync(
            CodeProjectRemoveRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));

        public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
            string workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodeProjectRecord>>([_project]);

        public Task<CodeProjectRecord?> GetProjectAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CodeProjectRecord?>(_project);
    }
}
