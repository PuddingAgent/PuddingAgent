using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **G5 的关键数字：一次 extract 拿到前 10 条标题、完整序列化 UTF-8 ≤ 1536 字节**。
///
/// 这里用 TestSite 的 **真实 SERP fixture** 生成块（正则把标题/链接抽出来，模拟驱动会观察到的语义块），
/// 再喂给真实的阅读管线（<see cref="ArticleCompactJson"/>）。因此这条用例验证的是**预算承诺本身**：
/// fixture 若变长、或管线对块的处理方式变了，1536 这个数字就会对不上。
///
/// **诚实边界**：测试侧的"抽块"是**替身**（真机上由驱动做）。它不验证驱动的抽取质量，
/// 只验证"给定这些块，管线能否在预算内把 10 条标题交付完"。
/// </summary>
[TestClass]
public sealed class SerpBudgetFixtureTests
{
    /// <summary>G5 的预算（设计方案：完整序列化 UTF-8 ≤ 1536 字节）。</summary>
    private const int G5BudgetBytes = 1_536;

    private static string FixturePath(string fileName) =>
        Path.Combine(RepoRoot(), "Tests", "PuddingBrowser.TestSite", "fixtures", fileName);

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    /// <summary>把 SERP fixture 抽成驱动会给出的语义块（标题 + 结果链接）。</summary>
    private static string BlocksFromSerpFixture(string fileName)
    {
        var html = File.ReadAllText(FixturePath(fileName));

        var titles = Regex.Matches(html, @"<h3 class=""title"">([^<]+)</h3>")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        var links = Regex.Matches(html, @"<h3 class=""title"">[^<]+</h3><a href=""([^""]+)"">([^<]+)</a>")
            .Select(match => (Href: match.Groups[1].Value, Text: match.Groups[2].Value))
            .ToArray();

        Assert.AreEqual(10, titles.Length, $"{fileName} 应有 10 条结果");
        Assert.AreEqual(10, links.Length, $"{fileName} 的每条结果都应带链接");

        var lines = new List<string>();
        lines.Add("""{"k":"h","t":"测试搜索结果","d":1}""");
        for (var index = 0; index < titles.Length; index++)
        {
            // 驱动给块时的形状：标题（h3）+ 结果链接（a）。这里用 JSON 行拼接（与 ContentBlocks 同形）。
            lines.Add($"{{\"k\":\"h\",\"t\":\"{titles[index]}\",\"d\":3}}");
            lines.Add($"{{\"k\":\"a\",\"t\":\"{links[index].Text}\",\"href\":\"{links[index].Href}\"}}");
        }

        return string.Join('\n', lines);
    }

    [TestMethod]
    public void ChineseSerpFitsTheBudgetOrPagesExplicitly()
    {
        var blocks = BlocksFromSerpFixture("serp-zh.html");
        var titles = Regex.Matches(File.ReadAllText(FixturePath("serp-zh.html")), @"<h3 class=""title"">([^<]+)</h3>")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.AreEqual(10, titles.Length);

        var article = ArticleCompactJson.Compose(blocks, G5BudgetBytes, cursor: 0);

        // 硬约束（无论装不装得下都必须成立）：输出 ≤ 预算，且**要么给全、要么明确标注截断并给游标**。
        Assert.IsTrue(
            article.Bytes <= G5BudgetBytes,
            $"中文 SERP 超出 G5 预算：{article.Bytes} > {G5BudgetBytes} 字节");
        Assert.IsTrue(
            article.Truncated ? article.NextCursor is not null : article.NextCursor is null,
            "截断必须带续读游标；未截断就不该给游标（不能两者矛盾）");

        // 实测（本轮发现，见 changelog）：以驱动当前给出的"标题+链接两块/结果"粒度，
        // 中文 SERP 在 1536 字节预算内**装不下 10 条**（会分页）。
        // 因此这里断言的是"分页必须能续读到全部 10 条"，而不是"一次给全"。
        var all = CollectAllPages(blocks, G5BudgetBytes);
        foreach (var title in titles)
        {
            StringAssert.Contains(all, title);
        }
    }

    /// <summary>把 compact_json 输出里的 `t` 字段拼回来（JSON 会把非 ASCII 转义成 \uXXXX，直接比原文会误判）。</summary>
    private static string DecodedText(string compactJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(compactJson);
        if (!document.RootElement.TryGetProperty("blocks", out var blocks))
        {
            return string.Empty;
        }

        return string.Join(
            '\n',
            blocks.EnumerateArray()
                .Select(block => block.TryGetProperty("t", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String
                    ? text.GetString()
                    : null)
                .Where(text => !string.IsNullOrEmpty(text)));
    }

    /// <summary>逐页续读并拼合（上限 200 页，防止真出现死循环时把测试挂住）。</summary>
    private static string CollectAllPages(string blocks, int budget)
    {
        var pages = new List<string>();
        int? cursor = 0;

        for (var page = 0; cursor is { } current && page < 200; page++)
        {
            var composed = ArticleCompactJson.Compose(blocks, budget, current);
            Assert.IsTrue(composed.Bytes <= budget, $"第 {page + 1} 页超出预算：{composed.Bytes}");
            pages.Add(DecodedText(composed.Text));
            cursor = composed.NextCursor;
        }

        Assert.IsNull(cursor, "200 页内应当续读完毕（否则是分页不前进）");
        return string.Join('\n', pages);
    }

    [TestMethod]
    public void TheChineseSerpFitsInOneExtractAtTheDocumentedPageBudget()
    {
        // 实测：给足预算时中文 SERP 一次给全 10 条需要多少字节。
        // 这个数字与 G5 的 1536 对比，决定了"一次 extract 拿 10 条"是否成立（见 changelog 的发现）。
        var blocks = BlocksFromSerpFixture("serp-zh.html");
        var article = ArticleCompactJson.Compose(blocks, ArticleCompactJson.DefaultMaxBytes, cursor: 0);

        Assert.IsFalse(article.Truncated, "4096 字节预算内应当一次给全 10 条");
        Assert.IsNull(article.NextCursor);
        Assert.IsTrue(article.Bytes <= ArticleCompactJson.DefaultMaxBytes);

        // 记录实测值（供 changelog/设计对照）：远低于 4096，但**高于** 1536。
        Assert.IsTrue(article.Bytes > G5BudgetBytes,
            $"若实测已 ≤1536 字节，说明块粒度变轻了，应回头更新本条与运行手册（当前 {article.Bytes}）");
    }

    [TestMethod]
    public void EnglishSerpFitsTheG5BudgetInOneExtract()
    {
        var blocks = BlocksFromSerpFixture("serp-en.html");
        var article = ArticleCompactJson.Compose(blocks, G5BudgetBytes, cursor: 0);

        Assert.IsTrue(
            article.Bytes <= G5BudgetBytes,
            $"英文 SERP 超出 G5 预算：{article.Bytes} > {G5BudgetBytes} 字节");
        Assert.IsFalse(article.Truncated);
        Assert.IsNull(article.NextCursor);

        var titles = Regex.Matches(File.ReadAllText(FixturePath("serp-en.html")), @"<h3 class=""title"">([^<]+)</h3>")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        var decodedEnglish = DecodedText(article.Text);
        foreach (var title in titles)
        {
            StringAssert.Contains(decodedEnglish, title);
        }
    }

    [TestMethod]
    public void ATightBudgetPagesTheSameContentInsteadOfDroppingIt()
    {
        // 预算被压到很小 ⇒ 必须**分页**（游标续读），而不是静默丢内容；逐页拼回来后内容应完整。
        var blocks = BlocksFromSerpFixture("serp-zh.html");
        var titles = Regex.Matches(File.ReadAllText(FixturePath("serp-zh.html")), @"<h3 class=""title"">([^<]+)</h3>")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        var first = ArticleCompactJson.Compose(blocks, maxBytes: 200, cursor: 0);
        Assert.IsTrue(first.Truncated, "小预算下应当分页（否则内容被静默丢弃）");
        Assert.IsNotNull(first.NextCursor, "分页必须给续读游标");
        Assert.IsTrue(first.Bytes <= 200, $"第一页超出预算：{first.Bytes}");

        var joined = CollectAllPages(blocks, 200);
        foreach (var title in titles)
        {
            StringAssert.Contains(joined, title);
        }
    }

    [TestMethod]
    public void TheMarkdownRepresentationOfTheSameChineseSerpAlsoFitsTheBudget()
    {
        // 同一批块、同一预算，换一种表示：这决定了"G5 的 1536 字节"到底对哪种表示成立。
        var blocks = BlocksFromSerpFixture("serp-zh.html");
        var titles = Regex.Matches(File.ReadAllText(FixturePath("serp-zh.html")), @"<h3 class=""title"">([^<]+)</h3>")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        var markdown = ArticleMarkdown.Compose(blocks, G5BudgetBytes, cursor: 0);

        Assert.IsTrue(markdown.Bytes <= G5BudgetBytes, $"markdown 超出预算：{markdown.Bytes}");
        Assert.IsFalse(markdown.Truncated, "markdown 表示下应当一次给全 10 条（否则 1536 字节对两种表示都不成立）");
        Assert.IsNull(markdown.NextCursor);

        foreach (var title in titles)
        {
            StringAssert.Contains(markdown.Text, title);
        }
    }

    [TestMethod]
    public void TheBudgetNumberMatchesTheDesignDocument()
    {
        // 1536 不是测试里随手写的常量：它来自设计方案 §6.1（G5）。改这里必须同步改设计。
        var design = File.ReadAllText(Path.Combine(
            RepoRoot(), "Docs", "12_features", "浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md"));

        StringAssert.Contains(design, "1,536");
        Assert.AreEqual(1_536, G5BudgetBytes);
        Assert.AreEqual(1_536, Encoding.UTF8.GetByteCount(new string('x', 1_536)));
    }
}
