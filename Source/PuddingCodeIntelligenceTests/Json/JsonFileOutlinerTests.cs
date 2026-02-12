using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Json;

namespace PuddingCodeIntelligenceTests.Json;

[TestClass]
public class JsonFileOutlinerTests
{
    private readonly JsonFileOutliner _outliner = new();

    [TestMethod]
    public async Task OutlineAsync_SupportedExtensions_ReturnsJson()
    {
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".json"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".jsonc"));
    }

    [TestMethod]
    public async Task OutlineAsync_SimpleObject_TopLevelKeysExtracted()
    {
        var source = """
            {
                "name": "my-app",
                "version": "1.0.0",
                "private": true
            }
            """;

        var result = await _outliner.OutlineAsync("package.json", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.Nodes.Count);

        Assert.AreEqual("name", result.Nodes[0].Name);
        Assert.AreEqual(CodeSymbolKind.Property, result.Nodes[0].Kind);

        Assert.AreEqual("version", result.Nodes[1].Name);
        Assert.AreEqual("private", result.Nodes[2].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_NestedObjects_KeysExtractedWithContainer()
    {
        var source = """
            {
                "compilerOptions": {
                    "target": "ES2020",
                    "module": "commonjs",
                    "strict": true
                },
                "include": ["src"]
            }
            """;

        var result = await _outliner.OutlineAsync("tsconfig.json", source);

        Assert.IsTrue(result.Success);
        var names = result.Nodes.Select(n => n.Name).ToList();
        Assert.IsTrue(names.Contains("compilerOptions"));
        Assert.IsTrue(names.Contains("target"));
        Assert.IsTrue(names.Contains("module"));
        Assert.IsTrue(names.Contains("strict"));
        Assert.IsTrue(names.Contains("include"));
    }

    [TestMethod]
    public async Task OutlineAsync_ComplexJsonFile()
    {
        var source = """
            {
                "name": "pudding-agent",
                "version": "2.0.0",
                "dependencies": {
                    "express": "^4.18.0",
                    "typescript": "^5.0.0"
                },
                "scripts": {
                    "build": "tsc",
                    "test": "jest"
                }
            }
            """;

        var result = await _outliner.OutlineAsync("package.json", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Count >= 5);
    }

    [TestMethod]
    public async Task OutlineAsync_EmptyObject_ReturnsEmpty()
    {
        var result = await _outliner.OutlineAsync("empty.json", "{}");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Nodes.Count);
    }

    [TestMethod]
    public async Task OutlineAsync_InvalidJson_ReturnsFallback()
    {
        var source = """
            {
                "name": "test"
                "missing": "comma"
            }
            """;

        var result = await _outliner.OutlineAsync("bad.json", source);

        // Should not crash, may return partial results or error
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task OutlineAsync_JsoncWithComments_CommentsStripped()
    {
        var source = """
            {
                // This is a comment
                "name": "test",
                "version": "1.0.0"
            }
            """;

        var result = await _outliner.OutlineAsync("config.jsonc", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Count >= 2);
    }
}
