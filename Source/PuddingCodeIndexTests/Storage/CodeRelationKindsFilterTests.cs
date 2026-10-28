using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndexTests.Services;
using PuddingCodeIndexTests.Services.CodeIndex;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// ADR-089 R6 前置：关系查询的多值 kind 过滤面。
///
/// <para>
/// 背景：<c>RetrievalIntent.Implementation</c> 需要一次覆盖 Inherits/Implements/Overrides 三种边。
/// 若沿用单值 <c>relationKind</c>，调用方必须自己拆 3 次查询再把结果拼起来，
/// 而"哪些类型实现了这个接口"本来就该是一次查询的答案。
/// </para>
/// <para>
/// 这三个用例钉住：多值 union 生效、relationKinds 优先于单值、空集合回退既有语义。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeRelationKindsFilterTests
{
    private const string Ws = MaintenanceTestData.WorkspaceId;
    private const string Scope = MaintenanceTestData.ScopeId;

    /// <summary>造 iface ← impl 三种不同 kind 的出边：Implements / References / Calls。</summary>
    private static async Task SeedThreeKindsAsync(MaintenanceHarness harness)
    {
        var iface = await SeedSymbolAsync(harness, "src/iface.cs", "sym:iface");
        var impl = await SeedSymbolAsync(harness, "src/impl.cs", "sym:impl");

        await harness.Store.UpsertRelationsAsync(Ws, Scope,
        [
            new CodeRelationRecord(Ws, Scope, impl, iface, CodeRelationKind.Implements, 10, impl),
            new CodeRelationRecord(Ws, Scope, impl, iface, CodeRelationKind.References, 11, impl),
            new CodeRelationRecord(Ws, Scope, impl, iface, CodeRelationKind.Calls, 12, impl),
        ]);
    }

    private static async Task<string> SeedSymbolAsync(
        MaintenanceHarness harness, string relativePath, string symbolId)
    {
        var fullPath = harness.Combine(relativePath);
        await harness.Store.UpsertFilesAsync(Ws, Scope,
            [new CodeFileRecord(Ws, Scope, fullPath, "C#", DateTimeOffset.UnixEpoch)]);
        await harness.Store.UpsertSymbolsAsync(Ws, Scope,
            [new CodeSymbolRecord(Ws, Scope, fullPath, symbolId, symbolId, CodeSymbolKind.Class, 1, 20,
                null, Container: null)]);
        return symbolId;
    }

    [TestMethod]
    public async Task RelationKinds_Multiple_Returns_Union_Of_Those_Kinds()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeKindsAsync(harness);

        // 模拟 RetrievalIntent.Implementation：三种"实现关系"一次取回。
        var implEdges = await harness.Store.ListRelationsAsync(
            Ws, Scope, "sym:impl",
            relationKinds: [CodeRelationKind.Implements, CodeRelationKind.Inherits, CodeRelationKind.Overrides]);

        Assert.AreEqual(1, implEdges.Count, "三条边里只有 Implements 命中该集合");
        Assert.AreEqual(CodeRelationKind.Implements, implEdges[0].Kind);
    }

    [TestMethod]
    public async Task RelationKinds_Takes_Precedence_Over_Single_Kind()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeKindsAsync(harness);

        var result = await harness.Store.ListRelationsAsync(
            Ws, Scope, "sym:impl",
            relationKind: CodeRelationKind.Calls,
            relationKinds: [CodeRelationKind.References]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(CodeRelationKind.References, result[0].Kind,
            "relationKinds 非空时必须优先于单值 relationKind");
    }

    [TestMethod]
    public async Task Empty_RelationKinds_Falls_Back_To_Single_Kind()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeKindsAsync(harness);

        var all = await harness.Store.ListRelationsAsync(Ws, Scope, "sym:impl", relationKinds: []);
        Assert.AreEqual(3, all.Count, "空 relationKinds 不得变成『什么都不返回』");

        var calls = await harness.Store.ListRelationsAsync(
            Ws, Scope, "sym:impl",
            relationKind: CodeRelationKind.Calls,
            relationKinds: []);
        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual(CodeRelationKind.Calls, calls[0].Kind);
    }
}
