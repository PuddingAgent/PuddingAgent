using System.Text;
using System.Text.RegularExpressions;

namespace DesktopServiceTests;

/// <summary>
/// TestSite 验收 fixture 的**结构契约**（G5/G6/G8 的观察对象）。
///
/// 为什么需要它：外部 smoke 清单里的 S-05/S-06/S-08 依赖这些 fixture 的**固定属性**
/// （10 条结果、标题长度固定、表格/代码/链接齐全、有嵌套滚动容器与有终点的无限滚动）。
/// fixture 一旦被随意改动，验收项就**悄悄变得无法执行** —— 这条测试把它变成会红的门禁。
/// </summary>
public sealed class TestSiteFixtureTests
{
    private static string FixturePath(string fileName) =>
        Path.Combine(RepoLayout.Root, "Tests", "PuddingBrowser.TestSite", "fixtures", fileName);

    [Fact]
    public void TheChineseSerpFixtureHasExactlyTenResultsWithFixedTitleLength()
    {
        var path = FixturePath("serp-zh.html");
        Assert.True(File.Exists(path), $"找不到 SERP fixture：{path}");

        var html = File.ReadAllText(path);

        var titles = Regex.Matches(html, @"<h3 class=""title"">([^<]+)</h3>")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        // G5：一次 extract 要能拿到前 10 条。
        Assert.Equal(10, titles.Length);

        // "标题长度明确固定" —— 否则"≤1536 字节"无法复核。
        var fixedLength = titles[0].Length;
        Assert.All(titles, title => Assert.Equal(fixedLength, title.Length));
        Assert.True(fixedLength > 0);

        // 每条结果都有链接（阅读管线要能取到来源 URL）。
        Assert.Equal(10, Regex.Matches(html, @"<a href=""https://example\.com/r\d+""").Count);
    }

    [Fact]
    public void TheEnglishSerpFixtureHasExactlyTenResultsWithFixedTitleLength()
    {
        // S-05 要求**中文与英文**两种 fixture（预算判定在两种文字下的成本不同）。
        var path = FixturePath("serp-en.html");
        Assert.True(File.Exists(path), $"找不到英文 SERP fixture：{path}");

        var html = File.ReadAllText(path);

        var titles = Regex.Matches(html, @"<h3 class=""title"">([^<]+)</h3>")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(10, titles.Length);

        var fixedLength = titles[0].Length;
        Assert.All(titles, title => Assert.Equal(fixedLength, title.Length));
        Assert.Equal(43, fixedLength);   // fixture 里写死的长度；改动 fixture 必须同步这里

        Assert.Equal(10, Regex.Matches(html, @"<a href=""https://example\.com/e\d+""").Count);

        // 中英两条 fixture 必须在同一站点上都能取到（S-05 的两次观察）。
        var program = File.ReadAllText(Path.Combine(
            RepoLayout.Root, "Tests", "PuddingBrowser.TestSite", "Program.cs"));
        Assert.Contains("/fixtures/serp-en", program, StringComparison.Ordinal);
        Assert.Contains("/fixtures/serp-zh", program, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArticleFixtureCoversHeadingListTableCodeAndLink()
    {
        var html = File.ReadAllText(FixturePath("article.html"));

        // G6：标题/列表/表格/代码/链接结构齐全，才谈得上"聚合后覆盖 100%、不重不漏"。
        Assert.Contains("<h1>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>", html, StringComparison.Ordinal);
        Assert.Contains("<ul>", html, StringComparison.Ordinal);
        Assert.True(Regex.Matches(html, @"<li>").Count >= 3);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=", html, StringComparison.Ordinal);

        // 长文要有"最后一段"，分页边界与游标连续性才有观察对象。
        Assert.Contains("最后一段", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScrollFixtureHasNestedContainerAndABoundedInfiniteList()
    {
        var html = File.ReadAllText(FixturePath("scroll.html"));

        // G8：① 嵌套可滚动容器（外层窗口不动、内层动）② 文档足够高 ③ 无限滚动有**确定终点**。
        Assert.Contains("overflow-y: scroll", html, StringComparison.Ordinal);
        Assert.Contains("height: 3000px", html, StringComparison.Ordinal);

        // 终点必须写死：不限批次的 fixture 会让"等新条目"永远等不到结论。
        Assert.Contains("appended >= 10", html, StringComparison.Ordinal);
        Assert.Contains("window.puddingAppendBatch", html, StringComparison.Ordinal);
    }

    [Fact]
    public void FixturesAreServedFromFilesAndCopiedToOutput()
    {
        // 站点按文件读（Program.cs），项目文件必须把 fixture 复制到输出目录，否则运行期 404。
        var program = File.ReadAllText(Path.Combine(
            RepoLayout.Root, "Tests", "PuddingBrowser.TestSite", "Program.cs"));
        Assert.Contains("/fixtures/serp-zh", program, StringComparison.Ordinal);
        Assert.Contains("/fixtures/article", program, StringComparison.Ordinal);
        Assert.Contains("/fixtures/scroll", program, StringComparison.Ordinal);

        var project = File.ReadAllText(Path.Combine(
            RepoLayout.Root, "Tests", "PuddingBrowser.TestSite", "PuddingBrowser.TestSite.csproj"));
        Assert.Contains("fixtures/**/*.html", project, StringComparison.Ordinal);
        Assert.Contains("CopyToOutputDirectory", project, StringComparison.Ordinal);
    }

    [Fact]
    public void FixtureFilesAreUtf8AndReasonablySmall()
    {
        foreach (var fileName in new[] { "serp-zh.html", "serp-en.html", "article.html", "scroll.html" })
        {
            var bytes = File.ReadAllBytes(FixturePath(fileName));

            // 中文 fixture 必须是 UTF-8（站点用 charset=utf-8 声明）；带 BOM 会让首行比较出意外。
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("<!DOCTYPE html>", text, StringComparison.Ordinal);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{fileName} 带 BOM；fixture 一律不带 BOM");

            // fixture 要小到可以随仓库走。
            Assert.True(bytes.Length < 16 * 1024, $"{fileName} 太大：{bytes.Length} 字节");
        }
    }
}
