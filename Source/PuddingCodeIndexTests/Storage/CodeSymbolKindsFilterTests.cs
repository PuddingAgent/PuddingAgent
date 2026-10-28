using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndexTests.Services;
using PuddingCodeIndexTests.Services.CodeIndex;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// ADR-089 R6 前置：符号检索的多值 kind 过滤面。
///
/// <para>
/// 背景：<c>RetrievalIntent.Type</c> 需要一次覆盖 Class/Interface/Struct/Enum/Delegate 五种，
/// <c>RetrievalIntent.Member</c> 需要 Method/Constructor/Property/Field/Event/Operator 六种，
/// 而 <see cref="CodeSymbolKind"/> 单值参数装不下 —— 拆成 5–6 次查询会让**跨次去重失效**
/// （ADR-089 硬约束 9/10 要求去重优先于过载判定）。
/// </para>
/// <para>
/// 这三个用例钉住三件事：多值 union 真的生效、<c>Kinds</c> 优先于 <c>Kind</c>、空 <c>Kinds</c> 不改变既有语义。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSymbolKindsFilterTests
{
    private const string Ws = MaintenanceTestData.WorkspaceId;
    private const string Scope = MaintenanceTestData.ScopeId;

    /// <summary>四个同名符号，四种 kind：Class / Method / Property / Interface。</summary>
    private static async Task SeedFourKindsAsync(MaintenanceHarness harness)
    {
        await SeedAsync(harness, "src/a.cs", "sym:class", CodeSymbolKind.Class);
        await SeedAsync(harness, "src/b.cs", "sym:method", CodeSymbolKind.Method);
        await SeedAsync(harness, "src/c.cs", "sym:property", CodeSymbolKind.Property);
        await SeedAsync(harness, "src/d.cs", "sym:interface", CodeSymbolKind.Interface);
    }

    private static async Task SeedAsync(
        MaintenanceHarness harness, string relativePath, string symbolId, CodeSymbolKind kind)
    {
        var fullPath = harness.Combine(relativePath);
        await harness.Store.UpsertFilesAsync(Ws, Scope,
            [new CodeFileRecord(Ws, Scope, fullPath, "C#", DateTimeOffset.UnixEpoch)]);
        await harness.Store.UpsertSymbolsAsync(Ws, Scope,
            [new CodeSymbolRecord(Ws, Scope, fullPath, symbolId, "Handler", kind, 1, 3,
                $"Handler ({symbolId})", Container: null)]);
    }

    [TestMethod]
    public async Task Kinds_Multiple_Returns_Union_Of_Those_Kinds()
    {
        using var harness = new MaintenanceHarness();
        await SeedFourKindsAsync(harness);

        // 模拟 RetrievalIntent.Type：只取"类型"语义，不含 Method / Property。
        var types = await harness.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(
            Ws, "Handler", Scope,
            Kinds: [CodeSymbolKind.Class, CodeSymbolKind.Interface]));

        Assert.AreEqual(2, types.Count, "Kinds=[Class,Interface] 应恰好返回这两个 kind");
        CollectionAssert.AreEquivalent(
            new[] { "sym:class", "sym:interface" },
            types.Select(s => s.SymbolId).ToArray(),
            "不得混入 Method / Property");
    }

    [TestMethod]
    public async Task Kinds_Takes_Precedence_Over_Single_Kind()
    {
        using var harness = new MaintenanceHarness();
        await SeedFourKindsAsync(harness);

        // 同时给 Kind=Class 与 Kinds=[Property]：按契约以 Kinds 为准。
        var result = await harness.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(
            Ws, "Handler", Scope,
            Kind: CodeSymbolKind.Class,
            Kinds: [CodeSymbolKind.Property]));

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("sym:property", result[0].SymbolId,
            "Kinds 非空时必须优先于单值 Kind，否则调用方无法表达『我改了主意』");
    }

    [TestMethod]
    public async Task Empty_Kinds_Falls_Back_To_Single_Kind()
    {
        using var harness = new MaintenanceHarness();
        await SeedFourKindsAsync(harness);

        // 空集合 = 不用多值过滤 ⇒ 回到 Kind 单值的既有语义（这里 Kind 也为空 ⇒ 不筛 kind）。
        var all = await harness.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(
            Ws, "Handler", Scope,
            Kinds: []));

        Assert.AreEqual(4, all.Count, "空 Kinds 不得变成『什么都不返回』");

        // 再钉一层：Kinds 为空 + Kind=Method 时应只返回 Method。
        var methods = await harness.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(
            Ws, "Handler", Scope,
            Kind: CodeSymbolKind.Method,
            Kinds: []));

        Assert.AreEqual(1, methods.Count);
        Assert.AreEqual("sym:method", methods[0].SymbolId);
    }
}
