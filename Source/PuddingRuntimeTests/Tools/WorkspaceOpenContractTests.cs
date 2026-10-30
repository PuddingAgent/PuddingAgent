using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingRuntime.Services.Tools;
using RegistryOutlineNode = PuddingCodeIntelligence.Contracts.OutlineNode;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>workspace_open</c> v1 的**契约测试**：钉的是"结果自身不允许出现自相矛盾的组合"，
/// 以及两个上游 outline 类型都能投影成同一种 load。
/// </summary>
[TestClass]
public sealed class WorkspaceOpenContractTests
{
    [TestMethod]
    public void Ok_WithoutPayload_Throws()
    {
        var threw = false;
        try
        {
            WorkspaceOpenResult.Ok(WorkspaceOpenView.Excerpt, @"D:\repo\a.cs", null!);
        }
        catch (ArgumentNullException ex)
        {
            threw = true;
            StringAssert.Contains(ex.Message, "payload");
        }

        Assert.IsTrue(threw, "成功结果没有负载时必须抛，而不是留一个 Success=true 的空壳。");
    }

    [TestMethod]
    public void Ok_TruncatedWithoutReason_Throws()
    {
        var payload = new ExcerptPayload(["line"], 1, 10);

        var threw = false;
        try
        {
            WorkspaceOpenResult.Ok(WorkspaceOpenView.Excerpt, @"D:\repo\a.cs", payload, truncated: true);
        }
        catch (ArgumentException ex)
        {
            threw = true;
            // 只说"被截断"调用方无法决定下一步看哪里 —— 这是契约级的拒绝，不是注释约定。
            StringAssert.Contains(ex.Message, "截断");
        }

        Assert.IsTrue(threw, "标了截断却不给原因，必须抛。");
    }

    [TestMethod]
    public void Ok_TruncatedWithReason_KeepsBoth()
    {
        var payload = new ExcerptPayload(["line"], 1, 10);

        var result = WorkspaceOpenResult.Ok(
            WorkspaceOpenView.Excerpt, @"D:\repo\a.cs", payload, truncated: true, truncatedReason: "超出 MaxBytes");

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual("超出 MaxBytes", result.TruncatedReason);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void Failed_WithoutError_Throws()
    {
        var threw = false;
        try
        {
            WorkspaceOpenResult.Failed(WorkspaceOpenView.Outline, "   ");
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "失败不给原因必须抛。");
    }

    [TestMethod]
    public void Failed_CarriesNoPayloadAndNoPath()
    {
        var result = WorkspaceOpenResult.Failed(WorkspaceOpenView.Outline, "(文件不存在: a.cs)");

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Payload, "失败结果不得带负载 —— 否则调用方会以为拿到了数据。");
        Assert.IsNull(result.ResolvedPath);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void FromCSharpOutline_MapsNameKindAndTotalCount()
    {
        var runtimeNode = new OutlineNode
        {
            Name = "OrderService",
            Kind = "class",
            Line = 10,
            EndLine = 40,
            Signature = "(int id)",
        };
        var outcome = CodeOutlineOutcome.FromCSharp([runtimeNode], totalCount: 7, truncated: true, hasSyntaxErrors: false);

        var payload = WorkspaceOpenMapping.FromCSharpOutline(outcome);

        Assert.AreEqual(1, payload.Nodes.Count);
        Assert.AreEqual("OrderService", payload.Nodes[0].Name);
        Assert.AreEqual("class", payload.Nodes[0].Kind);
        Assert.AreEqual(10, payload.Nodes[0].StartLine);

        // TotalCount 必须来自服务（真实总数），不能等于映射后的节点数 —— 否则截断信息丢失。
        Assert.AreEqual(7, payload.TotalCount);
        Assert.IsTrue(payload.Truncated);
    }

    [TestMethod]
    public void FromRegistryOutline_MapsKindAsString()
    {
        var registryNode = new RegistryOutlineNode("Title", CodeSymbolKind.Unknown, 1, 1);
        var outcome = CodeOutlineOutcome.FromOutliner([registryNode]);

        var payload = WorkspaceOpenMapping.FromRegistryOutline(outcome);

        Assert.AreEqual(1, payload.Nodes.Count);
        Assert.AreEqual("Title", payload.Nodes[0].Name);
        Assert.AreEqual("Unknown", payload.Nodes[0].Kind);
        Assert.AreEqual(1, payload.TotalCount);
        Assert.IsFalse(payload.Truncated);
    }

    [TestMethod]
    public void FromProjectMap_PreservesTrueTypeCount()
    {
        // Types 被 MaxTopLevelTypes 截断（默认 3）。若不带真实总数，
        // 调用方会把「只列了 3 个」读成「这个文件只有 3 个类型」——
        // 这属于 ADR-089 §8.3 禁止的 hollow negation。
        var node = new ProjectMapNode { Name = "root", IsRoot = true, TotalFileCount = 12 };
        node.Files.Add(new ProjectMapFileEntry
        {
            Name = "Big.cs",
            Types = ["class A", "class B", "class C"],
            TotalTypeCount = 7,
        });
        var outcome = new ProjectMapOutcome(true, null, "root", 1, node);

        var payload = WorkspaceOpenMapping.FromProjectMap(outcome);

        Assert.AreEqual(12, payload.TotalFileCount, "目录的文件总数要原样透传。");
        Assert.AreEqual(3, payload.Root.Files[0].Types.Count);
        Assert.AreEqual(7, payload.Root.Files[0].TotalTypeCount, "真实类型总数不能被截断掩盖。");
    }

    [TestMethod]
    public void FromProjectMap_TotalsAllNodes_NotJustRootDirectFiles()
    {
        // 回归守卫：根目录通常没有直属 .cs。若顶层 total_file_count 直接取 root.TotalFileCount，
        // 一份有几千个文件的地图会报 0，调用方读成「地图里没有文件」（ADR-089 §8.3）。
        var root = new ProjectMapNode { Name = "root", IsRoot = true, TotalFileCount = 0 };
        root.Children.Add(new ProjectMapNode { Name = "a", TotalFileCount = 3 });
        root.Children.Add(new ProjectMapNode { Name = "b", TotalFileCount = 4 });
        var outcome = new ProjectMapOutcome(true, null, "root", 1, root);

        var payload = WorkspaceOpenMapping.FromProjectMap(outcome);

        Assert.AreEqual(7, payload.TotalFileCount, "顶层总数必须是各节点之和，不能等于根目录直属数（0）。");
        Assert.AreEqual(0, payload.Root.TotalFileCount, "节点级仍报该目录真实文件数，语义不变。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(MapPayload.TotalFileCountScope), "口径说明不能为空。");
    }

    [TestMethod]
    public void AnonymousCSharpNode_GetsPlaceholderName()
    {
        var runtimeNode = new OutlineNode { Name = null, Kind = "class", Line = 1, EndLine = 2 };
        var outcome = CodeOutlineOutcome.FromCSharp([runtimeNode], 1, false, false);

        Assert.AreEqual("(anonymous)", WorkspaceOpenMapping.FromCSharpOutline(outcome).Nodes[0].Name);
    }
}
