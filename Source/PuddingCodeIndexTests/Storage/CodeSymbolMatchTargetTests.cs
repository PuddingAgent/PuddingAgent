using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndexTests.Services;
using PuddingCodeIndexTests.Services.CodeIndex;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// ADR-089 §2.3「匹配域」：符号检索必须能表达「只关注符号名」。
///
/// 修复前的实测症状（本测试即其回归锁）：搜 <c>conf</c> 返回 40 条**全部是 <c>.ctor</c>** ——
/// 构造器的 Name 是 <c>.ctor</c>，命中实际发生在 Signature 列（其签名里含该词）。
/// 调用方无法表达「只在符号名上匹配」，于是拿到一堆假阳性。
/// </summary>
[TestClass]
public sealed class CodeSymbolMatchTargetTests
{
    private const string Ws = MaintenanceTestData.WorkspaceId;
    private const string Scope = MaintenanceTestData.ScopeId;

    /// <summary>
    /// 造出三条各在不同列命中 <c>Conf</c> 的符号：
    /// ① Name 命中；② 仅 Signature 命中（模拟 <c>.ctor</c>）；③ 仅 Container 命中。
    /// </summary>
    private static async Task SeedThreeTargetsAsync(MaintenanceHarness harness)
    {
        await SeedAsync(harness, "a.cs", "sym:class", "ConfLoader", CodeSymbolKind.Class,
            signature: "class ConfLoader", container: null);
        await SeedAsync(harness, "b.cs", "sym:ctor", ".ctor", CodeSymbolKind.Constructor,
            signature: "ConfLoader..ctor(ILogger logger)", container: null);
        await SeedAsync(harness, "c.cs", "sym:member", "Load", CodeSymbolKind.Method,
            signature: "void Load()", container: "ConfLoader");
    }

    private static async Task SeedAsync(
        MaintenanceHarness harness, string relativePath, string symbolId, string name,
        CodeSymbolKind kind, string signature, string? container)
    {
        var fullPath = harness.Combine(relativePath);
        await harness.Store.UpsertFilesAsync(Ws, Scope,
            [new CodeFileRecord(Ws, Scope, fullPath, "C#", DateTimeOffset.UnixEpoch)]);
        await harness.Store.UpsertSymbolsAsync(Ws, Scope,
            [new CodeSymbolRecord(Ws, Scope, fullPath, symbolId, name, kind, 1, 5, signature, container)]);
    }

    private static Task<IReadOnlyList<CodeSymbolRecord>> SearchAsync(MaintenanceHarness harness, string query,
        CodeSymbolMatchTarget target) =>
        harness.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(Ws, query, Scope, MatchTarget: target));

    [TestMethod]
    public async Task NameOnly_Excludes_Signature_Only_And_Container_Only_Hits()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeTargetsAsync(harness);

        var nameOnly = await SearchAsync(harness, "Conf", CodeSymbolMatchTarget.Name);

        // 核心判据（本刀要修的行为）：聚焦符号名后，签名命中与容器命中都必须被排除。
        // 修复前这里会返回 3 条 —— 这正是"搜类名返回一堆构造器"。
        Assert.AreEqual(1, nameOnly.Count, "Name 匹配域下只应返回 Name 命中的那一条");
        Assert.AreEqual("ConfLoader", nameOnly[0].Name);

        // 不得出现任何构造器：构造器的 Name 是 `.ctor`，它不可能通过 Name 命中 "Conf"。
        Assert.IsFalse(nameOnly.Any(s => s.Kind == CodeSymbolKind.Constructor),
            "Name 匹配域下不得返回签名里提到该词的构造器");
    }

    [TestMethod]
    public async Task SignatureOnly_Returns_Every_Signature_Hit_Even_When_Name_Does_Not_Match()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeTargetsAsync(harness);

        var signatureOnly = await SearchAsync(harness, "Conf", CodeSymbolMatchTarget.Signature);

        // 真实索引里 Signature 通常含 Name（`class ConfLoader`），所以有两条签名命中：
        //   ① ConfLoader —— Name 也命中；② `.ctor` —— 它的 Name 是 `.ctor`，**只在签名里**提到 Conf。
        // ② 正是“搜类名却返回一堆构造器”的来源：Name 聚焦后它必须消失（见 NameOnly 测试）。
        Assert.AreEqual(2, signatureOnly.Count);
        CollectionAssert.AreEquivalent(
            new[] { "ConfLoader", ".ctor" },
            signatureOnly.Select(s => s.Name).ToArray());
        Assert.IsFalse(signatureOnly.Any(s => s.Name == "Load"),
            "仅 Container 命中的符号不得出现在 Signature 匹配域里");
    }

    [TestMethod]
    public async Task ContainerOnly_Returns_Exactly_The_Container_Hit()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeTargetsAsync(harness);

        var containerOnly = await SearchAsync(harness, "Conf", CodeSymbolMatchTarget.Container);

        Assert.AreEqual(1, containerOnly.Count);
        Assert.AreEqual("Load", containerOnly[0].Name);
    }

    [TestMethod]
    public async Task Combined_Targets_Union_Their_Hits()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeTargetsAsync(harness);

        var nameAndSignature = await SearchAsync(harness, "Conf",
            CodeSymbolMatchTarget.Name | CodeSymbolMatchTarget.Signature);

        Assert.AreEqual(2, nameAndSignature.Count, "多个匹配域应取并集");
        CollectionAssert.AreEquivalent(
            new[] { "ConfLoader", ".ctor" },
            nameAndSignature.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public async Task Default_Target_Equals_Explicit_All_And_Keeps_PreFix_Behaviour()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeTargetsAsync(harness);

        // 缺省的 MatchTarget 必须是 All：现有调用方一行不改、行为逐字不变。
        var byDefault = await harness.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(Ws, "Conf", Scope));
        var explicitAll = await SearchAsync(harness, "Conf", CodeSymbolMatchTarget.All);

        Assert.AreEqual(3, byDefault.Count, "默认匹配域 = 三列全开（与修复前一致）");
        CollectionAssert.AreEqual(
            byDefault.Select(s => s.SymbolId).ToList(),
            explicitAll.Select(s => s.SymbolId).ToList(),
            "缺省与显式 All 必须逐条同序相同");
    }

    [TestMethod]
    public async Task None_Is_Treated_As_All_Not_As_Return_Everything()
    {
        using var harness = new MaintenanceHarness();
        await SeedThreeTargetsAsync(harness);

        var none = await SearchAsync(harness, "Conf", CodeSymbolMatchTarget.None);

        // None 若被当成"无匹配域"就会退化为"返回全部符号"（连不相关的一起返回），
        // 那是个静默的语义陷阱；这里锁定它等同于 All。
        Assert.AreEqual(3, none.Count);
        Assert.IsFalse(none.Any(s => s.Name == "Load" && s.Kind != CodeSymbolKind.Method));
    }
}
