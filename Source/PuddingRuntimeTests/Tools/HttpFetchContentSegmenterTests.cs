using System.Text;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class HttpFetchContentSegmenterTests
{
    [TestMethod]
    public void Short_Content_Returns_In_A_Single_Page()
    {
        var page = HttpFetchContentSegmenter.Slice("short body", 0, 100);

        Assert.AreEqual("short body", page.Text);
        Assert.AreEqual(0, page.StartOffset);
        Assert.AreEqual(10, page.NextOffset);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public void Page_Breaks_On_A_Heading_Boundary()
    {
        var content = "# Intro\n" + new string('a', 200) + "\n# Next\n" + new string('b', 200);

        var page = HttpFetchContentSegmenter.Slice(content, 0, 260);

        Assert.IsTrue(page.HasMore);
        Assert.IsFalse(page.Text.Contains("# Next", StringComparison.Ordinal), "page must not split a heading off its body");
        Assert.IsTrue(content.AsSpan(page.NextOffset).StartsWith("# Next"), "next page must start at the heading");
    }

    /// <summary>
    /// The strongest invariant: paging must never drop or duplicate a character.
    /// </summary>
    [TestMethod]
    public void Every_Page_Concatenates_Back_Into_The_Original()
    {
        var content = BuildLongDocument();
        var rebuilt = new StringBuilder();
        var offset = 0;
        var pages = 0;

        while (true)
        {
            var page = HttpFetchContentSegmenter.Slice(content, offset, 120);
            rebuilt.Append(page.Text);
            pages++;

            if (!page.HasMore)
                break;

            Assert.IsTrue(page.NextOffset > offset, "cursor must always advance");
            offset = page.NextOffset;
            Assert.IsTrue(pages < 200, "segmentation must terminate");
        }

        Assert.AreEqual(content, rebuilt.ToString());
        Assert.IsTrue(pages > 1, $"expected paging, got {pages} page(s)");
    }

    [TestMethod]
    public void Offset_Past_The_End_Returns_An_Empty_Terminal_Page()
    {
        var page = HttpFetchContentSegmenter.Slice("abc", 99, 10);

        Assert.AreEqual(string.Empty, page.Text);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public void Outline_Lists_Heading_Levels()
    {
        var content = "# Title\nbody\n## Section A\nmore\n### Deep\nend\n## Section B\n";

        var outline = HttpFetchContentSegmenter.BuildOutline(content);

        StringAssert.Contains(outline, "- Title");
        StringAssert.Contains(outline, "  - Section A");
        StringAssert.Contains(outline, "    - Deep");
        StringAssert.Contains(outline, "  - Section B");
    }

    [TestMethod]
    public void Outline_Reports_When_There_Are_No_Headings()
    {
        var outline = HttpFetchContentSegmenter.BuildOutline("just a paragraph, no headings here");

        StringAssert.Contains(outline, "no markdown headings");
    }

    [TestMethod]
    public void Outline_Is_Capped()
    {
        var content = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"## H{i}"));

        var outline = HttpFetchContentSegmenter.BuildOutline(content, maxHeadings: 5);

        StringAssert.Contains(outline, "outline truncated");
        Assert.IsFalse(outline.Contains("## H19", StringComparison.Ordinal));
    }

    /// <summary>Headings need a space after the hashes; <c>#hashtag</c> is not a heading.</summary>
    [TestMethod]
    public void Hash_Without_A_Space_Is_Not_A_Heading()
    {
        var outline = HttpFetchContentSegmenter.BuildOutline("#not-a-heading\n## real heading");

        StringAssert.Contains(outline, "real heading");
        Assert.IsFalse(outline.Contains("not-a-heading", StringComparison.Ordinal));
    }

    private static string BuildLongDocument()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            sb.Append("## Section ").Append(i).AppendLine();
            sb.AppendLine(new string('x', 150));
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
