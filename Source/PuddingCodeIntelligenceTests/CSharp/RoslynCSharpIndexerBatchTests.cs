using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;
using PuddingCodeIntelligence.CSharp;

namespace PuddingCodeIntelligenceTests.CSharp;

/// <summary>
/// D4：Roslyn 侧**批量更新**门禁。
/// <para>
/// 核心断言不是「提取出来了」，而是三件事：
/// ① <b>一个批次只打开一次工程</b>（逐文件打开工程就是被诊断出来的放大）；
/// ② 提取结果以 payload 形式交给调用方，**索引一个字都不写**（原子提交由调用方负责）；
/// ③ 路由/失败语义不混用：不属于工作区的文件是 <c>NotApplicable</c>，
///    工程根缺失或工作区打不开是 <c>Retryable</c>（带原因，按退避重试，不升级整仓）。
/// </para>
/// </summary>
[TestClass]
public sealed class RoslynCSharpIndexerBatchTests : IDisposable
{
    private const string WorkspaceId = "ws-batch-cs";
    private const string ProjectId = "scope-batch-cs";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pudding-roslyn-batch-tests", Guid.NewGuid().ToString("N"));

    private SqliteCodeIndexStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "code-index.db"));
    }

    private static CodeIndexBatchContext Context() => new("config-1", "policy-1", Generation: 3);

    private string ProjectDirectory()
    {
        var directory = Path.Combine(_root, "proj");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private CodeWorkspaceDescriptor Descriptor(string projectPath) =>
        new(WorkspaceId, ProjectId, projectPath, ProjectFilePaths: [Path.Combine(projectPath, "Test.csproj")]);

    /// <summary>在内存里装配一个包含给定文件的 C# 工程（不触碰 MSBuild）。</summary>
    private static AdhocWorkspace CreateWorkspace(params (string FilePath, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .ToArray<MetadataReference>();

        var project = workspace.AddProject(
            ProjectInfo.Create(projectId, VersionStamp.Create(), "Test", "Test", LanguageNames.CSharp)
                .WithMetadataReferences(references));

        foreach (var (filePath, source) in files)
        {
            workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id),
                Path.GetFileName(filePath),
                filePath: filePath,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create()))));
        }

        return workspace;
    }

    private static string Source(string className) => $$"""
        namespace BatchTests
        {
            public class {{className}}
            {
                public int Value() => 42;

                public int Doubled() => Value() * 2;
            }
        }
        """;

    [TestMethod]
    public async Task OneWorkspaceIsOpenedForTheWholeBatchAndEveryFileGetsAPayload()
    {
        var projectPath = ProjectDirectory();
        var first = Path.Combine(projectPath, "First.cs");
        var second = Path.Combine(projectPath, "Second.cs");
        var workspace = CreateWorkspace((first, Source("First")), (second, Source("Second")));

        var opens = 0;
        var indexer = new RoslynCSharpIndexer(_store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) =>
        {
            opens++;
            return Task.FromResult<Workspace>(workspace);
        });

        var result = await indexer.UpdateFilesAsync(Descriptor(projectPath), [first, second], Context());

        Assert.AreEqual(1, opens, "两个文件必须共享同一个工程/编译快照（逐文件打开工程就是放大本身）");
        Assert.HasCount(2, result.Outcomes);
        Assert.IsTrue(result.Outcomes.All(outcome => outcome.Status == CodeIndexConsumerStatus.Applied));
        Assert.IsNotNull(result.SessionKey, "批内复用的快照标识必须可观测");
        Assert.AreEqual("config-1", result.ConfigurationFingerprint);

        foreach (var outcome in result.Outcomes)
        {
            Assert.IsNotNull(outcome.Payload);
            Assert.AreEqual(outcome.FilePath, outcome.Payload!.FilePath);
            Assert.IsTrue(
                outcome.Payload.Symbols.Any(symbol => symbol.Kind == CodeSymbolKind.Class),
                $"{outcome.FilePath} 应当提取到类符号");
        }
    }

    [TestMethod]
    public async Task BatchNeverWritesToTheIndex()
    {
        var projectPath = ProjectDirectory();
        var file = Path.Combine(projectPath, "Only.cs");
        var workspace = CreateWorkspace((file, Source("Only")));

        var indexer = new RoslynCSharpIndexer(
            _store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) => Task.FromResult<Workspace>(workspace));

        var result = await indexer.UpdateFilesAsync(Descriptor(projectPath), [file], Context());

        Assert.AreEqual(CodeIndexConsumerStatus.Applied, result.Outcomes.Single().Status);
        Assert.IsEmpty(
            await _store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, file),
            "提取结果由调用方经 ReplaceFilesAsync 原子提交：这里不得写索引");
        Assert.IsEmpty(await _store.ListFilesAsync(WorkspaceId, ProjectId));
    }

    [TestMethod]
    public async Task NonCSharpAndUnknownFilesAreNotApplicable()
    {
        var projectPath = ProjectDirectory();
        var known = Path.Combine(projectPath, "Known.cs");
        var unknown = Path.Combine(projectPath, "NotInWorkspace.cs");
        var document = Path.Combine(projectPath, "notes.md");
        var workspace = CreateWorkspace((known, Source("Known")));

        var indexer = new RoslynCSharpIndexer(
            _store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) => Task.FromResult<Workspace>(workspace));

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath), [known, unknown, document], Context());

        var byPath = result.Outcomes.ToDictionary(outcome => outcome.FilePath, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[known].Status);
        Assert.AreEqual(
            CodeIndexConsumerStatus.NotApplicable,
            byPath[unknown].Status,
            "不在已加载工作区里的 .cs 是能力路由结果，不是失败");
        Assert.AreEqual(CodeIndexConsumerStatus.NotApplicable, byPath[document].Status);
        Assert.IsNull(byPath[document].Payload);
    }

    [TestMethod]
    public async Task MissingProjectRootIsRetryableAndNeverOpensTheWorkspace()
    {
        var projectPath = Path.Combine(_root, "missing");
        var file = Path.Combine(projectPath, "Gone.cs");

        var opens = 0;
        var indexer = new RoslynCSharpIndexer(_store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) =>
        {
            opens++;
            throw new InvalidOperationException("must not be called");
        });

        var result = await indexer.UpdateFilesAsync(Descriptor(projectPath), [file], Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, outcome.Status, "工程根缺失是按路径退避，不是整仓重建");
        StringAssert.Contains(outcome.Reason!, "does not exist");
        Assert.AreEqual(0, opens, "根不可用就不该尝试打开工程");
    }

    [TestMethod]
    public async Task AWorkspaceThatCannotBeOpenedMarksEveryCSharpPathRetryable()
    {
        var projectPath = ProjectDirectory();
        var first = Path.Combine(projectPath, "A.cs");
        var second = Path.Combine(projectPath, "B.cs");
        var document = Path.Combine(projectPath, "readme.md");

        var indexer = new RoslynCSharpIndexer(_store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) =>
            throw new InvalidOperationException("MSBuild unavailable"));

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath), [first, second, document], Context());

        var byPath = result.Outcomes.ToDictionary(outcome => outcome.FilePath, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, byPath[first].Status);
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, byPath[second].Status);
        StringAssert.Contains(byPath[first].Reason!, "MSBuild unavailable");
        Assert.AreEqual(
            CodeIndexConsumerStatus.NotApplicable,
            byPath[document].Status,
            "路由结果不受工作区打不开的影响");
    }

    [TestMethod]
    public async Task DuplicateAndBlankPathsAreCollapsed()
    {
        var projectPath = ProjectDirectory();
        var file = Path.Combine(projectPath, "Once.cs");
        var workspace = CreateWorkspace((file, Source("Once")));

        var opens = 0;
        var indexer = new RoslynCSharpIndexer(_store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) =>
        {
            opens++;
            return Task.FromResult<Workspace>(workspace);
        });

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath), [file, file.ToUpperInvariant(), "   "], Context());

        Assert.HasCount(1, result.Outcomes);
        Assert.AreEqual(1, opens);
    }

    [TestMethod]
    public async Task EmptyBatchDoesNotOpenAnyWorkspace()
    {
        var opens = 0;
        var indexer = new RoslynCSharpIndexer(_store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) =>
        {
            opens++;
            return Task.FromResult<Workspace>(new AdhocWorkspace());
        });

        var result = await indexer.UpdateFilesAsync(Descriptor(ProjectDirectory()), [], Context());

        Assert.IsEmpty(result.Outcomes);
        Assert.AreEqual(0, opens);
    }

    [TestMethod]
    public async Task ExtractedPayloadCarriesRelationsAndReferences()
    {
        var projectPath = ProjectDirectory();
        var file = Path.Combine(projectPath, "Calls.cs");
        var workspace = CreateWorkspace((file, Source("Calls")));

        var indexer = new RoslynCSharpIndexer(
            _store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) => Task.FromResult<Workspace>(workspace));

        var result = await indexer.UpdateFilesAsync(Descriptor(projectPath), [file], Context());

        var payload = result.Outcomes.Single().Payload!;
        Assert.IsTrue(
            payload.Relations.Any(relation => relation.Kind == CodeRelationKind.Calls),
            "Doubled() 调用 Value()：关系必须在 payload 里，而不是直接写库");
        Assert.IsTrue(
            payload.References.Count > 0,
            "引用也要随 payload 交给调用方");
        Assert.IsTrue(payload.Symbols.All(symbol => symbol.FilePath == file));
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
}
