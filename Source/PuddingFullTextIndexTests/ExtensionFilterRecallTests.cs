using PuddingFullTextIndex;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndexTests;

/// <summary>
/// R1-01 / P1-01 回归：扩展名与子目录过滤是<b>结果后过滤</b>，因此检索窗口必须能越过
/// 「高排名命中全被过滤掉」的情形。
/// <para>
/// 固定倍数窗口（原实现 <c>maxResults * 5</c>）在噪声占满 Top-K 时会让过滤后的结果集<b>空</b>，
/// 而正确结果确实存在 —— 这正是把「漏检」报成「不存在」的错误否定。
/// </para>
/// </summary>
[TestClass]
public sealed class ExtensionFilterRecallTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pfi-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言。
        }
    }

    /// <summary>
    /// 1 个噪声文件含 200 行命中（每行 1 次，与目标词频相同 ⇒ 同分按 docId 排序，
    /// 噪声先入索引），1 个目标 cs 只含 1 行。maxResults=1 ⇒ 原实现的窗口是 5，
    /// 前 5 条全来自噪声文件，扩展名过滤后为空，而目标确实存在。
    /// </summary>
    private async Task<(LuceneSearchEngine Engine, string TargetPath)> BuildNoisyCorpusAsync()
    {
        var noise = Path.Combine(_root, "noise.txt");
        File.WriteAllLines(noise, Enumerable.Repeat("needle", 200));

        var target = Path.Combine(_root, "target.cs");
        File.WriteAllText(target, "needle");

        var engine = new LuceneSearchEngine(new FullTextIndexOptions());
        var built = await engine.BuildIndexAsync(_root);
        Assert.IsTrue(built.Success, built.Error);

        return (engine, target);
    }

    [TestMethod]
    public async Task ExtensionFilter_StillReturnsTheOnlyMatchingFile_WhenNoiseFillsTheTopK()
    {
        var (engine, target) = await BuildNoisyCorpusAsync();

        var result = await engine.SearchAsync(
            "needle", _root, maxResults: 1, fileExtensionFilter: ".cs");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreNotEqual(
            0,
            result.Matches.Count,
            "高排名命中全被扩展名过滤掉时不得返回空集 —— 会把漏检报成不存在。");
        Assert.IsTrue(
            result.Matches.All(m => m.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)),
            "返回项必须全部满足扩展名过滤。");
        Assert.AreEqual(target, result.Matches[0].FilePath);
    }

    /// <summary>正对照：同一语料不过滤时必须能返回结果，证明引擎本身健康。</summary>
    [TestMethod]
    public async Task UnfilteredSearch_OnTheSameCorpus_ReturnsHits()
    {
        var (engine, _) = await BuildNoisyCorpusAsync();

        var result = await engine.SearchAsync("needle", _root, maxResults: 2);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.Matches.Count);
    }

    /// <summary>子目录过滤与扩展名过滤叠加时同样不得漏检。</summary>
    [TestMethod]
    public async Task SubDirectoryFilter_AlsoSurvivesNoiseSaturation()
    {
        var (engine, target) = await BuildNoisyCorpusAsync();
        var nested = Path.Combine(_root, "src");
        Directory.CreateDirectory(nested);
        var nestedPath = Path.Combine(nested, "deep.cs");
        File.WriteAllText(nestedPath, "needle");
        Assert.IsTrue((await engine.BuildIndexAsync(_root)).Success);

        var result = await engine.SearchAsync(
            "needle", _root, maxResults: 2, fileExtensionFilter: ".cs", subDirectoryFilter: "src");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreNotEqual(0, result.Matches.Count, "子目录过滤叠加时同样不得返回空集。");
        Assert.AreEqual(nestedPath, result.Matches[0].FilePath);
        _ = target;
    }
}
