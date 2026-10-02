using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;
using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.TypeScript;

namespace PuddingCodeIntelligenceTests.TypeScript;

/// <summary>
/// D4：TypeScript 侧**批量更新**门禁。
/// <para>
/// 用真实的 Node 子进程 + 一个可控的桩提取器来断言三件事：
/// ① 一个批次只跑**一次项目级提取**（桩把每次 <c>--project</c> 调用写进标记文件，计数必须是 1）；
/// ② 结果以 payload 返回、**索引一个字都不写**；
/// ③ <c>SessionKey</c> 说实话：真的用了项目模式才有值，退化成逐文件提取时为空。
/// </para>
/// </summary>
[TestClass]
public sealed class TypeScriptIndexerBatchTests : IDisposable
{
    private const string WorkspaceId = "ws-ts-batch";
    private const string ProjectId = "scope-ts-batch";

    private string _root = null!;
    private SqliteCodeIndexStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-ts-batch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "code-index.db"));
    }

    private static CodeIndexBatchContext Context() => new("config-ts", "policy-ts", Generation: 2);

    private TypeScriptIndexer IndexerWithStub(string componentBaseDirectory) =>
        new(_store, NullLogger<TypeScriptIndexer>.Instance, new ExtractorAssetResolver(componentBaseDirectory));

    /// <summary>
    /// 写一个项目模式可用的桩提取器：项目模式按目录里的每个 <c>.ts</c> 文件产出一个同名符号，
    /// 并在每一行写一条标记（用来数「一批跑了几次项目级提取」）。逐文件模式同样产出符号。
    /// </summary>
    private static void WriteStubExtractor(string componentBaseDirectory, string projectMarkerPath)
    {
        var scriptsDirectory = Path.Combine(componentBaseDirectory, "Scripts");
        Directory.CreateDirectory(Path.Combine(scriptsDirectory, "node_modules"));

        var script = $$"""
            'use strict';
            const fs = require('fs');
            const path = require('path');
            const marker = {{JsonSerializer.Serialize(projectMarkerPath)}};
            const argv = process.argv.slice(2);

            if (argv[0] === '--project') {
              const projectDir = argv[1];
              fs.appendFileSync(marker, 'project\n');
              const files = fs.readdirSync(projectDir)
                .filter((name) => name.endsWith('.ts'))
                .sort();
              process.stdout.write(JSON.stringify({
                files: files.map((name) => ({
                  file: name,
                  symbols: [{
                    kind: 'class',
                    name: path.basename(name, '.ts'),
                    fullName: path.basename(name, '.ts'),
                    line: 1,
                    signature: 'class ' + path.basename(name, '.ts'),
                    containerName: null
                  }]
                })),
                crossReferences: []
              }) + '\n');
              process.exit(0);
            }

            const filePath = argv[0];
            fs.appendFileSync(marker, 'file:' + path.basename(filePath) + '\n');
            process.stdout.write(JSON.stringify({
              symbols: [{
                name: path.basename(filePath, '.ts'),
                kind: 'class',
                startLine: 1,
                endLine: 1,
                signature: 'class ' + path.basename(filePath, '.ts')
              }],
              relations: []
            }) + '\n');
            """;

        File.WriteAllText(Path.Combine(scriptsDirectory, "extract-ts-symbols.js"), script);
    }

    private (TypeScriptIndexer Indexer, string ProjectPath, string MarkerPath) Arrange(params string[] projectFiles)
    {
        var componentBaseDirectory = Path.Combine(_root, "component");
        var markerPath = Path.Combine(_root, "extractor-runs.txt");
        WriteStubExtractor(componentBaseDirectory, markerPath);

        var projectPath = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectPath);
        foreach (var name in projectFiles)
            File.WriteAllText(Path.Combine(projectPath, name), $"export class {Path.GetFileNameWithoutExtension(name)} {{}}");

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
        var (indexer, projectPath, markerPath) = Arrange("a.ts", "b.ts");

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath),
            [Path.Combine(projectPath, "a.ts"), Path.Combine(projectPath, "b.ts")],
            Context());

        Assert.AreEqual(1, ProjectRuns(markerPath), "两个文件必须共享同一个项目级提取进程");
        Assert.HasCount(2, result.Outcomes);
        Assert.IsTrue(result.Outcomes.All(outcome => outcome.Status == CodeIndexConsumerStatus.Applied));
        Assert.IsNotNull(result.SessionKey, "真的用了项目模式时 SessionKey 必须有值");
        Assert.AreEqual("config-ts", result.ConfigurationFingerprint);

        foreach (var outcome in result.Outcomes)
        {
            Assert.IsNotNull(outcome.Payload);
            Assert.IsTrue(
                outcome.Payload!.Symbols.Any(symbol => symbol.Kind == CodeSymbolKind.Class),
                $"{outcome.FilePath} 应当提取到类符号");
        }

        Assert.IsEmpty(await _store.ListFilesAsync(WorkspaceId, ProjectId), "批量接缝不得写索引");
        Assert.IsEmpty(
            await _store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, Path.Combine(projectPath, "a.ts")));
    }

    [TestMethod]
    public async Task PathsOutsideTheLanguageAreNotApplicableAndDoNotRunTheExtractor()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.ts");
        var document = Path.Combine(projectPath, "notes.md");
        File.WriteAllText(document, "# notes");

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath),
            [document],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.NotApplicable, outcome.Status);
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
            [Path.Combine(missingProject, "gone.ts")],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, outcome.Status, "工程根缺失是按路径退避，不是整仓重建");
        StringAssert.Contains(outcome.Reason!, "does not exist");
        Assert.AreEqual(0, ProjectRuns(markerPath));
    }

    [TestMethod]
    public async Task MissingExtractorAssetsIsRetryableNotAFailure()
    {
        // 组件资产缺失（没有任何 script）时全部请求路径按退避重试，而不是升级整仓。
        var emptyComponentDirectory = Path.Combine(_root, "empty-component");
        Directory.CreateDirectory(emptyComponentDirectory);

        var projectPath = Path.Combine(_root, "project-missing-assets");
        Directory.CreateDirectory(projectPath);

        var indexer = IndexerWithStub(emptyComponentDirectory);

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath),
            [Path.Combine(projectPath, "a.ts")],
            Context());

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(CodeIndexConsumerStatus.Retryable, outcome.Status);
        StringAssert.Contains(outcome.Reason!, "extract-ts-symbols.js");
    }

    [TestMethod]
    public async Task PathsNotCoveredByProjectModeFallBackPerFileAndClearTheSessionKey()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.ts");
        var ghost = Path.Combine(projectPath, "ghost.ts"); // 项目模式看不到它

        var result = await indexer.UpdateFilesAsync(
            Descriptor(projectPath),
            [Path.Combine(projectPath, "a.ts"), ghost],
            Context());

        Assert.AreEqual(1, ProjectRuns(markerPath));
        Assert.IsTrue(
            File.ReadAllLines(markerPath).Any(line => line == "file:ghost.ts"),
            "项目模式没覆盖到的路径必须逐文件提取，不能静默丢掉");

        var byPath = result.Outcomes.ToDictionary(outcome => outcome.FilePath, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[Path.Combine(projectPath, "a.ts")].Status);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[ghost].Status);

        Assert.IsNull(
            result.SessionKey,
            "一旦退化到逐文件提取，SessionKey 必须为空：它要如实反映「这一批没有复用同一个快照」");
    }

    [TestMethod]
    public async Task DuplicateAndBlankPathsAreCollapsedIntoOneRun()
    {
        var (indexer, projectPath, markerPath) = Arrange("a.ts");
        var file = Path.Combine(projectPath, "a.ts");

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
        var (indexer, projectPath, markerPath) = Arrange("a.ts");

        var result = await indexer.UpdateFilesAsync(Descriptor(projectPath), [], Context());

        Assert.IsEmpty(result.Outcomes);
        Assert.IsNull(result.SessionKey);
        Assert.AreEqual(0, ProjectRuns(markerPath));
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
