using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRetrieval.Contracts;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// <see cref="SymbolAnchor"/> 与 <see cref="RetrievalRequest"/> 锚点约束的契约断言。
/// <para>
/// 断言重点不是"能构造出来"，而是三条<b>失败即可见</b>的不变量：
/// ①空查询只在给了锚点时合法（否则无界噪音）；
/// ②锚点只在关系面意图合法（否则锚点会被静默忽略，退化成名字检索）；
/// ③锚点自身不完整（空项目/空符号）必须当场抛，不得留到引擎里变成"查不到"。
/// </para>
/// </summary>
[TestClass]
public sealed class SymbolAnchorContractTests
{
    private const string ScopeId = "ws-1";

    private static SymbolAnchor Anchor(string projectId = "proj-1", string symbolId = "sym-1") =>
        new(projectId, symbolId);

    [TestMethod]
    public void Anchor_AllowsEmptyQuery_OnRelationFace()
    {
        var request = new RetrievalRequest(
            query: string.Empty,
            scopeId: ScopeId,
            intent: RetrievalIntent.Callers,
            anchor: Anchor());

        Assert.AreEqual(string.Empty, request.Query);
        Assert.IsTrue(request.HasAnchor);
        Assert.AreEqual("sym-1", request.Anchor!.SymbolId);
        Assert.AreEqual("proj-1", request.Anchor.ProjectId);
    }

    [TestMethod]
    public void EmptyQuery_WithoutAnchor_IsRejected()
    {
        // 空查询是无界噪音：没有锚点时"对谁做"完全没有信息。
        var ex = Assert.Throws<ArgumentException>(
            () => new RetrievalRequest(query: "   ", scopeId: ScopeId, intent: RetrievalIntent.Callers));

        Assert.AreEqual("query", ex.ParamName);
    }

    [TestMethod]
    public void Anchor_WithAutoIntent_IsRejected_AndSaysWhichIntentsAreLegal()
    {
        // Auto 会走多路名字检索 ⇒ 锚点被静默忽略。必须当场拒绝，且理由要能指导调用方改正。
        var ex = Assert.Throws<ArgumentException>(
            () => new RetrievalRequest(query: string.Empty, scopeId: ScopeId, intent: RetrievalIntent.Auto, anchor: Anchor()));

        Assert.AreEqual("intent", ex.ParamName);
        StringAssert.Contains(ex.Message, "关系面");
        StringAssert.Contains(ex.Message, "Callers");
    }

    [TestMethod]
    public void Anchor_WithNameFaceIntent_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new RetrievalRequest(query: "OrderService", scopeId: ScopeId, intent: RetrievalIntent.Type, anchor: Anchor()));

        Assert.AreEqual("intent", ex.ParamName);
    }

    [TestMethod]
    [DataRow(RetrievalIntent.Callers)]
    [DataRow(RetrievalIntent.Callees)]
    [DataRow(RetrievalIntent.References)]
    [DataRow(RetrievalIntent.Implementation)]
    [DataRow(RetrievalIntent.Impact)]
    public void Anchor_AcceptsEveryRelationFaceIntent(RetrievalIntent intent)
    {
        var request = new RetrievalRequest(query: string.Empty, scopeId: ScopeId, intent: intent, anchor: Anchor());

        Assert.AreEqual(intent, request.Intent);
        Assert.IsTrue(request.HasAnchor);
    }

    [TestMethod]
    public void Anchor_BlankProjectId_IsRejectedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SymbolAnchor("  ", "sym-1"));

        Assert.AreEqual("projectId", ex.ParamName);
    }

    [TestMethod]
    public void Anchor_BlankSymbolId_IsRejectedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SymbolAnchor("proj-1", string.Empty));

        Assert.AreEqual("symbolId", ex.ParamName);
    }

    [TestMethod]
    public void Anchor_TrimsIdentifiers()
    {
        var anchor = new SymbolAnchor("  proj-1  ", "  sym-1  ");

        Assert.AreEqual("proj-1", anchor.ProjectId);
        Assert.AreEqual("sym-1", anchor.SymbolId);
        Assert.AreEqual("proj-1/sym-1", anchor.ToString());
    }

    [TestMethod]
    public void Subject_EchoesAnchor_NotQuery()
    {
        // 回显必须反映"实际对谁做"：带锚点时回显查询词会让调用方以为走了名字检索。
        var anchored = new RetrievalRequest(
            query: "OrderService",
            scopeId: ScopeId,
            intent: RetrievalIntent.Callers,
            anchor: Anchor(symbolId: "sym-target"));

        Assert.AreEqual("sym-target", anchored.Subject);

        var byName = new RetrievalRequest(query: "OrderService", scopeId: ScopeId, intent: RetrievalIntent.Callers);
        Assert.AreEqual("OrderService", byName.Subject);
        Assert.IsFalse(byName.HasAnchor);
    }
}
