using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;

namespace PuddingCodeIndexTests.Services;

/// <summary>
/// 语义差异门禁：只有**消失**或**签名/种类/名称/容器变化**的符号才可能让别的文件失效；
/// 行号变化不算，新增符号也不算（新增不会打断已有绑定）。
/// </summary>
[TestClass]
public sealed class CodeFileSemanticDiffTests
{
    private const string WorkspaceId = "ws-diff";
    private const string ScopeId = "scope-diff";
    private const string FilePath = @"C:\repo\A.cs";

    private static CodeSymbolRecord Symbol(
        string symbolId,
        string signature = "void M()",
        string name = "M",
        CodeSymbolKind kind = CodeSymbolKind.Method,
        string? container = "C",
        int startLine = 10) =>
        new(WorkspaceId, ScopeId, FilePath, symbolId, name, kind, startLine, startLine + 2, signature, container);

    [TestMethod]
    public void NewOverload_RequiresExistingOverloadsDependentsToRebind()
    {
        // 新增 Pick(string) 会让原本绑定到 Pick(object) 的调用改绑（重载解析变化），
        // 因此 Pick(object) 的依赖方必须重绑。
        var changed = CodeFileSemanticDiff.ChangedSymbolIds(
            [Symbol("M:Acme.Pick(object)", signature: "void Pick(object value)", name: "Pick")],
            [
                Symbol("M:Acme.Pick(object)", signature: "void Pick(object value)", name: "Pick"),
                Symbol("M:Acme.Pick(string)", signature: "void Pick(string value)", name: "Pick"),
            ]);

        Assert.IsTrue(
            changed.Contains("M:Acme.Pick(object)"),
            "新增重载会改变重载解析，已有重载的依赖方必须重绑。");
    }

    /// <summary>正对照：没有同名旧重载时，新增方法不得触发任何重绑（否则退化成全量重绑）。</summary>
    [TestMethod]
    public void BrandNewMethodWithoutPriorOverload_DoesNotForceRebind()
    {
        var changed = CodeFileSemanticDiff.ChangedSymbolIds(
            [Symbol("sym-a")],
            [Symbol("sym-a"), Symbol("M:Acme.Fresh", signature: "void Fresh()", name: "Fresh")]);

        Assert.AreEqual(0, changed.Count, "没有任何同名旧重载时，新增方法不需让任何人重绑。");
    }

    [TestMethod]
    public void VanishedSymbolsRequireDependentsToRebind()
    {
        var changed = CodeFileSemanticDiff.ChangedSymbolIds(
            [Symbol("sym-a"), Symbol("sym-b")],
            [Symbol("sym-a")]);

        CollectionAssert.AreEqual(new[] { "sym-b" }, changed.ToArray());
    }

    [TestMethod]
    public void SignatureChangesRequireDependentsToRebind()
    {
        var changed = CodeFileSemanticDiff.ChangedSymbolIds(
            [Symbol("sym-a", signature: "void M()")],
            [Symbol("sym-a", signature: "void M(int value)")]);

        CollectionAssert.AreEqual(new[] { "sym-a" }, changed.ToArray());
    }

    [TestMethod]
    public void NameKindAndContainerChangesCountAsSemanticChanges()
    {
        Assert.HasCount(1, CodeFileSemanticDiff.ChangedSymbolIds([Symbol("s")], [Symbol("s", name: "N")]));
        Assert.HasCount(1, CodeFileSemanticDiff.ChangedSymbolIds([Symbol("s")], [Symbol("s", kind: CodeSymbolKind.Class)]));
        Assert.HasCount(1, CodeFileSemanticDiff.ChangedSymbolIds([Symbol("s")], [Symbol("s", container: "Other")]));
    }

    [TestMethod]
    public void LineMovesAreNotSemanticChanges()
    {
        var changed = CodeFileSemanticDiff.ChangedSymbolIds(
            [Symbol("sym-a", startLine: 10)],
            [Symbol("sym-a", startLine: 200)]);

        Assert.IsEmpty(changed, "整段代码下移不会让任何引用方失效");
    }

    [TestMethod]
    public void BrandNewSymbolsDoNotInvalidateAnybody()
    {
        var changed = CodeFileSemanticDiff.ChangedSymbolIds(
            [Symbol("sym-a")],
            // 夹具必须用**不同名**的新方法：与旧符号同名同容器就构成重载，
            // 重载会改变重载解析（R1-03），那就该触发重绑，不能用它证明「新增不失效」。
            [Symbol("sym-a"), Symbol("sym-new", name: "Fresh")]);

        Assert.IsEmpty(changed, "无关的新增符号没有旧依赖方需要失效");
    }

    [TestMethod]
    public void AFirstIndexHasNoBaselineAndThereforeNoDependentsToInvalidate()
    {
        Assert.IsEmpty(CodeFileSemanticDiff.ChangedSymbolIds(null, [Symbol("sym-a")]));
        Assert.IsEmpty(CodeFileSemanticDiff.ChangedSymbolIds([], [Symbol("sym-a")]));
    }

    [TestMethod]
    public void UnchangedSymbolsProduceNothing()
    {
        Assert.IsEmpty(CodeFileSemanticDiff.ChangedSymbolIds([Symbol("sym-a")], [Symbol("sym-a")]));
    }

    [TestMethod]
    public void ResultsAreSortedAndDeduplicated()
    {
        var changed = CodeFileSemanticDiff.ChangedSymbolIds(
            [Symbol("sym-c"), Symbol("sym-a"), Symbol("sym-b")],
            []);

        CollectionAssert.AreEqual(new[] { "sym-a", "sym-b", "sym-c" }, changed.ToArray());
    }
}
