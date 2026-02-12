using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIntelligence.Storage;

namespace PuddingCodeIntelligenceTests.CSharp;

[TestClass]
public sealed class RoslynCSharpIndexerTests : IDisposable
{
    private SqliteCodeIndexStore _store = null!;
    private ILogger<RoslynCSharpIndexer> _logger = null!;

    [TestInitialize]
    public void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-rolyn-indexer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _store = new SqliteCodeIndexStore(Path.Combine(root, "code-index.db"));
        _logger = NullLoggerFactory.Instance.CreateLogger<RoslynCSharpIndexer>();
    }

    [TestMethod]
    public async Task IndexCompilationAsync_ShouldExtractSymbolsAndContainsRelations()
    {
        var compilation = CreateTestCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);

        Assert.IsTrue(result.Success, $"Indexing failed: {result.Message}");
        Assert.AreEqual(CodeIndexStatus.Completed, result.Status);

        var symbols = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-1", "Calculator"), CancellationToken.None);

        Assert.IsTrue(symbols.Count >= 1, "Should find at least one Calculator symbol");
        var calculator = symbols.First(s => s.Name == "Calculator" && s.Kind == CodeSymbolKind.Class);
        Assert.IsNotNull(calculator.Container, "Calculator should have a container (namespace)");

        var allSymbols = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-1", "", Limit: 200), CancellationToken.None);

        var addMethod = allSymbols.FirstOrDefault(s => s.Name == "Add" && s.Kind == CodeSymbolKind.Method);
        Assert.IsNotNull(addMethod, "Should find Add method");

        var computeMethod = allSymbols.FirstOrDefault(s => s.Name == "Compute" && s.Kind == CodeSymbolKind.Method);
        Assert.IsNotNull(computeMethod, "Should find Compute method");

        Assert.IsTrue(allSymbols.Any(s => s.Kind == CodeSymbolKind.Namespace),
            "Should find namespace declarations");
    }

    [TestMethod]
    public async Task IndexCompilationAsync_ShouldExtractCallsRelations()
    {
        var compilation = CreateTestCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(result.Success, $"Indexing failed: {result.Message}");

        var allSymbols = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-1", "", Limit: 200), CancellationToken.None);

        var computeMethod = allSymbols.FirstOrDefault(s => s.Name == "Compute" && s.Kind == CodeSymbolKind.Method);
        Assert.IsNotNull(computeMethod, "Should find Compute method");

        var callees = await _store.ListRelationsAsync("ws-1", "proj-1", computeMethod!.SymbolId,
            CodeRelationKind.Calls, CancellationToken.None);
        Assert.IsTrue(callees.Count >= 1, $"Compute should call at least one method, found {callees.Count}");

        var addSymbol = allSymbols.FirstOrDefault(s => s.Name == "Add" && s.Kind == CodeSymbolKind.Method);
        if (addSymbol is not null)
        {
            Assert.IsTrue(callees.Any(r => r.TargetSymbolId == addSymbol.SymbolId),
                "Compute should call Add");
        }
    }

    [TestMethod]
    public async Task IndexCompilationAsync_ShouldExtractReferences()
    {
        var compilation = CreateTestCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(result.Success, $"Indexing failed: {result.Message}");

        var allSymbols = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-1", "", Limit: 200), CancellationToken.None);

        var addMethod = allSymbols.FirstOrDefault(s => s.Name == "Add" && s.Kind == CodeSymbolKind.Method);
        Assert.IsNotNull(addMethod, "Should find Add method");

        var references = await _store.ListReferencesAsync("ws-1", "proj-1", addMethod!.SymbolId, CancellationToken.None);
        Assert.IsTrue(references.Count >= 1, $"Add should have at least one reference, found {references.Count}");
    }

    [TestMethod]
    public async Task IndexCompilationAsync_ShouldPersistFiles()
    {
        var compilation = CreateTestCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(result.Success, $"Indexing failed: {result.Message}");

        var files = await _store.ListFilesAsync("ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(files.Count >= 1, "Should persist at least one file record");
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_EmptyDescriptor_ShouldReturnFailed()
    {
        var descriptor = new CodeWorkspaceDescriptor(
            "ws-1", "proj-1", "/nonexistent/path",
            ProjectFilePaths: []);
        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexWorkspaceAsync(descriptor, CancellationToken.None);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
    }

    [TestMethod]
    public async Task RemoveWorkspaceIndexAsync_ShouldRemoveProjectAndData()
    {
        var compilation = CreateTestCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);

        var removeResult = await indexer.RemoveWorkspaceIndexAsync("ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(removeResult.Success);

        var symbols = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-1", "", Limit: 10), CancellationToken.None);
        Assert.AreEqual(0, symbols.Count, "All symbols should be removed after project removal");
    }

    private static Compilation CreateTestCompilation()
    {
        var sourceCode = """
namespace MyApp.Utilities
{
    public class Calculator
    {
        public int Add(int a, int b)
        {
            return a + b;
        }

        public int Compute(int x, int y)
        {
            var result = Add(x, y);
            return result + 1;
        }
    }
}

namespace MyApp
{
    public static class Program
    {
        public static void Main()
        {
            var calc = new MyApp.Utilities.Calculator();
            var sum = calc.Compute(3, 4);
            System.Console.WriteLine(sum);
        }
    }
}
""";

        var tree = CSharpSyntaxTree.ParseText(sourceCode, path: "TestProgram.cs");
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray<MetadataReference>();

        return CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
    }

    public void Dispose()
    {
        // Cleanup handled by temp directory
    }
}
