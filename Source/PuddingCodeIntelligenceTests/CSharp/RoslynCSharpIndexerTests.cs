using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

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
            relationKind: CodeRelationKind.Calls, cancellationToken: CancellationToken.None);
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

    /// <summary>
    /// EndLine 必须覆盖**整个声明跨度**，而不是名称标识符所在那一行。
    /// 回归背景：此前用 <c>ISymbol.Locations</c> 取行范围，它指向标识符 token（单行），
    /// 于是 EndLine 恒等于 StartLine ⇒ ref 打开类只返回 1 行、分块边界塌陷。
    /// 断言用**跨度**而非绝对行号：跨度对缩进/命名空间布局不敏感，仍然能抓住“塌陷回去”。
    /// </summary>
    [TestMethod]
    public async Task IndexCompilationAsync_EndLineCoversDeclarationSpan_NotJustIdentifierLine()
    {
        var compilation = CreateTestCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(result.Success, $"Indexing failed: {result.Message}");

        var allSymbols = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-1", "", Limit: 200), CancellationToken.None);

        // Add 体：签名行 + { + return + } ⇒ 跨 4 行（距 3）
        var add = allSymbols.First(s => s.Name == "Add" && s.Kind == CodeSymbolKind.Method);
        Assert.AreEqual(3, add.EndLine - add.StartLine,
            "方法 EndLine 必须覆盖到闭合括号（距 3），而不是与 StartLine 相等");

        // Compute 体：签名行 + { + 两行语句 + } ⇒ 跨 5 行（距 4）
        var compute = allSymbols.First(s => s.Name == "Compute" && s.Kind == CodeSymbolKind.Method);
        Assert.AreEqual(4, compute.EndLine - compute.StartLine,
            "方法体 5 行 ⇒ 距必须是 4");

        // Calculator 容器：包住两个方法 ⇒ 距至少 10
        var calculator = allSymbols.First(s => s.Name == "Calculator" && s.Kind == CodeSymbolKind.Class);
        Assert.IsGreaterThanOrEqualTo(10, calculator.EndLine - calculator.StartLine,
            "类 EndLine 必须覆盖类体，不得只覆盖类名那一行");
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

    /// <summary>
    /// R1-04 门禁：`Action a = Target;` 是<b>方法组转换</b>——符号被引用，但没有发生调用，
    /// 因此只能产生 references，不得产生 calls 边。
    /// </summary>
    [TestMethod]
    public async Task MethodGroupConversion_IsReferencedBut_NotACall()
    {
        var compilation = CreateMethodGroupCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);

        var incoming = await IncomingRelationsAsync("GroupOnly");

        Assert.IsFalse(incoming.Any(r => r.Kind == CodeRelationKind.Calls),
            "方法组转换只是引用，不得报 calls（R1-04 门禁）。");
    }

    /// <summary>正对照：真实调用必须仍产出 calls 边，否则「永不产生 calls」也能让上面那条变绿。</summary>
    [TestMethod]
    public async Task DirectInvocation_IsStillReportedAsACall()
    {
        var compilation = CreateMethodGroupCompilation();

        var indexer = new RoslynCSharpIndexer(_store, _logger);
        var result = await indexer.IndexCompilationAsync(compilation, "ws-1", "proj-1", CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);

        var incoming = await IncomingRelationsAsync("Invoked");

        Assert.IsTrue(incoming.Any(r => r.Kind == CodeRelationKind.Calls),
            "真实调用必须产生 calls 边。");
    }

    private async Task<IReadOnlyList<CodeRelationRecord>> IncomingRelationsAsync(string methodName)
    {
        var symbols = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-1", methodName), CancellationToken.None);
        var target = symbols.First(s => s.Name == methodName && s.Kind == CodeSymbolKind.Method);
        Assert.IsFalse(string.IsNullOrEmpty(target.SymbolId));

        return await _store.ListIncomingRelationsAsync(
            "ws-1", "proj-1", target.SymbolId!, cancellationToken: CancellationToken.None);
    }

    private static Compilation CreateMethodGroupCompilation()
    {
        var sourceCode = """
namespace MyApp.Calls
{
    public class Caller
    {
        public static void GroupOnly() { }

        public static void Invoked() { }

        public static void Run()
        {
            System.Action a = GroupOnly;
            a();
            Invoked();
        }
    }
}
""";

        var tree = CSharpSyntaxTree.ParseText(sourceCode, path: "Calls.cs");
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray<MetadataReference>();

        return CSharpCompilation.Create(
            "CallAssembly",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
