using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;
using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.Python;

namespace PuddingCodeIntelligenceTests.Python;

/// <summary>
/// D4：Python 侧**批量更新**门禁（与 TypeScript 侧同一形状）。
/// <para>
/// 用真实的 Python 子进程 + 可控桩提取器断言：① 一个批次只跑**一次**项目级提取
/// （桩把每次 <c>--project</c> 调用写进标记文件）；② 结果以 payload 返回、**不写索引**；
/// ③ <c>SessionKey</c> 说实话（退化逐文件时为空）。
/// </para>
/// </summary>
[TestClass]
public sealed class PythonIndexerBatchTests : IDisposable
{
    private const string WorkspaceId = "ws-py-batch";
    private const string ProjectId = "scope-py-batch";

    private string _root = null!;
    private SqliteCodeIndexStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-py-batch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "code-index.db"));
    }

    private static CodeIndexBatchContext Context() => new("config-py", "policy-py", Generation: 5);

    private PythonIndexer IndexerWithStub(string componentBaseDirectory) =>
        new(_store, NullLogger<PythonIndexer>.Instance, new ExtractorAssetResolver(componentBaseDirectory));

    /// <summary>
    /// 写一个项目模式可用的桩提取器：项目模式按目录里的每个 <c>.py</c> 文件产出一个同名符号，
    /// 每次 <c>--project</c> 调用追加一行标记；逐文件模式同样产出符号。
    /// </summary>
    private static void WriteStubExtractor(string componentBaseDirectory, string projectMarkerPath)
    {
        var scriptsDirectory = Path.Combine(componentBaseDirectory, "Scripts");
        Directory.CreateDirectory(scriptsDirectory);

        var script = $$"""
            import json, os, sys

            MARKER = {{JsonSerializer.Serialize(projectMarkerPath)}}
            argv = sys.argv[1:]

            def append(line):
                with open(MARKER, 'a', encoding='utf-8') as handle:
                    handle.write(line + '\n')

            if argv and argv[0] == '--project':
                project_dir = argv[1]
                append('project')
                files = sorted(name for name in os.listdir(project_dir) if name.endswith('.py'))
                payload = {
                    'files': [
                        {
                            'file': name,
                            'symbols': [
                                {
                                    'kind': 'class',
                                    'name': os.path.splitext(name)[0],
                                    'line': 1,
                                    'signature': 'class ' + os.path.splitext(name)[0],
                                    'containerName': None,
                                }
                            ],
                        }
                        for name in files
                    ],
                    'crossReferences': [],
                }
                sys.stdout.write(json.dumps(payload) + '\n')
                sys.exit(0)

            file_path = argv[0]
            append('file:' + os.path.basename(file_path))
            payload = {
                'symbols': [
                    {
                        'name': os.path.splitext(os.path.basename(file_path))[0],
                        'kind': 'class',
                        'line': 1,
                        'signature': 'class ' + os.path.splitext(os.path.basename(file_path))[0],
                    }
                ],
                'relations': [],
            }
            sys.stdout.write(json.dumps(payload) + '\n')
            """;

        File.WriteAllText(Path.Combine(scriptsDirectory, "extract-py-symbols.py"), script);
    }

    private (PythonIndexer Indexer, string ProjectPath, string MarkerPath) Arrange(params string[] projectFiles)
    {
        var componentBaseDirectory = Path.Combine(_root, "component");
        var markerPath = Path.Combine(_root, "extractor-runs.txt");
        WriteStubExtractor(componentBaseDirectory, markerPath);

        var projectPath = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectPath);
        foreach (var name in projectFiles)
            File.WriteAllText(Path.Combine(projectPath, name), $"class {Path.GetFileNameWithoutExtension(name)}:\n    pass\n");

        return (IndexerWithStub(componentBaseDirectory), projectPath, markerPath);
    }

    private CodeWorkspaceDescriptor Descriptor(string projectPath) =>
        new(WorkspaceId, ProjectId, projectPath, ProjectFilePaths: []);

    private static int ProjectRuns(string markerPath) =>
        File.Exists(markerPath)
            ? File.ReadAllLines(markerPath).Count(line => line == "project")
            : 0;

    [TestMethod]
    public async Task OneProjectExtractionCoversTheWholeBatchAndNothingIsWritten()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.py", "b.py");

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath),
            [Path.Combine(projectPath, "a.py"), Path.Combine(projectPath, "b.py")],
            Context());

        Assert.AreEqual(1, ProjectRuns(markerPath), "两个文件必须共享同一个项目级提取进程");
        Assert.HasCount(2, result.Outcomes);
        Assert.IsTrue(result.Outcomes.All(outcome => outcome.Status == CodeIndexConsumerStatus.Applied));
        Assert.IsNotNull(result.SessionKey, "真的用了项目模式时 SessionKey 必须有值");
        Assert.AreEqual("config-py", result.ConfigurationFingerprint);

        foreach (var outcome in result.Outcomes)
        {
            Assert.IsNotNull(outcome.Payload);
            Assert.IsTrue(
                outcome.Payload!.Symbols.Any(symbol => symbol.Kind == CodeSymbolKind.Class),
                $"{outcome.FilePath} 应当提取到类符号");
        }

        Assert.IsEmpty(await _store.ListFilesAsync(WorkspaceId, ProjectId), "批量接缝不得写索引");
        Assert.IsEmpty(
            await _store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, Path.Combine(projectPath, "a.py")));
    }

    [TestMethod]
    public async Task PathsOutsideTheLanguageAreNotApplicableAndDoNotRunTheExtractor()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.py");
        var document = Path.Combine(projectPath, "notes.md");
        File.WriteAllText(document, "# notes");

        var result = await indexer.UpdateFilesAsync(Descriptor(projectPath), [document], Context());

        Assert.AreEqual(CodeIndexConsumerStatus.NotApplicable, result.Outcomes.Single().Status);
        Assert.IsNull(result.SessionKey);
        Assert.AreEqual(0, ProjectRuns(markerPath), "没有适用路径时一次提取器都不该跑");
    }

    [TestMethod]
    public async Task MissingProjectRootIsRetryableWithoutRunningTheExtractor()
    {
        var componentBaseDirectory = Path.Combine(_root, "component");
        var markerPath = Path.Combine(_root, "extractor-runs.txt");
        WriteStubExtractor(componentBaseDirectory, markerPath);

        var missingProject = Path.Combine(_root, "missing");
        var indexer = IndexerWithStub(componentBaseDirectory);

        var result = await indexer.UpdateFilesAsync(
            Descriptor(missingProject),
            [Path.Combine(missingProject, "gone.py")],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, outcome.Status, "工程根缺失是按路径退避，不是整仓重建");
        StringAssert.Contains(outcome.Reason!, "does not exist");
        Assert.AreEqual(0, ProjectRuns(markerPath));
    }

    [TestMethod]
    public async Task MissingExtractorAssetsIsRetryableNotAFailure()
    {
        var emptyComponentDirectory = Path.Combine(_root, "empty-component");
        Directory.CreateDirectory(emptyComponentDirectory);

        var projectPath = Path.Combine(_root, "project-missing-assets");
        Directory.CreateDirectory(projectPath);

        var result = await IndexerWithStub(emptyComponentDirectory).UpdateFilesAsync(
            Descriptor(projectPath),
            [Path.Combine(projectPath, "a.py")],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, outcome.Status);
        StringAssert.Contains(outcome.Reason!, "extract-py-symbols.py");
    }

    [TestMethod]
    public async Task PathsNotCoveredByProjectModeFallBackPerFileAndClearTheSessionKey()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.py");
        var ghost = Path.Combine(projectPath, "ghost.py"); // 项目模式看不到它

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath),
            [Path.Combine(projectPath, "a.py"), ghost],
            Context());

        Assert.AreEqual(1, ProjectRuns(markerPath));
        Assert.IsTrue(
            File.ReadAllLines(markerPath).Any(line => line == "file:ghost.py"),
            "项目模式没覆盖到的路径必须逐文件提取，不能静默丢掉");

        var byPath = result.Outcomes.ToDictionary(outcome => outcome.FilePath, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[Path.Combine(projectPath, "a.py")].Status);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[ghost].Status);
        Assert.IsNull(
            result.SessionKey,
            "一旦退化到逐文件提取，SessionKey 必须为空：它要如实反映「这一批没有复用同一个快照」");
    }

    [TestMethod]
    public async Task DuplicateAndBlankPathsAreCollapsedIntoOneRun()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.py");
        var file = Path.Combine(projectPath, "a.py");

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath),
            [file, file.ToUpperInvariant(), "   "],
            Context());

        Assert.HasCount(1, result.Outcomes);
        Assert.AreEqual(1, ProjectRuns(markerPath));
    }

    [TestMethod]
    public async Task EmptyBatchRunsNothing()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.py");

        var result = await indexer.UpdateFilesAsync(Descriptor(projectPath), [], Context());

        Assert.IsEmpty(result.Outcomes);
        Assert.IsNull(result.SessionKey);
        Assert.AreEqual(0, ProjectRuns(markerPath));
    }

    [TestMethod]
    public async Task FullRunProjectModeStillClearsStaleSymbolsAndWritesAfterTheReadOnlyRefactor()
    {
        // 本轮的只读投影重构把「清旧符号」从投影移到调用方：这条用例直接锁定全量路径仍等价 ——
        // 旧符号被清掉、新符号与文件记录被写入（Python 侧此前没有全量路径的用例）。
        var (indexer, projectPath, markerPath) = Arrange("a.py");
        var file = Path.Combine(projectPath, "a.py");

        await _store.UpsertSymbolsAsync(
            WorkspaceId,
            ProjectId,
            [new CodeSymbolRecord(WorkspaceId, ProjectId, file, "PY:stale", "Stale", CodeSymbolKind.Class, 1, 1, "class Stale", null)],
            CancellationToken.None);

        var result = await indexer.IndexWorkspaceAsync(Descriptor(projectPath), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(1, ProjectRuns(markerPath), "全量路径应当走一次项目级提取");
        Assert.IsNull(
            await _store.GetSymbolAsync(WorkspaceId, ProjectId, "PY:stale"),
            "旧符号必须被清掉（清理移到调用方后语义不变）");

        var symbols = await _store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, file);
        Assert.IsTrue(
            symbols.Any(symbol => string.Equals(symbol.Name, "a", StringComparison.OrdinalIgnoreCase)),
            "新符号必须被写入");
        Assert.HasCount(1, await _store.ListFilesAsync(WorkspaceId, ProjectId));
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
