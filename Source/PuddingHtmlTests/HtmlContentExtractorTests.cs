using PuddingHtml;

namespace PuddingHtmlTests;

[TestClass]
public sealed class HtmlContentExtractorTests
{
    [TestMethod]
    public void ExtractReadableHtml_Removes_Scripts_Styles_And_Chrome()
    {
        const string html = """
            <html><body>
              <header>site header</header>
              <nav>menu</nav>
              <main><p>keep this</p><script>var x = 1;</script><style>p{}</style></main>
              <footer>site footer</footer>
            </body></html>
            """;

        var readable = HtmlContentExtractor.ExtractReadableHtml(html);

        StringAssert.Contains(readable, "keep this");
        Assert.IsFalse(readable.Contains("site header", StringComparison.Ordinal), readable);
        Assert.IsFalse(readable.Contains("menu", StringComparison.Ordinal), readable);
        Assert.IsFalse(readable.Contains("site footer", StringComparison.Ordinal), readable);
        Assert.IsFalse(readable.Contains("var x", StringComparison.Ordinal), readable);
        Assert.IsFalse(readable.Contains("p{}", StringComparison.Ordinal), readable);
    }

    [TestMethod]
    public void ExtractReadableHtml_Prefers_Main_Over_Article_Over_Body()
    {
        const string html = """
            <html><body>
              <div>outside</div>
              <article>article body</article>
              <main>main body</main>
            </body></html>
            """;

        var readable = HtmlContentExtractor.ExtractReadableHtml(html);

        StringAssert.Contains(readable, "main body");
        Assert.IsFalse(readable.Contains("article body", StringComparison.Ordinal), readable);
        Assert.IsFalse(readable.Contains("outside", StringComparison.Ordinal), readable);
    }

    [TestMethod]
    public void ExtractReadableHtml_Falls_Back_To_Body_When_No_Landmark_Exists()
    {
        const string html = "<html><body><div>only body content</div></body></html>";

        var readable = HtmlContentExtractor.ExtractReadableHtml(html);

        StringAssert.Contains(readable, "only body content");
    }

    [TestMethod]
    public void ExtractReadableHtml_Returns_Empty_For_Blank_Input()
    {
        Assert.AreEqual(string.Empty, HtmlContentExtractor.ExtractReadableHtml("   "));
    }

    [TestMethod]
    public void ToPlainText_Decodes_Entities_And_Collapses_Whitespace()
    {
        const string html = "<html><body><main><p>a &amp; b</p><p>c\n\n   d</p></main></body></html>";

        var text = HtmlContentExtractor.ToPlainText(html);

        StringAssert.Contains(text, "a & b");
        Assert.IsFalse(text.Contains("  ", StringComparison.Ordinal), $"whitespace not collapsed: {text}");
    }

    [TestMethod]
    [DataRow("text/html; charset=utf-8")]
    [DataRow("application/xhtml+xml")]
    public void IsHtml_Trusts_A_ContentType_That_Mentions_Html(string contentType)
    {
        Assert.IsTrue(HtmlContentExtractor.IsHtml(contentType, "plain body, no tags"));
    }

    [TestMethod]
    public void IsHtml_Rejects_A_Non_Html_ContentType_Without_Html_Markup()
    {
        Assert.IsFalse(HtmlContentExtractor.IsHtml("text/plain", "just text"));
        Assert.IsFalse(HtmlContentExtractor.IsHtml("application/json", "{\"a\":1}"));
    }

    [TestMethod]
    public void IsHtml_Detects_By_Doctype_Or_Root_Element_When_ContentType_Is_Missing()
    {
        Assert.IsTrue(HtmlContentExtractor.IsHtml(null, "<!DOCTYPE html><p>x</p>"));
        Assert.IsTrue(HtmlContentExtractor.IsHtml(null, "  <html><body>x</body></html>"));
        Assert.IsFalse(HtmlContentExtractor.IsHtml(null, "{\"a\":1}"));
        Assert.IsFalse(HtmlContentExtractor.IsHtml(string.Empty, "# markdown\nbody"));
    }
}
