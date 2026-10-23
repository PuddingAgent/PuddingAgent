using PuddingHtml;

namespace PuddingHtmlTests;

[TestClass]
public sealed class HtmlToMarkdownConverterTests
{
    private readonly HtmlToMarkdownConverter _converter = new();

    [TestMethod]
    public void Converts_Headings_To_Atx_Form()
    {
        var markdown = _converter.Convert("<h1>Title</h1><h2>Section</h2>");

        StringAssert.Contains(markdown, "# Title");
        StringAssert.Contains(markdown, "## Section");
    }

    [TestMethod]
    public void Converts_Links_And_Emphasis()
    {
        var markdown = _converter.Convert("<p><strong>bold</strong> and <a href=\"https://example.com/x\">link</a></p>");

        StringAssert.Contains(markdown, "**bold**");
        StringAssert.Contains(markdown, "[link](https://example.com/x)");
    }

    [TestMethod]
    public void Converts_Lists_To_Dash_Bullets()
    {
        var markdown = _converter.Convert("<ul><li>one</li><li>two</li></ul>");

        StringAssert.Contains(markdown, "- one");
        StringAssert.Contains(markdown, "- two");
    }

    [TestMethod]
    public void Drops_Html_Comments()
    {
        var markdown = _converter.Convert("<p>before<!-- hidden note -->after</p>");

        Assert.IsFalse(markdown.Contains("hidden note", StringComparison.Ordinal), markdown);
        StringAssert.Contains(markdown, "before");
        StringAssert.Contains(markdown, "after");
    }

    [TestMethod]
    public void Preserves_Fenced_Code_Blocks()
    {
        var markdown = _converter.Convert("<pre><code>var x = 1;\nvar y = 2;</code></pre>");

        StringAssert.Contains(markdown, "```");
        StringAssert.Contains(markdown, "var x = 1;");
    }

    [TestMethod]
    public void Returns_Empty_For_Empty_Input()
    {
        Assert.AreEqual(string.Empty, _converter.Convert(string.Empty).Trim());
    }
}
