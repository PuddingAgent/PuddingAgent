using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Yaml;

namespace PuddingCodeIntelligenceTests.Yaml;

[TestClass]
public class YamlFileOutlinerTests
{
    private readonly YamlFileOutliner _outliner = new();

    [TestMethod]
    public async Task OutlineAsync_SupportedExtensions_ReturnsYamlYml()
    {
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".yml"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".yaml"));
    }

    [TestMethod]
    public async Task OutlineAsync_SimpleDocument_TopLevelKeysExtracted()
    {
        var source = """
            name: my-app
            version: 1.0.0
            private: true
            """;

        var result = await _outliner.OutlineAsync("config.yml", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.Nodes.Count);

        Assert.AreEqual("name", result.Nodes[0].Name);
        Assert.AreEqual("version", result.Nodes[1].Name);
        Assert.AreEqual("private", result.Nodes[2].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_NestedKeys_ExtractedWithContainer()
    {
        var source = """
            server:
              host: localhost
              port: 8080
            database:
              type: postgres
              connection: jdbc:postgresql://localhost/mydb
            """;

        var result = await _outliner.OutlineAsync("app.yml", source);

        Assert.IsTrue(result.Success);
        var names = result.Nodes.Select(n => n.Name).ToList();
        Assert.IsTrue(names.Contains("server"));
        Assert.IsTrue(names.Contains("host"));
        Assert.IsTrue(names.Contains("port"));
        Assert.IsTrue(names.Contains("database"));
        Assert.IsTrue(names.Contains("type"));
    }

    [TestMethod]
    public async Task OutlineAsync_DockerCompose()
    {
        var source = """
            version: '3.8'
            services:
              web:
                image: nginx
                ports:
                  - "80:80"
              api:
                image: node:18
                environment:
                  - NODE_ENV=production
            volumes:
              db-data:
            """;

        var result = await _outliner.OutlineAsync("docker-compose.yml", source);

        Assert.IsTrue(result.Success);
        var names = result.Nodes.Select(n => n.Name).ToList();
        Assert.IsTrue(names.Contains("version"));
        Assert.IsTrue(names.Contains("services"));
        Assert.IsTrue(names.Contains("volumes"));
    }

    [TestMethod]
    public async Task OutlineAsync_MultipleDocuments_SeparatorsDetected()
    {
        var source = """
            ---
            name: doc1
            ---
            name: doc2
            """;

        var result = await _outliner.OutlineAsync("multi.yml", source);

        Assert.IsTrue(result.Success);
        var seps = result.Nodes.Where(n => n.Name == "---").ToList();
        Assert.AreEqual(2, seps.Count);
    }

    [TestMethod]
    public async Task OutlineAsync_CommentsIgnored()
    {
        var source = """
            # This is a comment
            name: test
            # Another comment
            version: 1.0.0
            """;

        var result = await _outliner.OutlineAsync("commented.yml", source);

        Assert.IsTrue(result.Success);
        var comments = result.Nodes.Where(n => n.Name.StartsWith('#')).ToList();
        Assert.AreEqual(0, comments.Count);
        Assert.AreEqual(2, result.Nodes.Count);
    }

    [TestMethod]
    public async Task OutlineAsync_EmptyDocument_ReturnsEmpty()
    {
        var result = await _outliner.OutlineAsync("empty.yml", "");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Nodes.Count);
    }
}
