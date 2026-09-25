using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S1b：<c>.last_indexed</c> 的**协议形状**与**写读往返**（任务书 §2.3b）。
/// <para>
/// 本用例走的是<b>真实引擎</b>（<see cref="LuceneSearchEngine.BuildIndexAsync"/> 写、引擎自身的
/// <c>ReadLastIndexedAsync</c> 读），因为指纹的唯一价值就是"让引擎下次判定 patterns 没变"——
/// 只在 helper 上做单元断言抓不到"写进去的 <c>p</c> 没人读"这类断链。
/// </para>
/// <para>
/// <b>行为观测点</b>：<see cref="FullTextIndexResult.IndexedFileCount"/>。同一 patterns 的第二次构建
/// 必须走增量 ⇒ <c>0</c>；换 patterns ⇒ 指纹不等 ⇒ 全量重建 ⇒ 文件被<b>重新</b>索引。
/// 后者是**对照组**：没有它，"0"可能只是"增量恒为真"。
/// </para>
/// <para>
/// ⚠️ 索引根一律落在 <see cref="Path.GetTempPath"/> 之下（R8）：绝不写生产索引根 <c>D:\data\fulltext-index</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class LastIndexedStampRoundTripTests
{
    /// <summary>父级冻结值（§2.3a）：<c>"*.md"</c> 的指纹。</summary>
    private const string MdFingerprint = "ba405b6cd142";

    /// <summary>父级冻结值（§2.3a）：<c>"*.cs;*.md"</c> 的指纹。</summary>
    private const string CsMdFingerprint = "76d435e433a6";

    /// <summary>协议形状：字段名与顺序 <c>{"t":…,"p":…}</c>、<c>t</c> 为 UTC ISO、<c>p</c> 为 12 位小写 hex。</summary>
    private static readonly Regex StampShape =
        new("^\\{\"t\":\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(\\.\\d+)?Z\",\"p\":\"[0-9a-f]{12}\"\\}$");

    private string _root = null!;
    private string _corpus = null!;
    private LuceneSearchEngine _engine = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-s1b-" + Guid.NewGuid().ToString("N"));
        _corpus = Path.Combine(_root, "corpus");
        Directory.CreateDirectory(_corpus);

        _engine = new LuceneSearchEngine(new FullTextIndexOptions
        {
            IndexRootDirectory = Path.Combine(_root, "index"),
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _engine.Dispose();

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应把测试判红
        }
    }

    /// <summary>
    /// 写 → 形状 → 读回 → 引擎据 <c>p</c> 判定增量 → 换 patterns 触发全量（对照）。
    /// </summary>
    [TestMethod]
    public async Task S1b_LastIndexed_Shape_And_RoundTrip_Feed_The_Incremental_Decision()
    {
        Write("a.md", "alpha needle");
        Write("b.md", "beta needle");

        var rooted = (IFullTextIndexRootedEngine)_engine;
        var indexDir = rooted.ResolveIndexDirectory(_corpus);
        var stampPath = Path.Combine(indexDir, ".last_indexed");

        // R8：本用例的索引目录必须落在系统 Temp 下，绝不碰生产索引根
        Assert.IsTrue(
            indexDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"索引目录必须在系统临时目录下：{indexDir}");
        Assert.IsFalse(indexDir.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase), indexDir);

        // ── ① 首次构建（无既有索引 ⇒ 全量）⇒ 写出 .last_indexed ──
        var first = await _engine.BuildIndexAsync(_corpus, "*.md");
        Assert.IsTrue(first.Success, first.Error);
        Assert.AreEqual(2, first.IndexedFileCount);
        Assert.IsTrue(File.Exists(stampPath), $"首次构建必须写出 {stampPath}");

        var firstText = File.ReadAllText(stampPath);
        AssertStampShape(firstText);
        Assert.AreEqual(
            MdFingerprint,
            ReadStamp(stampPath).PatternHash,
            "写出的 p 必须是父级冻结值（与生产实际索引同源）");
        Assert.AreEqual(
            FullTextPolicyFingerprint.ComputePatternFingerprint("*.md"),
            ReadStamp(stampPath).PatternHash,
            "引擎写入的 p 必须来自单一真源 helper（本断言锁定的是耦合关系，值正确性由上面的冻结值负责）");

        // 写后读回：同一 indexDir 重复读取得到相同的 (t, p)（文件不再被重写）
        var (t1, p1) = ReadStamp(stampPath);
        Assert.AreEqual(firstText, File.ReadAllText(stampPath), "读操作不得改变 .last_indexed 的内容");
        Assert.AreEqual(p1, ReadStamp(stampPath).PatternHash);
        Assert.AreEqual(t1, ReadStamp(stampPath).Timestamp);

        // ── ② 读回路径：同 patterns ⇒ 指纹相等 + 未过期 ⇒ 增量 ⇒ 0 个文件被索引 ──
        var second = await _engine.BuildIndexAsync(_corpus, "*.md");
        Assert.IsTrue(second.Success, second.Error);
        Assert.AreEqual(
            0,
            second.IndexedFileCount,
            "同 patterns 的第二次构建必须是增量：这证明引擎读回了 .last_indexed.p 并判定相等");

        var afterSecond = File.ReadAllText(stampPath);
        AssertStampShape(afterSecond);
        var (t2, p2) = ReadStamp(stampPath);
        Assert.AreEqual(p1, p2, "重写后 p 必须逐位不变（patterns 未变）");
        Assert.IsTrue(t2 >= t1, $"t 必须前进（写入时机不变）：{t1:O} → {t2:O}");

        // ── ③ 对照组（防「恒为增量」假绿）：换 patterns ⇒ 指纹不等 ⇒ 全量重建 ⇒ 文件被重新索引 ──
        var third = await _engine.BuildIndexAsync(_corpus, "*.cs;*.md");
        Assert.IsTrue(third.Success, third.Error);
        Assert.AreEqual(
            2,
            third.IndexedFileCount,
            "patterns 变了必须全量重建（若这里也是 0，则步骤②的 0 不具备证明力）");

        var (_, p3) = ReadStamp(stampPath);
        Assert.AreEqual(CsMdFingerprint, p3, "换 patterns 后写出的 p 必须等于冻结值");

        // ── ④ 再读回：回到增量（新指纹同样能被读回并比对）──
        var fourth = await _engine.BuildIndexAsync(_corpus, "*.cs;*.md");
        Assert.IsTrue(fourth.Success, fourth.Error);
        Assert.AreEqual(0, fourth.IndexedFileCount, "同一 patterns 的再次构建必须回到增量路径");
        Assert.AreEqual(CsMdFingerprint, ReadStamp(stampPath).PatternHash);
    }

    private static void AssertStampShape(string json)
    {
        StringAssert.Matches(
            json,
            StampShape,
            $"`.last_indexed` 协议形状被改动了（字段名/顺序/t 序列化/p 形状）：{json}");

        using var doc = JsonDocument.Parse(json);
        CollectionAssert.AreEqual(
            new[] { "t", "p" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray(),
            "字段名与顺序必须冻结为 t, p");
        Assert.AreEqual(2, doc.RootElement.EnumerateObject().Count(), "不得多写字段");
    }

    private static (DateTime Timestamp, string PatternHash) ReadStamp(string stampPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(stampPath));
        return (
            doc.RootElement.GetProperty("t").GetDateTime(),
            doc.RootElement.GetProperty("p").GetString() ?? string.Empty);
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_corpus, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
