using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A19：命名哈希单一真源 <see cref="FullTextIndexPaths"/> 的**组件侧**直接断言（A3 + A1 的组件部分）。
/// <para>
/// 冻结的 64 位小写 hex 由 pwsh + BCL **独立算出**（<c>temp/a19-evidence/pwsh-oracle.txt</c>），
/// 不是由被测代码生成 —— 因此这里不是"算给自己看"的同义反复。
/// </para>
/// <para>
/// ⚠️ 本用例只做字符串与只读探针，索引根一律落在 <see cref="Path.GetTempPath"/> 之下，绝不写生产索引根。
/// </para>
/// </summary>
[TestClass]
public sealed class FullTextIndexPathsTests
{
    /// <summary><c>C:\Corpus\Alpha</c> 及其 5 种等价写法（大写小写 / 尾随 <c>\</c> / 尾随 <c>/</c> / 含 <c>..</c>）的冻结哈希。</summary>
    private const string FrozenAlphaHash = "6de429b30f1bfcb431bb1e7067b1c1539a8ed10792883161c67155366184556f";

    /// <summary><c>C:\语料 根\子目录 一</c>（含空格与中文）的冻结哈希。</summary>
    private const string FrozenCjkHash = "3c7545fa1b60342b982c656ddcf0a77379ae33a07d5eef338ca2fe6b9ce89ff6";

    /// <summary><c>C:\</c> 的冻结哈希 —— 输入是 <c>C:</c>（<b>不</b>保住盘根，引擎既有语义）。</summary>
    private const string FrozenDriveRootHash = "82179da1705476f753fdbb9fb58fef251195a42a5d468128266e12228af7e75b";

    /// <summary>6 类边界输入（任务书 §3 R4）。</summary>
    private static readonly (string Label, string CorpusRoot, string ExpectedHash)[] BoundaryCases =
    {
        ("①普通路径", @"C:\Corpus\Alpha", FrozenAlphaHash),
        ("②大小写混合", @"c:\corpus\alpha", FrozenAlphaHash),
        ("③尾随反斜杠", @"C:\Corpus\Alpha\", FrozenAlphaHash),
        ("④尾随正斜杠", @"C:\Corpus\Alpha/", FrozenAlphaHash),
        ("⑤含 .. 相对段", @"C:\Corpus\Nested\..\Alpha", FrozenAlphaHash),
        ("⑥含空格与中文", @"C:\语料 根\子目录 一", FrozenCjkHash),
    };

    /// <summary>A3：<c>TrimEnd</c>（两个分隔符）与 <c>ToUpperInvariant</c> 被**直接**断言，不靠集成结果间接推。</summary>
    [TestMethod]
    public void A3_NormalizeCorpusRoot_Trims_Trailing_Separators_And_Uppercases()
    {
        // TrimEnd：尾随 \ 与尾随 / 都不许留下（否则哈希与引擎不一致 = 既有索引凭空消失）
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\Alpha\"));
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\Alpha/"));
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\Alpha\\"));
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\Alpha\/"));
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\Alpha"));

        // ToUpperInvariant：不变文化大写（这是**哈希输入**的口径，与 scopeKey 的小写口径不同）
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"c:\corpus\alpha"));

        // GetFullPath：绝对化 + 解析 .. 段
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\Nested\..\Alpha"));
        Assert.AreEqual(@"C:\CORPUS\ALPHA", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\.\Alpha"));

        // 空格与中文不被改写（只大写 ASCII）
        Assert.AreEqual(@"C:\语料 根\子目录 一", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\语料 根\子目录 一"));
    }

    /// <summary>
    /// A3（反面 · 防合并）：<c>NormalizeCorpusRoot</c> <b>不</b>保住盘根 —— 与
    /// <c>SupplyScopeNormalizer.TrimTrailingSeparators</c>（保住 <c>C:\</c>）<b>刻意不同</b>。
    /// 本断言的作用是拦住「顺手把两套口径合并」的改动。
    /// </summary>
    [TestMethod]
    public void A3_NormalizeCorpusRoot_Does_Not_Preserve_The_Drive_Root()
    {
        Assert.AreEqual("C:", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\"));
        Assert.AreEqual("C:", FullTextIndexPaths.NormalizeCorpusRoot(@"C:/"));
        Assert.AreEqual(@"C:\CORPUS", FullTextIndexPaths.NormalizeCorpusRoot(@"C:\Corpus\"));
    }

    /// <summary>
    /// A1（组件侧）：6 类边界输入下，组件 helper 解析出的目录 == 引擎
    /// <see cref="IFullTextIndexRootedEngine.ResolveIndexDirectory"/> == <c>Path.Combine(indexRoot, 冻结 hex)</c>。
    /// </summary>
    [TestMethod]
    public void A1_ResolveIndexDirectory_Equals_The_Engine_And_The_Frozen_Hashes()
    {
        var indexRoot = Path.Combine(Path.GetTempPath(), "pudding-fts-a19-index-root");
        Assert.IsTrue(
            indexRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"组件用例的索引根必须在系统临时目录下：{indexRoot}");
        Assert.IsFalse(indexRoot.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase), indexRoot);

        var options = new FullTextIndexOptions { IndexRootDirectory = indexRoot };
        using var engine = new LuceneSearchEngine(options);
        var rooted = (IFullTextIndexRootedEngine)engine;

        foreach (var (label, corpusRoot, expectedHash) in BoundaryCases)
        {
            var expected = Path.Combine(indexRoot, expectedHash);

            var helper = FullTextIndexPaths.ResolveIndexDirectory(indexRoot, corpusRoot);
            var viaEngine = rooted.ResolveIndexDirectory(corpusRoot);

            Assert.AreEqual(expected, helper, $"组件 helper 与冻结哈希不一致（{label}）：{corpusRoot}");
            Assert.AreEqual(expected, viaEngine, $"引擎 ResolveIndexDirectory 与冻结哈希不一致（{label}）：{corpusRoot}");
            Assert.AreEqual(64, Path.GetFileName(helper).Length, $"目录名必须是 64 位 hex（{label}）");
        }

        // C:\ 的哈希输入是 C:（不保住盘根），冻结下来防"顺手统一"
        Assert.AreEqual(Path.Combine(indexRoot, FrozenDriveRootHash), rooted.ResolveIndexDirectory(@"C:\"));

        // 反向对照：不同语料根必须给出不同目录（否则"都相等"可能只是函数恒定）
        Assert.AreNotEqual(
            FullTextIndexPaths.ResolveIndexDirectory(indexRoot, @"C:\Corpus\Alpha"),
            FullTextIndexPaths.ResolveIndexDirectory(indexRoot, @"C:\Corpus\Beta"),
            "不同语料根必须解析到不同索引目录");
    }

    /// <summary>空 / 空白参数一律 fail-closed 抛出（不得悄悄算出某个目录名）。</summary>
    [TestMethod]
    public void ResolveIndexDirectory_Rejects_Blank_Arguments()
    {
        Assert.Throws<ArgumentNullException>(() => FullTextIndexPaths.NormalizeCorpusRoot(null!));
        Assert.Throws<ArgumentException>(() => FullTextIndexPaths.NormalizeCorpusRoot("   "));
        Assert.Throws<ArgumentNullException>(() => FullTextIndexPaths.ResolveIndexDirectory(null!, @"C:\Corpus"));
        Assert.Throws<ArgumentException>(() => FullTextIndexPaths.ResolveIndexDirectory("  ", @"C:\Corpus"));
        Assert.Throws<ArgumentException>(() => FullTextIndexPaths.ResolveIndexDirectory(@"C:\index", "  "));
    }
}
