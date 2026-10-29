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
    public void AnonymousCSharpNode_GetsPlaceholderName()
    {
        var runtimeNode = new OutlineNode { Name = null, Kind = "class", Line = 1, EndLine = 2 };
        var outcome = CodeOutlineOutcome.FromCSharp([runtimeNode], 1, false, false);

        Assert.AreEqual("(anonymous)", WorkspaceOpenMapping.FromCSharpOutline(outcome).Nodes[0].Name);
    }
}
