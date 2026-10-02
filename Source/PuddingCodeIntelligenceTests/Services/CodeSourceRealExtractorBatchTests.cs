using Microsoft.Extensions.Logging.Abstractions;

using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;
using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.Python;
using PuddingCodeIntelligence.TypeScript;

namespace PuddingCodeIntelligenceTests.Services;

/// <summary>
/// D4 **真实提取器**端到端批量接缝门禁（2026-10-02）。
/// <para>
/// 既有的批量用例都用桩提取器（TS/Python）或内存 AdhocWorkspace（C#），只证明「接缝的语义」；
/// 这一组用**真正的** <c>Extractors/Scripts/extract-ts-symbols.js</c> / <c>extract-py-symbols.py</c>
/// 跑真实源码，证明「接缝在真工具链上确实产出正确的 payload，且一个批次只跑一次提取」——
/// 这是新链路已在产品宿主启用后最需要的一份证据。
/// </para>
/// <para>
/// 需要 Node.js / Python 与组件资产（测试输出目录里就有 <c>Scripts/</c>）；缺失即跳过，
/// 不让环境差异把门禁变成噪声。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSourceRealExtractorBatchTests : IDisposable
{
    private const string WorkspaceId = "ws-real-batch";
    private const string ScopeId = "scope-real-batch";

    private string _root = null!;
    private SqliteCodeIndexStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-real-extractor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "db", "code-index.db"));
    }

    private static bool AssetsAvailable() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Scripts", "extract-ts-symbols.js"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "Scripts", "extract-py-symbols.py"));

    private CodeWorkspaceDescriptor Descriptor(string projectPath) =>
        new(WorkspaceId, ScopeId, projectPath, ProjectFilePaths: []);

    private static CodeIndexBatchContext Context() => new("cfg", "policy", Generation: 1);

    [TestMethod]
    public async Task RealTypeScriptExtractionReturnsOnePayloadPerFile()
    {
        if (!AssetsAvailable())
        {
            Assert.Inconclusive("extractor assets are not present in the test output directory");
            return;
        }

        var project = Path.Combine(_root, "ts");
        Directory.CreateDirectory(project);

        var alpha = Path.Combine(project, "alpha.ts");
        var beta = Path.Combine(project, "beta.ts");
        await File.WriteAllTextAsync(alpha, "export class Alpha {\n  run(): number { return 1; }\n}\n");
        await File.WriteAllTextAsync(beta, "export function betaHelper(): number { return 2; }\n");

        var indexer = new TypeScriptIndexer(_store, NullLogger<TypeScriptIndexer>.Instance, new ExtractorAssetResolver());

        var result = await indexer.UpdateFilesAsync(Descriptor(project), [alpha, beta], Context());

        var byPath = result.Outcomes.ToDictionary(outcome => outcome.FilePath, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[alpha].Status, byPath[alpha].Reason);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[beta].Status, byPath[beta].Reason);

        var alphaNames = byPath[alpha].Payload!.Symbols.Select(symbol => symbol.Name).ToArray();
        var betaNames = byPath[beta].Payload!.Symbols.Select(symbol => symbol.Name).ToArray();
        Assert.Contains("Alpha", alphaNames, $"真实提取器应当报出 Alpha，实际: {string.Join(',', alphaNames)}");
        Assert.Contains("betaHelper", betaNames, $"真实提取器应当报出 betaHelper，实际: {string.Join(',', betaNames)}");

        // 真工具链上的「一个批次一次提取」：项目模式可用时会有会话键（桩用例已另行锁定退化行为）。
        Assert.IsNotNull(result.SessionKey, "真实 TS 提取器的项目模式应当可用（否则说明退化成了逐文件提取）");
        Assert.IsEmpty(await _store.ListFilesAsync(WorkspaceId, ScopeId), "批量接缝绝不写索引");
    }

    [TestMethod]
    public async Task RealPythonExtractionReturnsOnePayloadPerFile()
    {
        if (!AssetsAvailable())
        {
            Assert.Inconclusive("extractor assets are not present in the test output directory");
            return;
        }

        var project = Path.Combine(_root, "py");
        Directory.CreateDirectory(project);

        var first = Path.Combine(project, "alpha.py");
        var second = Path.Combine(project, "beta.py");
        await File.WriteAllTextAsync(first, "class Alpha:\n    def run(self):\n        return 1\n");
        await File.WriteAllTextAsync(second, "def beta_helper():\n    return 2\n");

        var indexer = new PythonIndexer(_store, NullLogger<PythonIndexer>.Instance, new ExtractorAssetResolver());

        var result = await indexer.UpdateFilesAsync(Descriptor(project), [first, second], Context());

        var byPath = result.Outcomes.ToDictionary(outcome => outcome.FilePath, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[first].Status, byPath[first].Reason);
        Assert.AreEqual(CodeIndexConsumerStatus.Applied, byPath[second].Status, byPath[second].Reason);

        var firstNames = byPath[first].Payload!.Symbols.Select(symbol => symbol.Name).ToArray();
        var secondNames = byPath[second].Payload!.Symbols.Select(symbol => symbol.Name).ToArray();
        Assert.Contains("Alpha", firstNames, $"真实提取器应当报出 Alpha，实际: {string.Join(',', firstNames)}");
        Assert.Contains("beta_helper", secondNames, $"真实提取器应当报出 beta_helper，实际: {string.Join(',', secondNames)}");

        Assert.IsEmpty(await _store.ListFilesAsync(WorkspaceId, ScopeId), "批量接缝绝不写索引");
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
