using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIntelligenceTests.CSharp;

/// <summary>
/// U3-B3 at the indexer level: which step removes the rows of a file that disappeared, and what the per-file
/// entry point refuses.
/// </summary>
[TestClass]
public sealed class RoslynCSharpIndexerRemovalTests
{
    private const string WorkspaceId = "ws-u3b3";
    private const string ProjectId = "proj-u3b3";

    private SqliteCodeIndexStore _store = null!;
    private ILogger<RoslynCSharpIndexer> _logger = null!;
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-u3b3-indexer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "code-index.db"));
        _logger = NullLoggerFactory.Instance.CreateLogger<RoslynCSharpIndexer>();
    }

    /// <summary>
    /// Baseline fact measured in U3-B3 step 0, kept as the record of the division of labour: a <b>full</b>
    /// re-index walks the files of the compilation, so a file that is no longer part of it keeps its rows —
    /// there is no sweep here (manifest calibration/sweep is U3-C, ADR-089 §2). The per-file removal
    /// primitive is what clears them, which is what this slice wires into the change pipeline.
    /// </summary>
    [TestMethod]
    public async Task Full_Re_Index_Does_Not_Sweep_A_File_Missing_From_The_Compilation_But_Per_File_Removal_Does()
    {
        var indexer = new RoslynCSharpIndexer(_store, _logger);

        const string sourceAlpha = "namespace Probe { public class Alpha { public int Value() => 1; } }";
        const string sourceBeta = "namespace Probe { public class Beta { public int Value() => 2; } }";
        var fileAlpha = Path.Combine(_root, "Alpha.cs");
        var fileBeta = Path.Combine(_root, "Beta.cs");
        await File.WriteAllTextAsync(fileAlpha, sourceAlpha);
        await File.WriteAllTextAsync(fileBeta, sourceBeta);

        await indexer.IndexCompilationAsync(
            CreateCompilation((fileAlpha, sourceAlpha), (fileBeta, sourceBeta)), WorkspaceId, ProjectId);
        Assert.IsNotEmpty(await QueryAsync("Alpha"), "positive control: the file is indexed");

        // "The file is deleted": the next full run no longer sees it in the compilation.
        File.Delete(fileAlpha);
        await indexer.IndexCompilationAsync(CreateCompilation((fileBeta, sourceBeta)), WorkspaceId, ProjectId);

        Assert.IsNotEmpty(await QueryAsync("Alpha"),
            "a full re-index deliberately has no sweep: the rows of a file missing from the compilation stay " +
            "until calibration (U3-C) — if this ever fails, sweeping was implemented and this record must move");
        Assert.IsNotEmpty(await _store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, fileAlpha));

        // What this slice wires into the pipeline: per-file removal clears them for real.
        var removed = await _store.RemoveFilesAsync(WorkspaceId, ProjectId, [fileAlpha]);

        Assert.AreEqual(1, removed);
        Assert.IsEmpty(await QueryAsync("Alpha"), "the per-file removal must make the symbol unqueryable");
        Assert.IsEmpty(await _store.GetSymbolsByFileAsync(WorkspaceId, ProjectId, fileAlpha));
        Assert.IsNotEmpty(await QueryAsync("Beta"), "the other file must survive untouched");
    }

    [TestMethod]
    public async Task IndexFileAsync_MissingFile_Is_Reported_As_Failed()
    {
        var indexer = new RoslynCSharpIndexer(_store, _logger);

        var result = await indexer.IndexFileAsync(
            Descriptor(), Path.Combine(_root, "does-not-exist.cs"));

        Assert.IsFalse(result.Success, "a deleted file must not be reported as a successful per-file index");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "does not exist");
    }

    [TestMethod]
    public async Task IndexFileAsync_FileUnderANoiseDirectory_Is_Reported_As_Failed()
    {
        var indexer = new RoslynCSharpIndexer(_store, _logger);

        var noiseDirectory = Path.Combine(_root, "bin");
        Directory.CreateDirectory(noiseDirectory);
        var noiseFile = Path.Combine(noiseDirectory, "Generated.cs");
        await File.WriteAllTextAsync(noiseFile, "class Generated { }");

        var result = await indexer.IndexFileAsync(Descriptor(), noiseFile);

        Assert.IsFalse(result.Success, "a noise path must not be indexed on its own");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "excluded");
    }

    [TestMethod]
    public async Task IndexFileAsync_MissingProjectPath_Is_Reported_As_Failed()
    {
        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var file = Path.Combine(_root, "Real.cs");
        await File.WriteAllTextAsync(file, "class Real { }");

        var result = await indexer.IndexFileAsync(
            new CodeWorkspaceDescriptor(WorkspaceId, ProjectId, Path.Combine(_root, "missing-project"), ProjectFilePaths: []),
            file);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "Project path does not exist");
    }

    private Task<IReadOnlyList<CodeSymbolRecord>> QueryAsync(string name) =>
        _store.SearchSymbolsAsync(new CodeSymbolSearchRequest(WorkspaceId, name));

    private CodeWorkspaceDescriptor Descriptor() =>
        new(WorkspaceId, ProjectId, _root, ProjectFilePaths: []);

    private static Compilation CreateCompilation(params (string Path, string Source)[] files)
    {
        var trees = files
            .Select(file => CSharpSyntaxTree.ParseText(file.Source, path: file.Path))
            .ToArray<SyntaxTree>();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .ToArray<MetadataReference>();

        return CSharpCompilation.Create(
            "RemovalTestAssembly",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
