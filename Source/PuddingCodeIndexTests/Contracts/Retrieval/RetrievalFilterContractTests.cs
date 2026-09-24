using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A15（额外）：过滤面必须**正交且 AND 组合**，尤其"只关注类名称"这个面（ADR-089 §2.3）。
/// <para>
/// 实测动机：<c>code_symbol_search("conf")</c> 返回 40 条**全部是 <c>.ctor</c>** ——
/// 匹配发生在签名文本而非 <c>name</c>；而工具**无法表达"只匹配符号名"**。
/// </para>
/// </summary>
[TestClass]
public sealed class RetrievalFilterContractTests
{
    /// <summary>只关注类名称 ⇒ 只在签名文本里命中的构造函数必须被过滤掉；各面 AND 组合。</summary>
    [TestMethod]
    public void A15_Filter_Facets_Are_Orthogonal_And_Combined_With_And()
    {
        var constructorHit = new RetrievalHit(
            SymbolIdentity.Create("Msg.ctor", "Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs", 20),
            "MessageDeliveryDispatcher",
            CodeSymbolKind.Constructor,
            RetrievalHitKind.TextHit,
            new RetrievalEvidence("Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs", 20, snippet: "JsonLlmConfigService(...)"),
            RetrievalConfidence.Lexical,
            0.4,
            "签名文本里出现 conf",
            RetrievalMatchTarget.Declaration);

        var classHit = new RetrievalHit(
            SymbolIdentity.Create("Sym.Config", "Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs", 42),
            "MessageDeliveryDispatcher",
            CodeSymbolKind.Class,
            RetrievalHitKind.Declaration,
            new RetrievalEvidence("Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs", 42, snippet: "class MessageDeliveryDispatcher"),
            RetrievalConfidence.Semantic,
            0.9,
            "符号名命中 conf",
            RetrievalMatchTarget.SymbolName);

        var otherDirectoryHit = RetrievalTestData.Hit(
            "Sym.Other",
            filePath: "Source/PuddingCore/Other.cs",
            hitKind: RetrievalHitKind.Declaration);

        var tsFileHit = RetrievalTestData.Hit(
            "Sym.Ts",
            filePath: "Source/PuddingRuntime/Services/Messaging/web.ts",
            hitKind: RetrievalHitKind.Declaration);

        // ① 匹配域面：用户说的"只关注类名称"
        var symbolNameOnly = RetrievalFilter.ForMatchTargets(RetrievalMatchTarget.SymbolName);
        Assert.IsFalse(symbolNameOnly.Matches(constructorHit), "只关注类名称 ⇒ 签名文本命中必须被过滤掉");
        Assert.IsTrue(symbolNameOnly.Matches(classHit));
        Assert.IsFalse(constructorHit.IsSymbolNameMatch, "该命中的匹配域不含符号名");

        // ② AND 组合：匹配域 + 符号种类
        var nameAndClass = new RetrievalFilter(
            matchTargets: RetrievalMatchTarget.SymbolName,
            symbolKinds: [CodeSymbolKind.Class]);
        Assert.IsTrue(nameAndClass.Matches(classHit));
        Assert.IsFalse(nameAndClass.Matches(constructorHit));
        // 关掉一个面 ⇒ 重新纳入（证明面彼此独立、可独立开关）
        Assert.IsTrue(RetrievalFilter.ForSymbolKinds(CodeSymbolKind.Constructor).Matches(constructorHit));
        Assert.IsTrue(symbolNameOnly.Matches(classHit));

        // ③ 目录面 + 扩展名面（用户举的另外两个面；实测加这两个面后精确到可合法返回空）
        var dirAndExtension = RetrievalFilter.ForFileExtensions(".cs");
        Assert.IsTrue(RetrievalFilter.None.Matches(otherDirectoryHit), "无约束过滤器必须放行一切");
        Assert.IsTrue(dirAndExtension.Matches(otherDirectoryHit));
        Assert.IsFalse(dirAndExtension.Matches(tsFileHit), "ext=.cs ⇒ .ts 必须被过滤掉");

        var messagingOnly = new RetrievalFilter(
            fileExtensions: [".cs"],
            directoryPath: "Source/PuddingRuntime/Services/Messaging");
        Assert.IsTrue(messagingOnly.Matches(classHit));
        Assert.IsFalse(messagingOnly.Matches(otherDirectoryHit), "目录面必须把其它目录排除");
        Assert.IsFalse(messagingOnly.Matches(tsFileHit));

        // 非递归目录面只命中直接子文件
        var topLevelOnly = new RetrievalFilter(
            directoryPath: "Source/PuddingRuntime",
            recurse: false);
        Assert.IsFalse(
            topLevelOnly.Matches(classHit),
            "recurse=false ⇒ 深层文件必须被排除");

        // ④ 置信度下限面：只要语义级 ⇒ 词法猜测被排除
        var semanticOnly = new RetrievalFilter(confidenceFloor: RetrievalConfidence.Semantic);
        Assert.IsTrue(semanticOnly.Matches(classHit));
        Assert.IsFalse(semanticOnly.Matches(constructorHit));

        // ⑤ 命中层面
        var declarationOnly = new RetrievalFilter(hitKinds: [RetrievalHitKind.Declaration]);
        Assert.IsTrue(declarationOnly.Matches(classHit));
        Assert.IsFalse(declarationOnly.Matches(constructorHit));

        // ⑥ 空洞阀门：必然空 / 无意义的面不可表示
        Assert.ThrowsExactly<ArgumentException>(
            () => new RetrievalFilter(matchTargets: RetrievalMatchTarget.None),
            "匹配域 None 必然返回空 ⇒ 拒绝");
        Assert.ThrowsExactly<ArgumentException>(
            () => new RetrievalFilter(fileExtensions: ["   "]),
            "空扩展名 ⇒ 拒绝");
        Assert.ThrowsExactly<ArgumentException>(
            () => new RetrievalFilter(recurse: false),
            "关递归却不给目录 ⇒ 无意义 ⇒ 拒绝");
        Assert.ThrowsExactly<ArgumentException>(
            () => new RetrievalFilter(directoryPath: "   "),
            "空目录面 ⇒ 拒绝");

        // ⑦ 规范化 + 确定性排序 + 回显
        var normalized = new RetrievalFilter(fileExtensions: ["cs", ".TSX", "Cs"]);
        CollectionAssert.AreEqual(new[] { ".cs", ".tsx" }, normalized.FileExtensions.ToArray());
        Assert.IsTrue(normalized.HasAnyConstraint);
        Assert.IsFalse(RetrievalFilter.None.HasAnyConstraint);
        StringAssert.Contains(normalized.Describe(), "ext:.cs,.tsx");
        Assert.AreEqual("no-filter", RetrievalFilter.None.Describe());
        StringAssert.Contains(messagingOnly.Describe(), "dir:Source/PuddingRuntime/Services/Messaging(recursive)");
        StringAssert.Contains(symbolNameOnly.Describe(), "match:SymbolName");

        // ⑧ 生效面顺序成文（匹配域最前，目录最后）——"放宽建议"依赖它
        CollectionAssert.AreEqual(
            new[] { RetrievalFacet.MatchTarget, RetrievalFacet.SymbolKind, RetrievalFacet.FileExtension, RetrievalFacet.Directory },
            new RetrievalFilter(
                directoryPath: "Source",
                fileExtensions: [".cs"],
                matchTargets: RetrievalMatchTarget.SymbolName,
                symbolKinds: [CodeSymbolKind.Class]).ConstrainedFacets().ToArray());
    }
}
