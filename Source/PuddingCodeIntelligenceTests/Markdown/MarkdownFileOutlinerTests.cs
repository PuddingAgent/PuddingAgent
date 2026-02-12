using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Markdown;

namespace PuddingCodeIntelligenceTests.Markdown;

[TestClass]
public class MarkdownFileOutlinerTests
{
    private readonly MarkdownFileOutliner _outliner = new();

    [TestMethod]
    public async Task OutlineAsync_SupportedExtensions_ReturnsMdMdx()
    {
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".md"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".mdx"));
    }

    [TestMethod]
    public async Task OutlineAsync_ATXHeadings_Extracted()
    {
        var source = """
            # Title

            ## Section 1

            Some content.

            ### Subsection 1.1

            More content.

            ## Section 2

            Final content.
            """;

        var result = await _outliner.OutlineAsync("test.md", source);

        Assert.IsTrue(result.Success);
        // Should have 4 headings: Title, Section 1, Subsection 1.1, Section 2
        Assert.AreEqual(4, result.Nodes.Count);

        Assert.AreEqual("Title", result.Nodes[0].Name);
        Assert.AreEqual(CodeSymbolKind.Namespace, result.Nodes[0].Kind); // H1

        Assert.AreEqual("Section 1", result.Nodes[1].Name);
        Assert.AreEqual(CodeSymbolKind.Class, result.Nodes[1].Kind); // H2
    }

    [TestMethod]
    public async Task OutlineAsync_FrontMatter_Extracted()
    {
        var source = """
            ---
            title: Test Document
            author: Test
            ---

            # Content

            Hello world.
            """;

        var result = await _outliner.OutlineAsync("test.md", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Count >= 2); // front-matter + heading
        Assert.AreEqual("front-matter", result.Nodes[0].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_CodeBlocksIgnored()
    {
        var source = """
            # Real Heading

            ```markdown
            # Not a Heading
            ## Also Not a Heading
            ```

            ## Another Real Heading
            """;

        var result = await _outliner.OutlineAsync("test.md", source);

        Assert.IsTrue(result.Success);
        var headings = result.Nodes.Where(n => n.Name != "front-matter").ToList();
        Assert.AreEqual(2, headings.Count);
        Assert.AreEqual("Real Heading", headings[0].Name);
        Assert.AreEqual("Another Real Heading", headings[1].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_Hierarchy_NestedCorrectly()
    {
        var source = """
            # Chapter 1

            ## Section 1.1

            ### Detail 1.1.1

            ## Section 1.2

            # Chapter 2

            ## Section 2.1
            """;

        var result = await _outliner.OutlineAsync("test.md", source);

        Assert.IsTrue(result.Success);
        // Top-level should be H1s
        var h1Nodes = result.Nodes.Where(n => n.Modifiers == "h1").ToList();
        Assert.AreEqual(2, h1Nodes.Count);
    }

    [TestMethod]
    public async Task OutlineAsync_SetextHeadings_Extracted()
    {
        var source = """
            Main Title
            ==========

            Subtitle
            --------

            Content here.
            """;

        var result = await _outliner.OutlineAsync("test.md", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Count >= 2);
    }

    [TestMethod]
    public async Task OutlineAsync_EmptySource_ReturnsEmpty()
    {
        var result = await _outliner.OutlineAsync("empty.md", "");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Nodes.Count);
    }

    [TestMethod]
    public async Task OutlineAsync_ComplexDocument()
    {
        var source = """
            ---
            title: API Reference
            ---

            # API Reference

            ## Authentication

            ### OAuth 2.0

            Details about OAuth...

            ### API Keys

            Details about API keys...

            ## Endpoints

            ### GET /users

            Returns a list of users.

            ### POST /users

            Creates a new user.

            ## Error Codes

            | Code | Description |
            |------|-------------|
            | 400  | Bad Request |
            """;

        var result = await _outliner.OutlineAsync("api.md", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Count >= 8); // front-matter + 7 headings
    }
}
