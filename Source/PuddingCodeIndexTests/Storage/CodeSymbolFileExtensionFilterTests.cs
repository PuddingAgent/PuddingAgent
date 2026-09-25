using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndexTests.Services;
using PuddingCodeIndexTests.Services.CodeIndex;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// ADR-089 §2.3「文件类型」过滤面：符号检索必须能表达「只看某类文件的符号」。
///
/// 修复前 <see cref="CodeSymbolSearchRequest"/> 完全没有语言/扩展名维度 ⇒ 调用方只想要 C# 符号时，
/// 其他语言里同名符号（本仓实测：TS 的 <c>Recall@1 0.0682</c> 那一层）会一起返回并白占结果位。
/// </summary>
[TestClass]
public sealed class CodeSymbolFileExtensionFilterTests
{
    private const string Ws = MaintenanceTestData.WorkspaceId;
    private const string Scope = MaintenanceTestData.ScopeId;

    /// <summary>造出四个文件里各一个**同名**符号：<c>.cs</c> / <c>.ts</c> / <c>.md</c> / 无扩展名。</summary>
    private static async Task SeedFourExtensionsAsync(MaintenanceHarness harness)
    {
        await SeedAsync(harness, "src/main.cs", "sym:cs", "C#");
        await SeedAsync(harness, "web/main.ts", "sym:ts", "TypeScript");
        await SeedAsync(harness, "docs/main.md", "sym:md", "Markdown");
        await SeedAsync(harness, "tools/MAIN", "sym:noext", "Unknown");
    }

    private static async Task SeedAsync(
        MaintenanceHarness harness, string relativePath, string symbolId, string language)
    {
        var fullPath = harness.Combine(relativePath);
        await harness.Store.UpsertFilesAsync(Ws, Scope,
            [new CodeFileRecord(Ws, Scope, fullPath, language, DateTimeOffset.UnixEpoch)]);
        await harness.Store.UpsertSymbolsAsync(Ws, Scope,
            [new CodeSymbolRecord(Ws, Scope, fullPath, symbolId, "Handler", CodeSymbolKind.Class, 1, 3,
                $"class Handler ({symbolId})", Container: null)]);
    }

    private static Task<IReadOnlyList<CodeSymbolRecord>> SearchAsync(
        MaintenanceHarness harness, IReadOnlyList<string>? extensions) =>
        harness.Store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(Ws, "Handler", Scope, FileExtensions: extensions));

    [TestMethod]
    public async Task Single_Extension_Returns_Only_That_File_Type()
    {
        using var harness = new MaintenanceHarness();
        await SeedFourExtensionsAsync(harness);

        var onlyCSharp = await SearchAsync(harness, [".cs"]);

        // 核心判据（本刀要修的行为）：指名 .cs 后，其余三种文件类型的同名符号都必须消失。
        Assert.AreEqual(1, onlyCSharp.Count, "只指名 .cs 时应只返回 .cs 里的符号");
        Assert.AreEqual("sym:cs", onlyCSharp[0].SymbolId);
    }

    [TestMethod]
    public async Task Multiple_Extensions_Union_Their_Files()
    {
        using var harness = new MaintenanceHarness();
        await SeedFourExtensionsAsync(harness);

        var csharpAndTs = await SearchAsync(harness, [".cs", ".ts"]);

        Assert.AreEqual(2, csharpAndTs.Count, "多个扩展名应取并集");
        CollectionAssert.AreEquivalent(
            new[] { "sym:cs", "sym:ts" },
            csharpAndTs.Select(s => s.SymbolId).ToArray());
    }

    [TestMethod]
    public async Task Extension_Without_Leading_Dot_And_Mixed_Case_Are_Accepted()
    {
        using var harness = new MaintenanceHarness();
        await SeedFourExtensionsAsync(harness);

        // 调用方写 `cs`（无点）或 `.Cs`（混合大小写）都应等价于 `.cs` —— 归一化在存储层完成。
        var withoutDot = await SearchAsync(harness, ["cs"]);
        var mixedCase = await SearchAsync(harness, [".Cs"]);

        Assert.AreEqual(1, withoutDot.Count, "无前导点的 `cs` 应等价于 `.cs`");
        Assert.AreEqual("sym:cs", withoutDot[0].SymbolId);
        Assert.AreEqual(1, mixedCase.Count, "混合大小写 `.Cs` 应等价于 `.cs`");
        Assert.AreEqual("sym:cs", mixedCase[0].SymbolId);
    }

    [TestMethod]
    public async Task Omitted_Or_Empty_Filter_Keeps_All_File_Types()
    {
        using var harness = new MaintenanceHarness();
        await SeedFourExtensionsAsync(harness);

        // 回归守卫：缺省（null）与显式空集合都必须逐字保留历史行为 —— 不过滤，四种全返回。
        var omitted = await harness.Store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(Ws, "Handler", Scope));
        var empty = await SearchAsync(harness, []);

        Assert.AreEqual(4, omitted.Count, "省略 file_extensions 时不得过滤（历史行为）");
        Assert.AreEqual(4, empty.Count, "显式空集合时不得过滤（历史行为）");
        CollectionAssert.AreEqual(
            omitted.Select(s => s.SymbolId).ToList(),
            empty.Select(s => s.SymbolId).ToList(),
            "缺省与显式空集合必须逐条同序相同");
    }
}
